using Rewire.Core.Keyboards;
using static Rewire.Trainer.Tests.TrainerFixtures;

namespace Rewire.Trainer.Tests;

public class OldHabitDetectorTests
{
    private static readonly OldHabitDetector FromMacBook = new(
        KeyboardPackLoader.Load(Path.Combine(AppContext.BaseDirectory, "keyboards", "apple-macbook-us")), Advantage2);

    [Theory]
    [InlineData("bspace", "hyphen")]
    [InlineData("enter", "'")]
    [InlineData("`", "=")]
    [InlineData("=", "hyphen")]
    public void Repeats_the_old_reach_on_the_new_board(string action, string landing)
    {
        Assert.Equal(landing, FromMacBook.OldPositionOf(action));
    }

    [Theory]
    [InlineData("a")]
    [InlineData("b")]
    [InlineData("tab")]
    [InlineData("lshift")]
    public void Keys_in_the_same_place_land_on_themselves(string action)
    {
        Assert.Equal(action, FromMacBook.OldPositionOf(action));
    }

    [Fact]
    public void Tags_a_press_where_the_intended_key_used_to_be()
    {
        Assert.Equal("bspace", FromMacBook.Classify("bspace", "bspace", "hyphen"));
    }

    [Fact]
    public void Does_not_tag_other_mistakes()
    {
        Assert.Null(FromMacBook.Classify("bspace", "bspace", "p"));
        Assert.Null(FromMacBook.Classify("f", "f", "g"));
        Assert.Null(FromMacBook.Classify("bspace", "bspace", null));
    }

    [Fact]
    public void Ignores_actions_the_old_board_does_not_have()
    {
        Assert.Null(FromMacBook.OldPositionOf("kpshift"));
    }

    [Fact]
    public void A_session_records_the_old_habit_with_the_wrong_press()
    {
        var target = new DrillTarget("bspace", "bspace", "Bksp", IsCharacter: false);
        var session = new TypingSession(new Drill(Stage("thumbs"), [target]), action => action == "hyphen" ? "hyphen" : action, oldHabits: FromMacBook);

        session.Press("hyphen");

        Assert.Equal("bspace", session.LastKeystroke!.OldHabit);
    }
}

public class SkillScoresTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "rewire-tests", Guid.NewGuid().ToString("N"));
    private readonly ManualClock _clock = new();
    private readonly TrainingStore _store;

    public SkillScoresTests()
    {
        _store = new TrainingStore(Path.Combine(_directory, "rewire.db"));
    }

    [Fact]
    public void A_key_often_missed_needs_more_practice()
    {
        Finish("asasasasas", mistakesOn: 'a');

        var weights = _store.Scores.Weights(_clock.GetUtcNow());

        Assert.True(weights.Key("a") > weights.Key("s"));
        Assert.True(weights.Key("a") > 1);
    }

    [Fact]
    public void A_mastered_key_fades_until_it_is_due_again()
    {
        Finish("ssssssssss");

        Assert.True(_store.Scores.Weights(_clock.GetUtcNow()).Key("s") < 1);
        _clock.Advance(TimeSpan.FromDays(2));
        Assert.True(_store.Scores.Weights(_clock.GetUtcNow()).Key("s") >= 1);
    }

    [Fact]
    public void Records_pairs_of_keys()
    {
        Finish("asasasasas", mistakesOn: 's');

        var weights = _store.Scores.Weights(_clock.GetUtcNow());

        Assert.True(weights.Bigram("a", "s") > weights.Bigram("s", "a"));
    }

    [Fact]
    public void The_first_layout_seen_is_recorded_without_queuing_anything()
    {
        Assert.Empty(_store.Scores.NoteLayout(new Dictionary<string, string> { ["caps"] = "caps" }, _clock.GetUtcNow()));
        Assert.Empty(_store.Scores.Relearning());
    }

    [Fact]
    public void A_remapped_key_is_queued_for_relearning_until_practised()
    {
        _store.Scores.NoteLayout(new Dictionary<string, string> { ["caps"] = "caps", ["a"] = "a" }, _clock.GetUtcNow());

        var changed = _store.Scores.NoteLayout(new Dictionary<string, string> { ["caps"] = "escape", ["a"] = "a" }, _clock.GetUtcNow());

        Assert.Equal(["caps"], changed);
        Assert.Equal(["caps"], _store.Scores.Relearning());
        Assert.Equal(3, _store.Scores.Weights(_clock.GetUtcNow()).Key("caps"));
    }

    [Fact]
    public void Weak_keys_come_up_more_often_in_drills()
    {
        var weights = new SkillWeights(new Dictionary<string, double> { ["f"] = 10 }, new Dictionary<string, double>());
        var plain = new DrillGenerator(Factory, Typable, new Random(3)).Generate(Stage("home"), []);
        var weighted = new DrillGenerator(Factory, Typable, new Random(3)) { Weights = weights }.Generate(Stage("home"), []);

        Assert.True(weighted.Targets.Count(target => target.KeyId == "f") > plain.Targets.Count(target => target.KeyId == "f"));
    }

    public void Dispose()
    {
        _store.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    private void Finish(string text, char? mistakesOn = null)
    {
        var targets = text.Select(character => new DrillTarget(character.ToString(), character.ToString(), character.ToString(), true)).ToList();
        var session = new TypingSession(new Drill(Stage("home"), targets), action => action, _clock);
        foreach (var target in targets)
        {
            if (target.Display[0] == mistakesOn)
                session.Press("q");
            session.Press(target.Action);
            _clock.Advance(TimeSpan.FromMilliseconds(250));
        }

        _store.Save(session);
    }
}

public class CodeTemplateTests
{
    private static readonly IReadOnlyList<CodeTemplate> Templates = CodeTemplates.Load(Path.Combine(AppContext.BaseDirectory, "templates"));

    private static CodeDrill Build(string text) =>
        CodeTemplates.Build(new CodeTemplate("csharp", 1, "test.cs", text), new DrillGenerator(Factory, Typable).KeyFor, "Enter");

    [Fact]
    public void Loads_templates_for_every_launch_language()
    {
        Assert.Equal(["cpp", "csharp", "go", "java", "javascript", "python", "rust", "typescript"],
            Templates.Select(template => template.Language).Distinct().Order());
        Assert.All(Templates, template => Assert.InRange(template.Level, 1, CodeTemplates.Levels));
    }

    [Fact]
    public void Skips_indentation_and_ends_each_line_with_enter()
    {
        var code = Build("if (x)\n    y();\n");

        Assert.Equal("if (x)⏎y();", string.Concat(code.Drill.Targets.Select(target => target.IsCharacter ? target.Display : "⏎")));
        Assert.Equal([0, 4], code.Lines.Select(line => line.Indent));
    }

    [Fact]
    public void Types_shifted_characters_with_their_unshifted_key()
    {
        var code = Build("(A)");

        Assert.Equal(["9", "a", "0"], code.Drill.Targets.Select(target => target.KeyId));
    }

    [Fact]
    public void Remembers_where_each_character_came_from_for_colouring()
    {
        var code = Build("a\n\n  bc");

        Assert.Equal([0, -1, 5, 6], code.Offsets);
    }

    [Fact]
    public void Every_template_can_be_typed_on_the_factory_layout()
    {
        var keys = new DrillGenerator(Factory, Typable).KeyFor;
        foreach (var template in Templates)
        {
            var typable = template.Text.Where(character => !char.IsWhiteSpace(character));
            Assert.All(typable, character => Assert.NotNull(CharacterActions.ActionFor(character) is { } action ? keys(action) : null));
        }
    }
}

public class ShortcutTests
{
    [Theory]
    [InlineData("shift+cmd+A", "shift+cmd+a")]
    [InlineData("cmd+shift+a", "shift+cmd+a")]
    [InlineData("alt+ctrl+o", "ctrl+alt+o")]
    [InlineData("f7", "f7")]
    public void Puts_modifiers_in_one_order(string chord, string normalised)
    {
        Assert.Equal(normalised, Chords.Normalise(chord));
    }

    [Fact]
    public void Loads_only_the_packs_for_this_platform()
    {
        var packs = ShortcutPack.Load(Path.Combine(AppContext.BaseDirectory, "shortcuts"), "macos");

        Assert.Equal(["IntelliJ IDEA", "macOS editing"], packs.Select(pack => pack.Name));
        Assert.DoesNotContain(packs.SelectMany(pack => pack.Shortcuts), shortcut => shortcut.Chord is "cmd+q" or "cmd+h" or "cmd+w");
    }

    [Fact]
    public void Reveals_the_answer_after_two_misses_and_scores_first_attempts()
    {
        var pack = new ShortcutPack("test", "Test", "macos", [new Shortcut("Copy", "cmd+c"), new Shortcut("Paste", "cmd+v")]);
        var session = new ShortcutSession(pack, pack.Shortcuts);

        Assert.Equal(ShortcutResult.Wrong, session.Press("cmd+x"));
        Assert.False(session.IsRevealed);
        session.Press("cmd+z");
        Assert.True(session.IsRevealed);
        Assert.Equal(ShortcutResult.Correct, session.Press("cmd+c"));
        Assert.Equal(ShortcutResult.Correct, session.Press("CMD+V"));

        Assert.True(session.IsComplete);
        Assert.Equal(0.5, session.Accuracy);
    }
}

public class CodeLevelTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "rewire-tests", Guid.NewGuid().ToString("N"));
    private readonly TrainingStore _store;

    public CodeLevelTests()
    {
        _store = new TrainingStore(Path.Combine(_directory, "rewire.db"));
    }

    [Fact]
    public void Opens_the_next_level_after_three_accurate_templates()
    {
        var clock = new ManualClock();
        var code = CodeTemplates.Build(new CodeTemplate("csharp", 1, "x.cs", "var x;"), new DrillGenerator(Factory, Typable).KeyFor, "Enter");

        for (var run = 0; run < 3; run++)
        {
            Assert.Equal(StageState.Locked, _store.CodeLevels("csharp")[1]);
            var session = new TypingSession(code.Drill, action => action, clock);
            foreach (var target in code.Drill.Targets)
            {
                session.Press(target.Action);
                clock.Advance(TimeSpan.FromMilliseconds(300));
            }

            _store.Save(session);
            clock.Advance(TimeSpan.FromMinutes(1));
        }

        Assert.Equal([StageState.Passed, StageState.Open, StageState.Locked, StageState.Locked, StageState.Locked], _store.CodeLevels("csharp"));
    }

    public void Dispose()
    {
        _store.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }
}
