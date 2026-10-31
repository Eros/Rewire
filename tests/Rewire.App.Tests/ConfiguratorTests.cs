using System.ComponentModel;
using PhysicalKey = Avalonia.Input.PhysicalKey;
using Rewire.Core.Keyboards;
using Rewire.Device.Snapshots;
using Rewire.Services;
using Rewire.ViewModels;

namespace Rewire.App.Tests;

public class ConfiguratorTests : IDisposable
{
    private const string Original = "[tab]>[q]\r\n";

    private static readonly KeyboardPack Advantage2 =
        KeyboardPackLoader.Load(Path.Combine(AppContext.BaseDirectory, "keyboards", "kinesis-advantage2"));

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "rewire-tests", Guid.NewGuid().ToString("N"));
    private readonly FakeKeyboard _keyboard = new();
    private readonly ProfileStore _store;
    private readonly ConfiguratorViewModel _configurator;

    public ConfiguratorTests()
    {
        _keyboard.Write("qwerty.txt", Original);
        _keyboard.Write("dvorak.txt", "");
        _keyboard.Write("state.txt", "startup_file=qwerty.txt\r\n");
        _store = new ProfileStore(Path.Combine(_directory, "rewire.db"));
        _configurator = new ConfiguratorViewModel(Advantage2, _store, _keyboard, new SnapshotStore(Path.Combine(_directory, "snapshots"), "test"));
        _configurator.Flow.ReturnTimeout = TimeSpan.FromSeconds(3);

        // Plays the user: whenever Rewire asks for Program+F1 to close the drive, press it.
        _configurator.Flow.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(DeviceFlowViewModel.Status) && _configurator.Flow.Status.StartsWith("Press Program+F1 once"))
                _keyboard.PressProgramF1();
        };
    }

    [Fact]
    public void Starts_empty_until_layouts_are_imported()
    {
        Assert.False(_configurator.HasProfiles);
    }

    [Fact]
    public async Task Imports_the_keyboard_layouts_and_shows_qwerty()
    {
        await _configurator.ImportCommand.ExecuteAsync(null);

        Assert.Equal(["qwerty.txt", "dvorak.txt"], _configurator.Profiles.Select(profile => profile.FileName));
        Assert.Equal("qwerty.txt", _configurator.SelectedProfile!.FileName);
        Assert.True(_configurator.Flow.Succeeded);
        Assert.False(_configurator.HasPendingChanges);
    }

    [Fact]
    public async Task Editing_a_key_changes_the_working_copy_only()
    {
        await ImportAndSelect("qwerty.txt", "caps");

        _configurator.ChooseActionCommand.Execute(new ActionOption("escape", "Esc"));

        Assert.Equal("escape", _configurator.Layout!.Find("base", "caps")!.Action);
        Assert.Equal(1, _configurator.PendingChangeCount);
        Assert.Equal(Original, _keyboard.Read("qwerty.txt"));
    }

    [Fact]
    public async Task Pressing_a_key_picks_its_action()
    {
        await ImportAndSelect("qwerty.txt", "caps");

        _configurator.BeginCaptureCommand.Execute(null);
        _configurator.KeyCaptured(PhysicalKey.ControlLeft);

        Assert.Equal("lctrl", _configurator.Layout!.Find("base", "caps")!.Action);
        Assert.False(_configurator.IsCapturing);
    }

    [Fact]
    public async Task Swaps_the_selected_key_with_the_next_one_clicked()
    {
        await ImportAndSelect("qwerty.txt", "bspace");

        _configurator.BeginSwapCommand.Execute(null);
        _configurator.SelectedKeyId = "delete";

        Assert.Equal("delete", _configurator.Layout!.Find("base", "bspace")!.Action);
        Assert.Equal("bspace", _configurator.Layout!.Find("base", "delete")!.Action);
    }

    [Fact]
    public async Task Resetting_the_only_change_leaves_nothing_to_apply()
    {
        await ImportAndSelect("qwerty.txt", "caps");
        _configurator.ChooseActionCommand.Execute(new ActionOption("escape", "Esc"));

        _configurator.ResetKeyCommand.Execute(null);

        Assert.False(_configurator.HasPendingChanges);
    }

    [Fact]
    public async Task Reviews_changes_in_plain_language_beside_the_raw_line()
    {
        await ImportAndSelect("qwerty.txt", "caps");
        _configurator.ChooseActionCommand.Execute(new ActionOption("escape", "Esc"));

        _configurator.BeginReviewCommand.Execute(null);

        var group = Assert.Single(_configurator.Review);
        var change = Assert.Single(group.Changes);
        Assert.Equal("QWERTY (qwerty.txt)", group.ProfileName);
        Assert.Equal("Caps → Esc", change.Description);
        Assert.Equal("+ [caps]>[escape]", change.Raw);
    }

    [Fact]
    public async Task Applies_changes_to_the_keyboard_and_records_a_snapshot()
    {
        await ImportAndSelect("qwerty.txt", "caps");
        _configurator.ChooseActionCommand.Execute(new ActionOption("escape", "Esc"));
        _configurator.BeginReviewCommand.Execute(null);

        await _configurator.ApplyCommand.ExecuteAsync(null);

        Assert.True(_configurator.Flow.Succeeded, _configurator.Flow.Status);
        Assert.Equal(Original + "[caps]>[escape]\r\n", _keyboard.Read("qwerty.txt"));
        Assert.False(_configurator.HasPendingChanges);
        Assert.Equal("Caps → Esc", Assert.Single(_configurator.History).Note);
        Assert.Equal(1, _keyboard.Ejects);
    }

    [Fact]
    public async Task Refuses_to_apply_over_changes_made_on_the_keyboard_since_import()
    {
        await ImportAndSelect("qwerty.txt", "caps");
        _configurator.ChooseActionCommand.Execute(new ActionOption("escape", "Esc"));
        _keyboard.Write("qwerty.txt", "[q]>[a]\r\n");

        await _configurator.ApplyCommand.ExecuteAsync(null);

        Assert.False(_configurator.Flow.Succeeded);
        Assert.Contains("has changed on the keyboard", Assert.Single(_configurator.Flow.Problems));
        Assert.Equal("[q]>[a]\r\n", _keyboard.Read("qwerty.txt"));
        Assert.True(_configurator.HasPendingChanges);
    }

    [Fact]
    public async Task Restores_a_snapshot_and_the_working_copy_with_it()
    {
        await ImportAndSelect("qwerty.txt", "caps");
        _configurator.ChooseActionCommand.Execute(new ActionOption("escape", "Esc"));
        await _configurator.ApplyCommand.ExecuteAsync(null);
        _keyboard.PressProgramF1();

        await _configurator.RestoreCommand.ExecuteAsync(_configurator.History.Single());

        Assert.True(_configurator.Flow.Succeeded, _configurator.Flow.Status);
        Assert.Equal(Original, _keyboard.Read("qwerty.txt"));
        Assert.Equal("q", _configurator.Layout!.Find("base", "tab")!.Action);
        Assert.Equal("caps", _configurator.Layout!.Find("base", "caps")!.Action);
        Assert.False(_configurator.HasPendingChanges);
    }

    public void Dispose()
    {
        _store.Dispose();
        _keyboard.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    private async Task ImportAndSelect(string fileName, string keyId)
    {
        await _configurator.ImportCommand.ExecuteAsync(null);
        _configurator.SelectedProfile = _configurator.Profiles.Single(profile => profile.FileName == fileName);
        _configurator.SelectedKeyId = keyId;
    }
}
