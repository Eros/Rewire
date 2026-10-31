using System.Text;
using Rewire.Core.Keyboards;
using Rewire.Core.Layouts;
using Rewire.Device.Snapshots;
using Rewire.Services;
using Rewire.ViewModels;
using PhysicalKey = Avalonia.Input.PhysicalKey;

namespace Rewire.App.Tests;

public class MacroEditorTests : IDisposable
{
    private static readonly string PackDirectory = Path.Combine(AppContext.BaseDirectory, "keyboards", "kinesis-advantage2");
    private static readonly KeyboardPack Advantage2 = KeyboardPackLoader.Load(PackDirectory);

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "rewire-tests", Guid.NewGuid().ToString("N"));
    private readonly FakeKeyboard _keyboard = new();
    private readonly ProfileStore _store;
    private readonly ConfiguratorViewModel _configurator;

    public MacroEditorTests()
    {
        _store = new ProfileStore(Path.Combine(_directory, "rewire.db"));
        _store.Import(new Dictionary<string, byte[]> { ["qwerty.txt"] = "{rctrl}{g}>{g}{o}\r\n[caps]>[escape]\r\n"u8.ToArray() });
        _configurator = new ConfiguratorViewModel(Advantage2, _store, _keyboard, new SnapshotStore(Path.Combine(_directory, "snapshots"), "test"),
            Preset.Load(PackDirectory));
    }

    private string Working => Encoding.Latin1.GetString(_store.Find("qwerty.txt")!.Working);

    [Fact]
    public void Lists_the_macros_a_key_triggers()
    {
        _configurator.SelectedKeyId = "g";

        var macro = Assert.Single(_configurator.KeyMacros);
        Assert.Equal("Ctrl + G", macro.Trigger);
        Assert.Equal("go", macro.Steps);
    }

    [Fact]
    public void Summarises_a_macro_as_the_text_it_types()
    {
        _store.SaveWorking("qwerty.txt", "{kp-w}>{speed5}{-lshift}{9}{0}{+lshift}{=}{-lshift}{.}{obrack}{+lshift}\r\n"u8.ToArray());
        var configurator = new ConfiguratorViewModel(Advantage2, _store, _keyboard, new SnapshotStore(Path.Combine(_directory, "snapshots"), "test"));
        configurator.SelectedLayer = configurator.Layers[1];
        configurator.SelectedKeyId = "w";

        Assert.Equal("()=>{", Assert.Single(configurator.KeyMacros).Steps);
    }

    [Fact]
    public void Turns_typed_text_into_keys_holding_shift_where_needed()
    {
        var editor = NewMacroOn("h");

        editor.Text = "Hi!";
        editor.AddTextCommand.Execute(null);

        Assert.Equal("{h}>{-lshift}{h}{+lshift}{i}{-lshift}{1}{+lshift}", editor.Build().Render());
        Assert.True(editor.CanSave);
    }

    [Fact]
    public void Records_modifiers_as_holds_and_releases()
    {
        var editor = NewMacroOn("h");

        editor.ToggleRecordingCommand.Execute(null);
        editor.KeyDown(PhysicalKey.MetaLeft);
        editor.KeyDown(PhysicalKey.S);
        editor.KeyUp(PhysicalKey.S);
        editor.KeyUp(PhysicalKey.MetaLeft);
        editor.ToggleRecordingCommand.Execute(null);

        Assert.Equal(["L Win ↓", "S", "L Win ↑"], editor.Steps.Select(step => step.Label));
    }

    [Fact]
    public void Saves_a_new_macro_with_its_speed_and_modifiers()
    {
        var editor = NewMacroOn("h");
        editor.Modifiers.Single(modifier => modifier.Location == "lctrl").IsOn = true;
        editor.Speed = editor.SpeedOptions[9];
        editor.Text = "hey";
        editor.AddTextCommand.Execute(null);

        _configurator.SaveMacroCommand.Execute(null);

        Assert.EndsWith("{lctrl}{h}>{speed9}{h}{e}{y}\r\n", Working);
        Assert.Null(_configurator.MacroEditor);
        Assert.Equal("Ctrl + H", Assert.Single(_configurator.KeyMacros).Trigger);
    }

    [Fact]
    public void Editing_a_macro_keeps_its_place_in_the_file()
    {
        _configurator.SelectedKeyId = "g";
        _configurator.EditMacroCommand.Execute(_configurator.KeyMacros.Single());
        _configurator.MacroEditor!.ClearStepsCommand.Execute(null);
        _configurator.MacroEditor.Text = "git";
        _configurator.MacroEditor.AddTextCommand.Execute(null);

        _configurator.SaveMacroCommand.Execute(null);

        Assert.Equal("{rctrl}{g}>{g}{i}{t}\r\n[caps]>[escape]\r\n", Working);
    }

    [Fact]
    public void Will_not_save_a_macro_with_nothing_to_play()
    {
        var editor = NewMacroOn("h");

        Assert.False(editor.CanSave);
        Assert.Contains(editor.Problems, problem => problem.Message.Contains("plays nothing"));
    }

    [Fact]
    public void Warns_when_the_key_already_has_a_remap()
    {
        var editor = NewMacroOn("caps");
        editor.Text = "x";
        editor.AddTextCommand.Execute(null);

        Assert.Contains(editor.Problems, problem => problem.Message.Contains("the macro takes priority"));
        Assert.True(editor.CanSave);
    }

    [Fact]
    public void Applies_and_removes_a_preset()
    {
        var brackets = _configurator.Presets.Single(item => item.Preset.Name == "Brackets on the thumbs");
        _configurator.ApplyPresetCommand.Execute(brackets);

        Assert.Contains("[home]>[obrack]", Working);
        Assert.Equal(PresetState.Applied, _configurator.Presets.Single(item => item.Preset.Name == "Brackets on the thumbs").State);

        _configurator.RemovePresetCommand.Execute(_configurator.Presets.Single(item => item.Preset.Name == "Brackets on the thumbs"));

        Assert.DoesNotContain("[home]", Working);
    }

    [Fact]
    public void Warns_before_a_preset_replaces_an_existing_remap()
    {
        var esc = _configurator.Presets.Single(item => item.Preset.Name == "Esc on Caps Lock");
        Assert.Equal(PresetState.Applied, esc.State);

        _configurator.SelectedKeyId = "caps";
        _configurator.ChooseActionCommand.Execute(new ActionOption("lctrl", "Ctrl"));

        Assert.Equal("Replaces your remap of Caps (Ctrl).", _configurator.Presets.Single(item => item.Preset.Name == "Esc on Caps Lock").Warning);
    }

    public void Dispose()
    {
        _store.Dispose();
        _keyboard.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    private MacroEditorViewModel NewMacroOn(string keyId)
    {
        _configurator.SelectedKeyId = keyId;
        _configurator.NewMacroCommand.Execute(null);
        return _configurator.MacroEditor!;
    }
}
