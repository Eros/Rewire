using static Rewire.Trainer.Tests.TrainerFixtures;

namespace Rewire.Trainer.Tests;

public class TypingSessionTests
{
    private readonly ManualClock _clock = new();

    private TypingSession Session(string text, string stageId = "home")
    {
        var targets = text.Select(character => new DrillTarget(
            character == ' ' ? "space" : character.ToString(),
            CharacterActions.ActionFor(character)!,
            character.ToString(),
            IsCharacter: true)).ToList();
        return new TypingSession(new Drill(Stage(stageId), targets), action => action, _clock);
    }

    [Fact]
    public void Finishes_when_every_target_is_typed()
    {
        var session = Session("as");

        Assert.Equal(PressResult.Correct, session.Press("a"));
        Assert.Equal(PressResult.Correct, session.Press("s"));

        Assert.True(session.IsComplete);
        Assert.Equal(1, session.Accuracy);
    }

    [Fact]
    public void A_wrong_key_is_recorded_and_the_cursor_waits()
    {
        var session = Session("as");

        Assert.Equal(PressResult.Wrong, session.Press("d"));

        Assert.Equal(0, session.Position);
        Assert.Contains(0, session.Missed);
        var keystroke = Assert.Single(session.Keystrokes);
        Assert.Equal(("a", "d", false), (keystroke.IntendedKeyId, keystroke.PressedKeyId, keystroke.Correct));
    }

    [Fact]
    public void Accuracy_counts_every_judged_press()
    {
        var session = Session("as");

        session.Press("d");
        session.Press("a");
        session.Press("s");

        Assert.Equal(2.0 / 3, session.Accuracy, precision: 6);
    }

    [Fact]
    public void A_modifier_pressed_on_its_own_is_not_judged()
    {
        var session = Session("a");

        Assert.Equal(PressResult.Ignored, session.Press("lshift"));

        Assert.Empty(session.Keystrokes);
    }

    [Fact]
    public void Measures_latency_from_the_previous_correct_press()
    {
        var session = Session("asd");
        session.Press("a");
        _clock.Advance(TimeSpan.FromMilliseconds(300));
        session.Press("x");
        _clock.Advance(TimeSpan.FromMilliseconds(200));
        session.Press("s");

        Assert.Equal([0, 300, 500], session.Keystrokes.Select(keystroke => (int)keystroke.Latency.TotalMilliseconds));
    }

    [Fact]
    public void Counts_five_targets_as_a_word()
    {
        var session = Session("asdfg asdf");
        foreach (var target in session.Targets)
        {
            session.Press(target.Action);
            _clock.Advance(TimeSpan.FromSeconds(1));
        }

        // Ten targets typed over nine seconds: two words in 0.15 minutes.
        Assert.Equal(2 / 0.15, session.WordsPerMinute, precision: 6);
    }

    [Fact]
    public void A_timed_drill_ends_at_its_time_limit()
    {
        var session = Session("as as as", "baseline");
        session.Press("a");
        _clock.Advance(TimeSpan.FromMinutes(3));

        Assert.True(session.CheckTime());
        Assert.Equal(TimeSpan.FromMinutes(2), session.Elapsed);
        Assert.Equal(PressResult.Ignored, session.Press("s"));
    }
}
