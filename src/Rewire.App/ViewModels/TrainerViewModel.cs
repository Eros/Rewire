using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Rewire.Core.Keyboards;
using Rewire.Core.Layouts;
using Rewire.Services;
using Rewire.Trainer;
using PhysicalKey = Avalonia.Input.PhysicalKey;

namespace Rewire.ViewModels;

/// <summary>One key to type in the drill text, styled by where the cursor is and whether it was missed.</summary>
public sealed partial class TargetCell(DrillTarget target) : ObservableObject
{
    public string Text => target.Display;

    public bool IsCommand => !target.IsCharacter;

    [ObservableProperty]
    public partial bool IsCurrent { get; set; }

    [ObservableProperty]
    public partial bool IsDone { get; set; }

    [ObservableProperty]
    public partial bool IsMissed { get; set; }
}

/// <summary>A word's cells kept together, so the drill text wraps between words rather than inside them.</summary>
public sealed record WordCells(IReadOnlyList<TargetCell> Cells);

public sealed record StageItem(StageStatus Status, int Number)
{
    public string Title => $"{Number}. {Status.Stage.Title}";

    public bool IsLocked => Status.State == StageState.Locked;

    public string Summary => Status switch
    {
        { State: StageState.Locked } => "Locked until the stage before is passed",
        { Stage.Drill: DrillKind.Baseline, Best: { } best } => $"Done · {best.WordsPerMinute:0} wpm",
        { Stage.Drill: DrillKind.Baseline } => "Not done yet",
        { State: StageState.Passed, Best: { } best } => $"Passed · best {best.WordsPerMinute:0} wpm at {best.Accuracy:0%}",
        { RequiredWordsPerMinute: { } required } => $"To pass: 95% accuracy at {required:0} wpm",
        _ => "To pass: 95% accuracy",
    };
}

/// <summary>Everything besides the keyboard that the smart trainer draws on.</summary>
/// <param name="OldBoards">Staggered keyboards the user may be coming from, for the old-habit detector.</param>
public sealed record TrainerContent(
    IReadOnlyList<KeyboardPack> OldBoards,
    IReadOnlyList<CodeTemplate> Templates,
    IReadOnlyList<ShortcutPack> ShortcutPacks)
{
    public static TrainerContent None { get; } = new([], [], []);
}

public sealed record TimedOption(int Seconds, SessionSummary? Best)
{
    public string Name => TimedTest.Name(Seconds);

    public string Summary => Best is { } best ? $"Best {best.WordsPerMinute:0} wpm at {best.Accuracy:0%}" : "Not taken yet";
}

public sealed record SessionRow(string Stage, SessionSummary Session)
{
    public string Text => $"{Stage} · {Session.WordsPerMinute:0} wpm · {Session.Accuracy:0%} · {Session.EndedAt.ToLocalTime():d MMM, HH:mm}";
}

/// <summary>
/// The trainer: runs drills on the layout that is actually on the keyboard, logs every keystroke
/// and opens each stage once the one before it is passed.
/// </summary>
public sealed partial class TrainerViewModel : ViewModelBase
{
    private const string LayoutFileName = "qwerty.txt";
    private const string OldKeyboardSetting = "old_keyboard";
    private const int StagesMode = 0;
    private const int TimedMode = 1;
    private const int CodeMode = 2;
    private const int ShortcutsMode = 3;
    private static readonly TimeSpan HeatWindow = TimeSpan.FromDays(30);

    private readonly IReadOnlyList<Stage> _stages;
    private readonly ProfileStore _profiles;
    private readonly TrainingStore _training;
    private readonly TimeProvider _time;
    private DrillGenerator _generator;
    private OldHabitDetector? _oldHabits;
    // Long drills show a window of words that moves on a block at a time, keeping the stats and map in view.
    private const int VisibleWords = 36;
    private const int WordStep = 12;

    private List<TargetCell> _cells = [];
    private List<WordCells> _allWords = [];
    private int[] _wordOfCell = [];
    private int _windowStart = -1;

    public TrainerViewModel(
        KeyboardPack pack,
        IReadOnlyList<Stage> stages,
        ProfileStore profiles,
        TrainingStore training,
        TimeProvider? time = null,
        TrainerContent? content = null)
    {
        Pack = pack;
        _stages = stages;
        _profiles = profiles;
        _training = training;
        _time = time ?? TimeProvider.System;
        content ??= TrainerContent.None;

        Layout = EffectiveLayout.Build(pack, LayoutFile.Empty);
        _generator = new DrillGenerator(Layout, KeyCapture.Actions);
        Code = new CodePracticeViewModel(content.Templates, training, _time);
        Shortcuts = new ShortcutPracticeViewModel(pack, content.ShortcutPacks, training, _time);

        OldBoards = content.OldBoards;
        var savedBoard = training.Setting(OldKeyboardSetting);
        var defaultBoard = OperatingSystem.IsMacOS() ? "apple-macbook-us" : "ansi-us";
        SelectedOldBoard = OldBoards.FirstOrDefault(board => board.Id == savedBoard)
            ?? OldBoards.FirstOrDefault(board => board.Id == defaultBoard)
            ?? OldBoards.FirstOrDefault();

        Refresh();
    }

    public CodePracticeViewModel Code { get; }

    public ShortcutPracticeViewModel Shortcuts { get; }

    public IReadOnlyList<KeyboardPack> OldBoards { get; }

    public IReadOnlyList<string> Modes { get; } = ["Stages", "Timed", "Code", "Shortcuts"];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsStagesMode), nameof(IsTimedMode), nameof(IsTypingMode), nameof(IsCodeMode),
        nameof(IsShortcutsMode), nameof(CanStart), nameof(DrillTitle), nameof(DrillDescription))]
    public partial int Mode { get; set; }

    public bool IsStagesMode => Mode == StagesMode;

    public bool IsTimedMode => Mode == TimedMode;

    /// <summary>Stages and timed tests share the same typing screen.</summary>
    public bool IsTypingMode => Mode is StagesMode or TimedMode;

    public ObservableCollection<TimedOption> TimedOptions { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanStart), nameof(DrillTitle), nameof(DrillDescription))]
    public partial TimedOption? SelectedTimed { get; set; }

    public string DrillTitle => IsTimedMode
        ? SelectedTimed is { } timed ? TimedTest.Stage(timed.Seconds).Title : "Timed test"
        : SelectedStage?.Status.Stage.Title ?? "";

    public string DrillDescription => IsTimedMode
        ? TimedTest.Stage(SelectedTimed?.Seconds ?? 60).Description
        : SelectedStage?.Status.Stage.Description ?? "";

    public bool IsCodeMode => Mode == CodeMode;

    public bool IsShortcutsMode => Mode == ShortcutsMode;

    [ObservableProperty]
    public partial KeyboardPack? SelectedOldBoard { get; set; }

    /// <summary>Explains the last mistake when it looks like an old habit.</summary>
    [ObservableProperty]
    public partial string? Hint { get; private set; }

    [ObservableProperty]
    public partial string? RelearnSummary { get; private set; }

    [ObservableProperty]
    public partial string? OldHabitSummary { get; private set; }

    /// <summary>True while any drill, in any mode, is under way.</summary>
    public bool IsBusy => IsRunning || Code.IsRunning || Shortcuts.IsRunning;

    public KeyboardPack Pack { get; }

    public ObservableCollection<StageItem> Stages { get; } = [];

    public ObservableCollection<WordCells> Words { get; } = [];

    public ObservableCollection<SessionRow> RecentSessions { get; } = [];

    [ObservableProperty]
    public partial EffectiveLayout Layout { get; private set; }

    [ObservableProperty]
    public partial string LayoutDescription { get; private set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanStart), nameof(DrillTitle), nameof(DrillDescription))]
    public partial StageItem? SelectedStage { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRunning), nameof(CanStart))]
    public partial TypingSession? Session { get; private set; }

    [ObservableProperty]
    public partial string? HighlightKeyId { get; private set; }

    [ObservableProperty]
    public partial string LiveStats { get; private set; } = "";

    [ObservableProperty]
    public partial string? Result { get; private set; }

    [ObservableProperty]
    public partial string BaselineSummary { get; private set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Heat))]
    public partial bool ShowHeat { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Heat))]
    public partial IReadOnlyDictionary<string, double> ErrorHeat { get; private set; } = new Dictionary<string, double>();

    public IReadOnlyDictionary<string, double>? Heat => ShowHeat ? ErrorHeat : null;

    public bool IsRunning => Session is { IsComplete: false };

    public bool HasDrill => Words.Count > 0;

    public bool CanStart => !IsRunning && (IsTimedMode ? SelectedTimed is not null : SelectedStage is { IsLocked: false });

    /// <summary>Reloads the layout and progress, so a change just applied in the configurator is drilled at once.</summary>
    public void Refresh()
    {
        var onKeyboard = _profiles.Find(LayoutFileName)?.Keyboard;
        Layout = EffectiveLayout.Build(Pack, onKeyboard is null ? LayoutFile.Empty : LayoutFile.Parse(onKeyboard));
        LayoutDescription = onKeyboard is null
            ? "Drilling the factory layout. Import your layouts in Configure to drill your own."
            : $"Drilling {LayoutFileName} as it is on your keyboard.";
        _generator = new DrillGenerator(Layout, KeyCapture.Actions) { Weights = _training.Scores.Weights(_time.GetUtcNow()) };

        // Only the keyboard's own layout counts: the factory layout shown before any import is not one the user chose.
        if (onKeyboard is not null)
        {
            var actions = Pack.Keys
                .Select(key => Layout.Find(Pack.Layers[0].Id, key.Id))
                .OfType<EffectiveKey>()
                .ToDictionary(key => key.Key.Id, key => key.Action);
            _training.Scores.NoteLayout(actions, _time.GetUtcNow());
        }

        Code.Use(_generator, Pack.LegendFor("enter"));
        RefreshProgress();
    }

    [RelayCommand]
    private void Start()
    {
        Stage stage;
        if (IsTimedMode && SelectedTimed is { } timed)
            stage = TimedTest.Stage(timed.Seconds);
        else if (!IsTimedMode && SelectedStage is { IsLocked: false } item)
            stage = item.Status.Stage;
        else
            return;

        var earlier = _stages.TakeWhile(other => other != stage)
            .Where(other => other.Drill != DrillKind.Baseline)
            .SelectMany(other => other.Keys);

        _generator.Weights = _training.Scores.Weights(_time.GetUtcNow());
        var drill = _generator.Generate(stage, earlier);

        // The baseline is typed on the old keyboard, where reaching for old positions is right.
        var detector = stage.Drill == DrillKind.Baseline ? null : _oldHabits;
        Session = new TypingSession(drill, _generator.KeyFor, _time, detector);
        Result = null;
        Hint = null;
        BuildCells(drill);
        UpdateCursor();
    }

    [RelayCommand]
    private void Stop()
    {
        Session = null;
        Words.Clear();
        OnPropertyChanged(nameof(HasDrill));
        HighlightKeyId = null;
        LiveStats = "";
    }

    /// <summary>Called by the window for each key pressed, to be judged by whichever mode is running.</summary>
    public bool KeyPressed(PhysicalKey key, Avalonia.Input.KeyModifiers modifiers = Avalonia.Input.KeyModifiers.None) => Mode switch
    {
        CodeMode => Code.KeyPressed(key),
        ShortcutsMode => Shortcuts.KeyPressed(key, modifiers),
        _ => StageKeyPressed(key),
    };

    private bool StageKeyPressed(PhysicalKey key)
    {
        if (Session is not { IsComplete: false } session)
            return false;

        if (KeyCapture.ActionFor(key) is not { } action)
            return true;

        var position = session.Position;
        var result = session.Press(action);
        if (result == PressResult.Wrong)
        {
            _cells[position].IsMissed = true;
            Hint = session.LastKeystroke is { OldHabit: { } oldKey } keystroke ? OldHabitHint(keystroke, oldKey) : null;
        }
        else if (result == PressResult.Correct)
        {
            Hint = null;
        }

        UpdateCursor();
        if (session.IsComplete)
            Finish(session);
        return true;
    }

    /// <summary>Called a few times a second by the view, to keep the clock and timed drills moving.</summary>
    public void Tick()
    {
        if (Session is not { IsComplete: false } session)
            return;

        if (session.CheckTime())
            Finish(session);
        else
            UpdateLiveStats(session);
    }

    private void Finish(TypingSession session)
    {
        UpdateCursor();
        OnPropertyChanged(nameof(IsRunning));
        OnPropertyChanged(nameof(CanStart));

        if (session.Drill.Stage.Drill == DrillKind.Timed)
        {
            FinishTimed(session);
            return;
        }

        var before = _training.Progress(_stages);
        var saved = _training.Save(session);
        var after = _training.Progress(_stages);
        RefreshProgress();

        var index = _stages.ToList().IndexOf(session.Drill.Stage);
        var passedNow = after[index].State == StageState.Passed && before[index].State != StageState.Passed;
        var opened = index + 1 < after.Count && after[index + 1].State != StageState.Locked && before[index + 1].State == StageState.Locked
            ? after[index + 1].Stage.Title
            : null;

        if (opened is not null)
            SelectedStage = Stages[index + 1];

        var speed = $"{saved.WordsPerMinute:0} wpm at {saved.Accuracy:0%} accuracy.";
        Result = (session.Drill.Stage.Drill, passedNow, opened) switch
        {
            (DrillKind.Baseline, _, _) => $"Baseline saved: {speed} Later stages are measured against this.",
            (_, true, { } next) => $"Passed: {speed} {next} is now open.",
            (_, true, null) => $"Passed: {speed}",
            _ when after[index].State == StageState.Passed => $"{speed} This stage is already passed; keep practising or move on.",
            _ => $"{speed} Not passed yet: {Requirement(after[index])}",
        };
    }

    private void FinishTimed(TypingSession session)
    {
        var stageId = session.Drill.Stage.Id;
        var previousBest = _training.Sessions(stageId).MaxBy(previous => previous.WordsPerMinute);
        var saved = _training.Save(session);
        RefreshProgress();

        var length = TimedTest.Name((int)session.Drill.Stage.TimeLimit!.Value.TotalSeconds);
        var result = $"{saved.WordsPerMinute:0} wpm at {saved.Accuracy:0%} accuracy over {length}.";
        Result = previousBest switch
        {
            null => $"{result} That's your first {length} test.",
            _ when saved.WordsPerMinute > previousBest.WordsPerMinute => $"{result} A new best, up from {previousBest.WordsPerMinute:0} wpm.",
            _ => $"{result} Your best over {length} is {previousBest.WordsPerMinute:0} wpm.",
        };
    }

    partial void OnSelectedOldBoardChanged(KeyboardPack? value)
    {
        _oldHabits = value is null ? null : new OldHabitDetector(value, Pack);
        if (value is not null)
            _training.SetSetting(OldKeyboardSetting, value.Id);
    }

    private string OldHabitHint(Keystroke keystroke, string oldKey)
    {
        var key = Pack.Key(keystroke.IntendedKeyId);
        var where = key.Cluster == Cluster.Thumb
            ? $"under your {key.Hand.ToString().ToLowerInvariant()} thumb"
            : $"under your {key.Hand.ToString().ToLowerInvariant()} {key.Finger.ToString().ToLowerInvariant()}";
        // Named as the map names it: a Mac calls Backspace "Delete", which would clash with the Advantage2's own Delete key.
        return $"Old habit: you reached for {Pack.LegendFor(keystroke.IntendedAction)} where it is on your old keyboard. Here it's {where}.";
    }

    private static string Requirement(StageStatus status) => status.RequiredWordsPerMinute is { } required
        ? $"you need 95% accuracy at {required:0} wpm."
        : "you need 95% accuracy.";

    private void RefreshProgress()
    {
        var selectedId = SelectedStage?.Status.Stage.Id;
        var progress = _training.Progress(_stages);

        Stages.Clear();
        for (var i = 0; i < progress.Count; i++)
            Stages.Add(new StageItem(progress[i], i + 1));

        SelectedStage = Stages.FirstOrDefault(stage => stage.Status.Stage.Id == selectedId)
            ?? Stages.LastOrDefault(stage => !stage.IsLocked);

        var selectedSeconds = SelectedTimed?.Seconds ?? 60;
        TimedOptions.Clear();
        foreach (var seconds in TimedTest.Durations)
            TimedOptions.Add(new TimedOption(seconds, _training.Sessions(TimedTest.StageId(seconds)).MaxBy(session => session.WordsPerMinute)));
        SelectedTimed = TimedOptions.FirstOrDefault(option => option.Seconds == selectedSeconds);

        var titles = _stages.ToDictionary(stage => stage.Id, stage => stage.Title);
        foreach (var seconds in TimedTest.Durations)
            titles[TimedTest.StageId(seconds)] = $"Timed, {TimedTest.Name(seconds)}";
        RecentSessions.Clear();
        foreach (var session in _training.Sessions().Take(8))
            RecentSessions.Add(new SessionRow(titles.GetValueOrDefault(session.StageId, session.StageId), session));

        var baseline = _training.Baseline();
        var latest = _training.Sessions().FirstOrDefault(session => session.Drill != DrillKind.Baseline);
        BaselineSummary = (baseline, latest) switch
        {
            (null, _) => "No baseline yet. Type it on your old keyboard so progress can be measured.",
            (_, null) => $"Baseline: {baseline.WordsPerMinute:0} wpm on your old keyboard.",
            _ => $"Baseline: {baseline.WordsPerMinute:0} wpm. Latest session: {latest.WordsPerMinute:0} wpm, {latest.WordsPerMinute / baseline.WordsPerMinute:0%} of your old speed.",
        };

        var relearning = _training.Scores.Relearning();
        RelearnSummary = relearning.Count == 0
            ? null
            : $"Relearning after your remap: {string.Join(", ", relearning.Select(keyId => Pack.LegendFor(Pack.Layers[0].Keys.TryGetValue(keyId, out var entry) ? entry.Action : keyId)))}.";

        var habits = _training.OldHabits(_time.GetUtcNow() - HeatWindow);
        OldHabitSummary = habits.Count == 0 || SelectedOldBoard is not { } oldBoard
            ? null
            : $"Most frequent old habit: reaching for {Pack.LegendFor(habits[0].OldKey)} where it is on your {oldBoard.Name} ({habits[0].Count} times this month).";

        // Scaled against the worst key, so the map shows where the trouble is even when every rate is low.
        var statistics = _training.KeyStatistics(_time.GetUtcNow() - HeatWindow).Where(key => key.Presses >= 5).ToList();
        var worst = statistics.Select(key => key.ErrorRate).DefaultIfEmpty(0).Max();
        ErrorHeat = worst <= 0
            ? new Dictionary<string, double>()
            : statistics.ToDictionary(key => key.KeyId, key => key.ErrorRate / worst);
    }

    private void BuildCells(Drill drill)
    {
        _cells = drill.Targets.Select(target => new TargetCell(target)).ToList();
        _allWords = [];
        _wordOfCell = new int[_cells.Count];

        var word = new List<TargetCell>();
        for (var i = 0; i < _cells.Count; i++)
        {
            word.Add(_cells[i]);
            _wordOfCell[i] = _allWords.Count;
            if (_cells[i].Text == " ")
            {
                _allWords.Add(new WordCells(word));
                word = [];
            }
        }

        if (word.Count > 0)
            _allWords.Add(new WordCells(word));

        _windowStart = -1;
        ShowWindow(0);
        OnPropertyChanged(nameof(HasDrill));
    }

    private void ShowWindow(int position)
    {
        var currentWord = _wordOfCell.Length == 0 ? 0 : _wordOfCell[Math.Min(position, _wordOfCell.Length - 1)];
        var start = currentWord / WordStep * WordStep;
        if (start == _windowStart)
            return;

        _windowStart = start;
        Words.Clear();
        foreach (var visible in _allWords.Skip(start).Take(VisibleWords))
            Words.Add(visible);
    }

    private void UpdateCursor()
    {
        if (Session is not { } session)
            return;

        for (var i = 0; i < _cells.Count; i++)
        {
            _cells[i].IsDone = i < session.Position;
            _cells[i].IsCurrent = i == session.Position && !session.IsComplete;
        }

        ShowWindow(session.Position);

        // The baseline is typed on another keyboard, so pointing at Advantage2 keys would only confuse.
        HighlightKeyId = session.Drill.Stage.Drill == DrillKind.Baseline ? null : session.Current?.KeyId;
        UpdateLiveStats(session);
    }

    private void UpdateLiveStats(TypingSession session)
    {
        var clock = session.Drill.Stage.TimeLimit is { } limit
            ? $"{Math.Max(0, (limit - session.Elapsed).TotalSeconds):0} s left"
            : $"{session.Position} of {session.Targets.Count}";
        LiveStats = $"{session.WordsPerMinute:0} wpm · {session.Accuracy:0%} accuracy · {clock}";
    }
}
