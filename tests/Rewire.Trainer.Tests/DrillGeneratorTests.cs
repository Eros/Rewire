using static Rewire.Trainer.Tests.TrainerFixtures;

namespace Rewire.Trainer.Tests;

public class DrillGeneratorTests
{
    [Fact]
    public void Loads_the_five_stages_in_order()
    {
        Assert.Equal(["baseline", "home", "columns", "thumbs", "awkward"], Stages.Select(stage => stage.Id));
        Assert.Equal(TimeSpan.FromMinutes(2), Stage("baseline").TimeLimit);
    }

    [Fact]
    public void A_home_row_drill_uses_only_home_row_keys_and_space()
    {
        var drill = new DrillGenerator(Factory, Typable, new Random(1)).Generate(Stage("home"), []);

        var homeRow = Stage("home").Keys.Append("space").ToHashSet();
        Assert.NotEmpty(drill.Targets);
        Assert.All(drill.Targets, target => Assert.Contains(target.KeyId, homeRow));
    }

    [Fact]
    public void Drills_a_remapped_key_where_it_now_is()
    {
        // George's layout swaps Backspace and Space on the thumbs, so spaces are typed with the left thumb key.
        var drill = new DrillGenerator(Georges, Typable, new Random(1)).Generate(Stage("home"), []);

        var space = drill.Targets.First(target => target.Display == " ");
        Assert.Equal("bspace", space.KeyId);
        Assert.Equal("space", space.Action);
    }

    [Fact]
    public void Leaves_out_keys_whose_action_never_reaches_the_computer()
    {
        // Home and Page Up send keypad shift in George's layout.
        var drill = new DrillGenerator(Georges, Typable, new Random(1)).Generate(Stage("thumbs"), []);

        Assert.DoesNotContain(drill.Targets, target => target.KeyId is "home" or "pup");
        Assert.Contains(drill.Targets, target => target is { KeyId: "delete", Action: "escape", IsCharacter: false });
    }

    [Fact]
    public void A_thumb_drill_includes_every_thumb_command()
    {
        var drill = new DrillGenerator(Factory, Typable, new Random(1)).Generate(Stage("thumbs"), Stage("columns").Keys);

        var commands = drill.Targets.Where(target => !target.IsCharacter).Select(target => target.KeyId).ToHashSet();
        Assert.Superset(new HashSet<string> { "bspace", "delete", "enter", "home", "end" }, commands);
    }

    [Fact]
    public void A_column_drill_starts_down_the_left_pinky_column()
    {
        var drill = new DrillGenerator(Factory, Typable, new Random(1)).Generate(Stage("columns"), []);

        Assert.Equal("1qaz", string.Concat(drill.Targets.Take(4).Select(target => target.Display)));
    }

    [Fact]
    public void An_awkward_keys_drill_types_the_brackets()
    {
        var drill = new DrillGenerator(Factory, Typable, new Random(1)).Generate(Stage("awkward"), Stage("columns").Keys);

        Assert.Contains(drill.Targets, target => target.KeyId == "obrack");
        Assert.Contains(drill.Targets, target => target.KeyId == "left" && !target.IsCharacter);
    }

    [Theory]
    [InlineData("baseline")]
    [InlineData("home")]
    [InlineData("columns")]
    [InlineData("thumbs")]
    public void Never_shows_the_same_word_twice_in_a_row(string stageId)
    {
        for (var seed = 0; seed < 50; seed++)
        {
            var drill = new DrillGenerator(Factory, Typable, new Random(seed)).Generate(Stage(stageId), Stage("columns").Keys);
            var words = string.Concat(drill.Targets.Select(target => target.IsCharacter ? target.Display : " "))
                .Split(' ', StringSplitOptions.RemoveEmptyEntries);

            for (var i = 1; i < words.Length; i++)
                Assert.False(words[i] == words[i - 1], $"Seed {seed} repeats \"{words[i]}\" at word {i}.");
        }
    }

    [Fact]
    public void Finds_the_key_that_sends_an_action()
    {
        var generator = new DrillGenerator(Georges, Typable);

        Assert.Equal("bspace", generator.KeyFor("space"));
        Assert.Equal("space", generator.KeyFor("bspace"));
        Assert.Equal("a", generator.KeyFor("A"));
    }
}

public class TimedTestTests
{
    [Fact]
    public void Offers_four_lengths_with_readable_names()
    {
        Assert.Equal(["30 seconds", "1 minute", "2 minutes", "5 minutes"], TimedTest.Durations.Select(TimedTest.Name));
    }

    [Fact]
    public void A_timed_test_is_a_long_run_of_words_with_no_repeats()
    {
        var drill = new DrillGenerator(TrainerFixtures.Factory, TrainerFixtures.Typable, new Random(4)).Generate(TimedTest.Stage(60), []);
        var words = string.Concat(drill.Targets.Select(target => target.Display)).Split(' ');

        Assert.True(words.Length > 300);
        Assert.Equal(TimeSpan.FromMinutes(1), drill.Stage.TimeLimit);
        Assert.All(words.Zip(words.Skip(1)), pair => Assert.NotEqual(pair.First, pair.Second));
    }
}
