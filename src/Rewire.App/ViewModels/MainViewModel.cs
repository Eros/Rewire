using System.Reflection;
using CommunityToolkit.Mvvm.ComponentModel;
using Rewire.Core.Keyboards;
using Rewire.Core.Layouts;
using Rewire.Device.Platform;
using Rewire.Device.Snapshots;
using Rewire.Services;
using Rewire.Trainer;
using PhysicalKey = Avalonia.Input.PhysicalKey;

namespace Rewire.ViewModels;

/// <summary>The window: the configurator and the trainer, sharing one keyboard pack and one database.</summary>
public sealed partial class MainViewModel : ViewModelBase
{
    private const string PackId = "kinesis-advantage2";
    private const int TrainTab = 1;

    public MainViewModel()
        : this(Create())
    {
    }

    public MainViewModel(ConfiguratorViewModel configurator, TrainerViewModel trainer)
    {
        Configurator = configurator;
        Trainer = trainer;
    }

    private MainViewModel((ConfiguratorViewModel Configurator, TrainerViewModel Trainer) halves)
        : this(halves.Configurator, halves.Trainer)
    {
    }

    private static (ConfiguratorViewModel, TrainerViewModel) Create()
    {
        var packDirectory = Path.Combine(AppContext.BaseDirectory, "keyboards", PackId);
        var pack = KeyboardPackLoader.Load(packDirectory);
        var profiles = new ProfileStore(ProfileStore.DefaultPath);
        var version = Assembly.GetEntryAssembly()?.GetName().Version?.ToString() ?? "unknown";

        return (
            new ConfiguratorViewModel(pack, profiles, DrivePlatform.ForCurrentOs(), new SnapshotStore(SnapshotStore.DefaultDirectory, version),
                Preset.Load(packDirectory)),
            new TrainerViewModel(pack, Curriculum.Load(packDirectory), profiles, new TrainingStore(ProfileStore.DefaultPath),
                content: LoadTrainerContent()));
    }

    private static TrainerContent LoadTrainerContent()
    {
        var keyboards = KeyboardPackLoader.LoadAll(Path.Combine(AppContext.BaseDirectory, "keyboards"));
        var platform = OperatingSystem.IsMacOS() ? "macos" : "windows";
        return new TrainerContent(
            keyboards.Where(board => board.Backend is null).ToList(),
            CodeTemplates.Load(Path.Combine(AppContext.BaseDirectory, "templates")),
            ShortcutPack.Load(Path.Combine(AppContext.BaseDirectory, "shortcuts"), platform));
    }

    public ConfiguratorViewModel Configurator { get; }

    public TrainerViewModel Trainer { get; }

    [ObservableProperty]
    public partial int SelectedTab { get; set; }

    /// <summary>Gives a key press to whichever half is listening for one.</summary>
    /// <returns>True when the press was used and should not reach any other control.</returns>
    public bool KeyPressed(PhysicalKey key, Avalonia.Input.KeyModifiers modifiers = Avalonia.Input.KeyModifiers.None)
    {
        if (SelectedTab == TrainTab)
            return Trainer.KeyPressed(key, modifiers);

        return Configurator.KeyPressed(key);
    }

    public bool KeyReleased(PhysicalKey key) => SelectedTab != TrainTab && Configurator.KeyReleased(key);

    partial void OnSelectedTabChanged(int value)
    {
        if (value == TrainTab && !Trainer.IsBusy)
            Trainer.Refresh();
    }
}
