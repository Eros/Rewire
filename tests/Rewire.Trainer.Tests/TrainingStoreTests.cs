using static Rewire.Trainer.Tests.TrainerFixtures;

namespace Rewire.Trainer.Tests;

public class TrainingStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "rewire-tests", Guid.NewGuid().ToString("N"));
    private readonly ManualClock _clock = new();
    private readonly TrainingStore _store;

    public TrainingStoreTests()
    {
        _store = new TrainingStore(Path.Combine(_directory, "rewire.db"));
    }

    [Fact]
    public void Only_the_baseline_is_open_at_first()
    {
        var progress = _store.Progress(Stages);

        Assert.Equal([StageState.Open, StageState.Locked, StageState.Locked, StageState.Locked, StageState.Locked],
            progress.Select(status => status.State));
    }

    [Fact]
    public void A_baseline_opens_the_home_row_and_sets_its_speed()
    {
        Finish("baseline", "asdf asdf asdf asdf", secondsPerKey: 0.2);

        var progress = _store.Progress(Stages);

        Assert.Equal(StageState.Passed, progress[0].State);
        Assert.Equal(StageState.Open, progress[1].State);
        Assert.Equal(_store.Baseline()!.WordsPerMinute * 0.4, progress[1].RequiredWordsPerMinute!.Value, precision: 6);
    }

    [Fact]
    public void A_stage_passes_at_95_percent_accuracy_and_enough_speed()
    {
        Finish("baseline", "asdf asdf asdf asdf", secondsPerKey: 0.2);
        Finish("home", "asdf asdf asdf asdf", secondsPerKey: 0.4);

        var progress = _store.Progress(Stages);

        Assert.Equal(StageState.Passed, progress[1].State);
        Assert.Equal(StageState.Open, progress[2].State);
    }

    [Fact]
    public void A_slow_session_does_not_pass()
    {
        Finish("baseline", "asdf asdf asdf asdf", secondsPerKey: 0.2);
        Finish("home", "asdf asdf asdf asdf", secondsPerKey: 1);

        Assert.Equal(StageState.Open, _store.Progress(Stages)[1].State);
    }

    [Fact]
    public void An_inaccurate_session_does_not_pass()
    {
        Finish("baseline", "asdf asdf asdf asdf", secondsPerKey: 0.2);
        Finish("home", "asdf asdf asdf asdf", secondsPerKey: 0.3, mistakes: 3);

        Assert.Equal(StageState.Open, _store.Progress(Stages)[1].State);
    }

    [Fact]
    public void Reports_error_rate_and_speed_per_key_without_the_baseline()
    {
        Finish("baseline", "jjjj", secondsPerKey: 0.1);
        Finish("home", "as", secondsPerKey: 0.5, mistakes: 1);

        var statistics = _store.KeyStatistics(DateTimeOffset.MinValue).ToDictionary(key => key.KeyId);

        Assert.DoesNotContain("j", statistics.Keys);
        Assert.Equal(0.5, statistics["a"].ErrorRate);
        Assert.Equal(0, statistics["s"].ErrorRate);
        Assert.Equal(TimeSpan.FromMilliseconds(500), statistics["s"].MeanLatency);
    }

    public void Dispose()
    {
        _store.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    // Types the text, pressing a wrong key before each of the first few targets.
    private void Finish(string stageId, string text, double secondsPerKey, int mistakes = 0)
    {
        var targets = text.Select(character => new DrillTarget(
            character == ' ' ? "space" : character.ToString(), CharacterActions.ActionFor(character)!, character.ToString(), true)).ToList();
        var session = new TypingSession(new Drill(Stage(stageId), targets), action => action, _clock);

        foreach (var (target, index) in targets.Select((target, index) => (target, index)))
        {
            if (index < mistakes)
                session.Press("q");
            session.Press(target.Action);
            _clock.Advance(TimeSpan.FromSeconds(secondsPerKey));
        }

        session.CheckTime();
        _store.Save(session);
        _clock.Advance(TimeSpan.FromMinutes(1));
    }
}
