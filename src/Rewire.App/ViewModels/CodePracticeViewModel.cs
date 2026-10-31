using System.Collections.ObjectModel;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Rewire.Services;
using Rewire.Trainer;
using PhysicalKey = Avalonia.Input.PhysicalKey;

namespace Rewire.ViewModels;

/// <summary>One character of a code drill: grey until typed, then shown in its syntax colour.</summary>
public sealed partial class CodeCell(string text, IBrush typedBrush) : ObservableObject
{
    private static readonly IBrush Pending = new SolidColorBrush(Color.Parse("#8A8F98"), 0.55);
    private static readonly IBrush Missed = new SolidColorBrush(Color.Parse("#E5484D"));

    public string Text => text;

    public bool IsEnter => text == "⏎";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Foreground))]
    public partial bool IsDone { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Foreground))]
    public partial bool IsMissed { get; set; }

    [ObservableProperty]
    public partial bool IsCurrent { get; set; }

    public IBrush Foreground => IsMissed ? Missed : IsDone ? typedBrush : Pending;
}

public sealed record CodeLineCells(string Indent, IReadOnlyList<CodeCell> Cells);

public sealed record CodeLevelItem(int Level, string Name, StageState State)
{
    public string Title => $"Level {Level}: {Name}";

    public bool IsLocked => State == StageState.Locked;

    public string Summary => State switch
    {
        StageState.Locked => "Opens after three templates at 95% on the level before",
        StageState.Passed => "Passed",
        _ => "Open",
    };
}

/// <summary>Code drills: real snippets that reveal their syntax colours as each character is typed correctly.</summary>
public sealed partial class CodePracticeViewModel : ViewModelBase
{
    private readonly IReadOnlyList<CodeTemplate> _templates;
    private readonly TrainingStore _training;
    private readonly TimeProvider _time;
    private readonly Random _random;
    private List<CodeCell> _cells = [];
    private DrillGenerator? _generator;
    private string _enterLegend = "Enter";

    public CodePracticeViewModel(IReadOnlyList<CodeTemplate> templates, TrainingStore training, TimeProvider time, Random? random = null)
    {
        _templates = templates;
        _training = training;
        _time = time;
        _random = random ?? Random.Shared;
        Languages = templates.Select(template => template.Language).Distinct().ToList();
        SelectedLanguage = Languages.Contains("csharp") ? "csharp" : Languages.FirstOrDefault();
    }

    public IReadOnlyList<string> Languages { get; }

    /// <summary>Shows a template folder name, such as <c>csharp</c>, as the language's own name.</summary>
    public static Avalonia.Data.Converters.IValueConverter LanguageName { get; } =
        new Avalonia.Data.Converters.FuncValueConverter<string, string>(folder => folder switch
        {
            "csharp" => "C#",
            "cpp" => "C++",
            "javascript" => "JavaScript",
            "typescript" => "TypeScript",
            null => "",
            _ => char.ToUpperInvariant(folder[0]) + folder[1..],
        });

    public ObservableCollection<CodeLevelItem> Levels { get; } = [];

    public ObservableCollection<CodeLineCells> Lines { get; } = [];

    [ObservableProperty]
    public partial string? SelectedLanguage { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanStart))]
    public partial CodeLevelItem? SelectedLevel { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRunning), nameof(CanStart))]
    public partial TypingSession? Session { get; private set; }

    [ObservableProperty]
    public partial string TemplateName { get; private set; } = "";

    [ObservableProperty]
    public partial string LiveStats { get; private set; } = "";

    [ObservableProperty]
    public partial string? Result { get; private set; }

    public bool IsRunning => Session is { IsComplete: false };

    public bool CanStart => SelectedLevel is { IsLocked: false } && !IsRunning && _generator is not null;

    /// <summary>Called whenever the layout or skill weights change, so drills use the latest of both.</summary>
    public void Use(DrillGenerator generator, string enterLegend)
    {
        _generator = generator;
        _enterLegend = enterLegend;
        RefreshLevels();
        OnPropertyChanged(nameof(CanStart));
    }

    [RelayCommand]
    private void Start()
    {
        if (_generator is null || SelectedLanguage is null || SelectedLevel is not { IsLocked: false } level)
            return;

        var candidates = _templates.Where(template => template.Language == SelectedLanguage && template.Level == level.Level).ToList();
        if (candidates.Count == 0)
        {
            Result = "There are no templates for this level yet.";
            return;
        }

        // Templates rich in the user's weak keys come up more often.
        var template = Pick(candidates, candidate => _generator.Need(candidate.Text));
        var code = CodeTemplates.Build(template, _generator.KeyFor, _enterLegend);
        Session = new TypingSession(code.Drill, _generator.KeyFor, _time);
        TemplateName = template.Name;
        Result = null;
        BuildLines(code);
        UpdateCursor();
    }

    [RelayCommand]
    private void Stop()
    {
        Session = null;
        Lines.Clear();
        LiveStats = "";
    }

    public bool KeyPressed(PhysicalKey key)
    {
        if (Session is not { IsComplete: false } session)
            return false;

        if (KeyCapture.ActionFor(key) is not { } action)
            return true;

        var position = session.Position;
        if (session.Press(action) == PressResult.Wrong)
            _cells[position].IsMissed = true;

        UpdateCursor();
        if (session.IsComplete)
            Finish(session);
        return true;
    }

    private void Finish(TypingSession session)
    {
        OnPropertyChanged(nameof(IsRunning));
        OnPropertyChanged(nameof(CanStart));
        var saved = _training.Save(session);
        RefreshLevels();

        var next = SelectedLevel is { } current ? Levels.FirstOrDefault(level => level.Level == current.Level + 1) : null;
        Result = next is { IsLocked: false }
            ? $"{saved.Accuracy:0%} accuracy at {saved.WordsPerMinute:0} wpm. Level {next.Level} is open."
            : $"{saved.Accuracy:0%} accuracy at {saved.WordsPerMinute:0} wpm. The next level opens after three templates averaging 95%.";
    }

    partial void OnSelectedLanguageChanged(string? value) => RefreshLevels();

    private void RefreshLevels()
    {
        if (SelectedLanguage is null)
            return;

        var selected = SelectedLevel?.Level;
        var states = _training.CodeLevels(SelectedLanguage);
        Levels.Clear();
        for (var level = 1; level <= CodeTemplates.Levels; level++)
            Levels.Add(new CodeLevelItem(level, CodeTemplates.LevelNames[level - 1], states[level - 1]));

        SelectedLevel = Levels.FirstOrDefault(level => level.Level == selected && !level.IsLocked)
            ?? Levels.LastOrDefault(level => !level.IsLocked);
    }

    private void BuildLines(CodeDrill code)
    {
        var dark = Avalonia.Application.Current?.ActualThemeVariant == Avalonia.Styling.ThemeVariant.Dark;
        var colours = new CodeHighlighter(dark).Colours(code.Template.Language, code.Template.Text);
        var plain = new SolidColorBrush(dark ? Color.Parse("#D4D4D4") : Color.Parse("#1F2328"));

        _cells = code.Drill.Targets.Select((target, index) =>
        {
            var offset = code.Offsets[index];
            var brush = offset >= 0 && colours[offset] is { } colour ? new SolidColorBrush(colour) : plain;
            return new CodeCell(target.IsCharacter ? target.Display : "⏎", brush);
        }).ToList();

        Lines.Clear();
        foreach (var line in code.Lines)
            Lines.Add(new CodeLineCells(new string(' ', line.Indent), _cells.GetRange(line.FirstTarget, line.TargetCount)));
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

        LiveStats = $"{session.WordsPerMinute:0} wpm · {session.Accuracy:0%} accuracy · {session.Position} of {session.Targets.Count}";
    }

    private T Pick<T>(IReadOnlyList<T> items, Func<T, double> weight)
    {
        var roll = _random.NextDouble() * items.Sum(weight);
        foreach (var item in items)
        {
            roll -= weight(item);
            if (roll <= 0)
                return item;
        }

        return items[^1];
    }
}
