using Avalonia.Input;
using Rewire.Core.Keyboards;
using Rewire.Services;
using Rewire.Trainer;
using Rewire.ViewModels;
using PhysicalKey = Avalonia.Input.PhysicalKey;

namespace Rewire.App.Tests;

public class SmartTrainerTests : IDisposable
{
    private static readonly string Root = AppContext.BaseDirectory;
    private static readonly string PackDirectory = Path.Combine(Root, "keyboards", "kinesis-advantage2");
    private static readonly KeyboardPack Advantage2 = KeyboardPackLoader.Load(PackDirectory);

    private static readonly Dictionary<string, PhysicalKey> KeysByAction = Enum.GetValues<PhysicalKey>()
        .Select(key => (key, action: KeyCapture.ActionFor(key)))
        .Where(pair => pair.action is not null)
        .GroupBy(pair => pair.action!, StringComparer.OrdinalIgnoreCase)
        .ToDictionary(group => group.Key, group => group.First().key, StringComparer.OrdinalIgnoreCase);

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "rewire-tests", Guid.NewGuid().ToString("N"));
    private readonly ProfileStore _profiles;
    private readonly TrainingStore _training;
    private readonly TrainerViewModel _trainer;

    public SmartTrainerTests()
    {
        var database = Path.Combine(_directory, "rewire.db");
        _profiles = new ProfileStore(database);
        _training = new TrainingStore(database);
        var content = new TrainerContent(
            KeyboardPackLoader.LoadAll(Path.Combine(Root, "keyboards")).Where(board => board.Backend is null).ToList(),
            CodeTemplates.Load(Path.Combine(Root, "templates")),
            ShortcutPack.Load(Path.Combine(Root, "shortcuts"), "macos"));
        _trainer = new TrainerViewModel(Advantage2, Curriculum.Load(PackDirectory), _profiles, _training, content: content);
    }

    [Fact]
    public void Colours_keywords_differently_from_names()
    {
        var colours = new CodeHighlighter(dark: false).Colours("csharp", "public class Counter");

        Assert.NotNull(colours[0]);
        Assert.NotEqual(colours[0], colours["public class ".Length]);
    }

    [Fact]
    public void Runs_a_code_drill_to_the_end_and_saves_it()
    {
        _trainer.Mode = 1;
        _trainer.Code.SelectedLanguage = "python";
        _trainer.Code.StartCommand.Execute(null);

        while (_trainer.Code.Session!.Current is { } target)
            _trainer.KeyPressed(KeysByAction[target.Action]);

        Assert.False(_trainer.Code.IsRunning);
        Assert.StartsWith("100% accuracy", _trainer.Code.Result);
        Assert.Single(_training.Sessions(CodeTemplates.StageId("python", 1)));
        Assert.All(_trainer.Code.Lines.SelectMany(line => line.Cells), cell => Assert.True(cell.IsDone));
    }

    [Fact]
    public void Judges_a_shortcut_by_its_key_and_modifiers()
    {
        _trainer.Mode = 2;
        _trainer.Shortcuts.SelectedPack = _trainer.Shortcuts.Packs.Single(pack => pack.Name == "macOS editing");
        _trainer.Shortcuts.StartCommand.Execute(null);
        var first = _trainer.Shortcuts.Session!.Current!;

        // Pressing a modifier on its own is not an answer.
        _trainer.KeyPressed(PhysicalKey.MetaLeft, KeyModifiers.Meta);
        Assert.Equal(0, _trainer.Shortcuts.Session.Position);

        Press(first.Chord);

        Assert.Equal(1, _trainer.Shortcuts.Session.Position);
        Assert.Equal("", _trainer.Shortcuts.Feedback);
    }

    [Fact]
    public void Explains_a_mistake_that_looks_like_an_old_habit()
    {
        // A one-stage curriculum, which is always open, that drills only Backspace.
        var backspace = new Stage("backspace", "Backspace", "", DrillKind.Commands, ["bspace"], 0, null);
        var content = new TrainerContent(
            KeyboardPackLoader.LoadAll(Path.Combine(Root, "keyboards")).Where(board => board.Backend is null).ToList(), [], []);
        var trainer = new TrainerViewModel(Advantage2, [backspace], _profiles, _training, content: content);
        trainer.SelectedOldBoard = trainer.OldBoards.Single(board => board.Id == "apple-macbook-us");
        trainer.StartCommand.Execute(null);

        // On a MacBook, Backspace sits top right, where the Advantage2 has the hyphen key.
        trainer.KeyPressed(PhysicalKey.Minus);

        Assert.Equal("Old habit: you reached for Bksp where it is on your old keyboard. Here it's under your left thumb.", trainer.Hint);

        trainer.KeyPressed(PhysicalKey.Backspace);
        Assert.Null(trainer.Hint);
    }

    [Fact]
    public void Remembers_the_chosen_old_keyboard()
    {
        _trainer.SelectedOldBoard = _trainer.OldBoards.Single(board => board.Id == "ansi-us");

        Assert.Equal("ansi-us", _training.Setting("old_keyboard"));
    }

    public void Dispose()
    {
        _profiles.Dispose();
        _training.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    private void Press(string chord)
    {
        var parts = chord.Split('+');
        var modifiers = KeyModifiers.None;
        foreach (var part in parts[..^1])
        {
            modifiers |= part switch
            {
                "cmd" => KeyModifiers.Meta,
                "ctrl" => KeyModifiers.Control,
                "alt" => KeyModifiers.Alt,
                "shift" => KeyModifiers.Shift,
                _ => KeyModifiers.None,
            };
        }

        _trainer.KeyPressed(KeysByAction[parts[^1]], modifiers);
    }

}
