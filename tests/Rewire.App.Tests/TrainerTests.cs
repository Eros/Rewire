using Rewire.Core.Keyboards;
using Rewire.Services;
using Rewire.Trainer;
using Rewire.ViewModels;
using PhysicalKey = Avalonia.Input.PhysicalKey;

namespace Rewire.App.Tests;

public class TrainerTests : IDisposable
{
    private static readonly string PackDirectory = Path.Combine(AppContext.BaseDirectory, "keyboards", "kinesis-advantage2");
    private static readonly KeyboardPack Advantage2 = KeyboardPackLoader.Load(PackDirectory);

    // The computer key that reports each action, so a test can "type" any drill target.
    private static readonly Dictionary<string, PhysicalKey> KeysByAction = Enum.GetValues<PhysicalKey>()
        .Select(key => (key, action: KeyCapture.ActionFor(key)))
        .Where(pair => pair.action is not null)
        .GroupBy(pair => pair.action!, StringComparer.OrdinalIgnoreCase)
        .ToDictionary(group => group.Key, group => group.First().key, StringComparer.OrdinalIgnoreCase);

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "rewire-tests", Guid.NewGuid().ToString("N"));
    private readonly Clock _clock = new();
    private readonly ProfileStore _profiles;
    private readonly TrainingStore _training;
    private readonly TrainerViewModel _trainer;

    public TrainerTests()
    {
        var database = Path.Combine(_directory, "rewire.db");
        _profiles = new ProfileStore(database);
        _training = new TrainingStore(database);
        _trainer = new TrainerViewModel(Advantage2, Curriculum.Load(PackDirectory), _profiles, _training, _clock);
    }

    [Fact]
    public void Only_the_baseline_can_be_started_at_first()
    {
        Assert.Equal("baseline", _trainer.SelectedStage!.Status.Stage.Id);
        Assert.True(_trainer.CanStart);

        _trainer.SelectedStage = _trainer.Stages[1];

        Assert.False(_trainer.CanStart);
    }

    [Fact]
    public void A_baseline_ends_after_two_minutes_and_opens_the_home_row()
    {
        _trainer.StartCommand.Execute(null);
        TypeTargets(20, TimeSpan.FromMilliseconds(250));
        _clock.Advance(TimeSpan.FromMinutes(2));

        _trainer.Tick();

        Assert.False(_trainer.IsRunning);
        Assert.StartsWith("Baseline saved", _trainer.Result);
        Assert.False(_trainer.Stages[1].IsLocked);
        Assert.Equal("home", _trainer.SelectedStage!.Status.Stage.Id);
    }

    [Fact]
    public void Highlights_the_key_to_press_next_in_the_layout_on_the_keyboard()
    {
        // In this layout Backspace and Space are swapped on the thumbs.
        _profiles.Import(new Dictionary<string, byte[]> { ["qwerty.txt"] = "[bspace]>[space]\r\n[space]>[bspace]\r\n"u8.ToArray() });
        CompleteBaseline();
        _trainer.Refresh();

        _trainer.StartCommand.Execute(null);
        while (_trainer.Session!.Current is { Display: not " " } target)
            Press(target.Action);

        Assert.Equal("bspace", _trainer.HighlightKeyId);
    }

    [Fact]
    public void Marks_a_target_missed_when_a_wrong_key_is_pressed()
    {
        CompleteBaseline();
        _trainer.StartCommand.Execute(null);
        var first = _trainer.Words[0].Cells[0];

        Press(_trainer.Session!.Current!.Action == "q" ? "w" : "q");

        Assert.True(first.IsMissed);
        Assert.True(first.IsCurrent);
    }

    [Fact]
    public void Saves_a_finished_drill_and_reports_whether_it_passed()
    {
        CompleteBaseline();
        _trainer.StartCommand.Execute(null);

        TypeTargets(int.MaxValue, TimeSpan.FromSeconds(2));

        Assert.False(_trainer.IsRunning);
        Assert.Contains("Not passed yet", _trainer.Result);
        Assert.Equal(2, _training.Sessions().Count);
        Assert.Equal("Home row", _trainer.RecentSessions[0].Stage);
    }

    [Fact]
    public void A_timed_test_ends_at_its_time_and_reports_the_speed()
    {
        StartTimedTest(seconds: 60);
        TypeTargets(30, TimeSpan.FromMilliseconds(200));
        _clock.Advance(TimeSpan.FromMinutes(1));

        _trainer.Tick();

        Assert.False(_trainer.IsRunning);
        Assert.Matches(@"^\d+ wpm at 100% accuracy over 1 minute\. That's your first 1 minute test\.$", _trainer.Result);
        Assert.Single(_training.Sessions(TimedTest.StageId(60)));
        Assert.StartsWith("Best ", _trainer.TimedOptions.Single(option => option.Seconds == 60).Summary);
        Assert.Equal("Timed, 1 minute", _trainer.RecentSessions[0].Stage);
    }

    [Fact]
    public void Compares_each_timed_test_with_the_best_so_far()
    {
        // Speed over a timed test is the keys typed before time runs out.
        RunTimedTest(seconds: 30, keys: 50);

        RunTimedTest(seconds: 30, keys: 30);
        Assert.Contains("Your best over 30 seconds is 20 wpm", _trainer.Result);

        RunTimedTest(seconds: 30, keys: 80);
        Assert.Contains("A new best, up from 20 wpm", _trainer.Result);
    }

    [Fact]
    public void A_timed_test_leaves_the_stages_alone_but_counts_towards_key_practice()
    {
        RunTimedTest(seconds: 30, keys: 50);

        Assert.Equal(StageState.Open, _trainer.Stages[0].Status.State);
        Assert.Null(_training.Baseline());
        Assert.NotEmpty(_training.Scores.Weights(_clock.GetUtcNow()).Keys);
    }

    [Fact]
    public void Shows_a_long_test_a_few_lines_at_a_time()
    {
        StartTimedTest(seconds: 60);
        var first = _trainer.Words[0];
        Assert.Equal(36, _trainer.Words.Count);

        // Typing through the first twelve words moves the window on by twelve.
        while (_trainer.Words[0] == first && _trainer.Session!.Current is { } target)
            _trainer.KeyPressed(KeysByAction[target.Action]);

        Assert.True(_trainer.Words[0].Cells[0].IsCurrent);
        Assert.Equal(36, _trainer.Words.Count);
    }

    [Fact]
    public void Ignores_keys_when_no_drill_is_running()
    {
        Assert.False(_trainer.KeyPressed(PhysicalKey.A));
    }

    public void Dispose()
    {
        _profiles.Dispose();
        _training.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    private void StartTimedTest(int seconds)
    {
        _trainer.Mode = 1;
        _trainer.SelectedTimed = _trainer.TimedOptions.Single(option => option.Seconds == seconds);
        _trainer.StartCommand.Execute(null);
    }

    private void RunTimedTest(int seconds, int keys)
    {
        StartTimedTest(seconds);
        TypeTargets(keys, TimeSpan.FromMilliseconds(100));
        _clock.Advance(TimeSpan.FromSeconds(seconds));
        _trainer.Tick();
        _clock.Advance(TimeSpan.FromMinutes(1));
    }

        private void CompleteBaseline()
    {
        _trainer.SelectedStage = _trainer.Stages[0];
        _trainer.StartCommand.Execute(null);
        TypeTargets(200, TimeSpan.FromMilliseconds(150));
        _clock.Advance(TimeSpan.FromMinutes(2));
        _trainer.Tick();
    }

    private void TypeTargets(int count, TimeSpan interval)
    {
        for (var i = 0; i < count && _trainer.Session?.Current is { } target; i++)
        {
            Press(target.Action);
            _clock.Advance(interval);
        }
    }

    private void Press(string action) => _trainer.KeyPressed(KeysByAction[action]);

    private sealed class Clock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 10, 31, 9, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }
}
