using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Rewire.Core.Keyboards;
using Rewire.Core.Layouts;
using Rewire.Services;
using Rewire.Trainer;
using PhysicalKey = Avalonia.Input.PhysicalKey;

namespace Rewire.ViewModels;

/// <summary>One step of a macro shown as a removable chip, such as "H", "hold Shift" or "pause 500 ms".</summary>
public sealed record MacroChip(MacroToken Token, string Label);

/// <summary>A modifier that, held with the key, triggers the macro.</summary>
public sealed partial class TriggerModifier(string location, string label) : ObservableObject
{
    public string Location => location;

    public string Label => label;

    [ObservableProperty]
    public partial bool IsOn { get; set; }
}

public sealed record SpeedOption(int? Speed)
{
    public string Label => Speed is { } speed ? $"Speed {speed}" : "Keyboard's global speed";
}

/// <summary>
/// Builds one macro for one key: its trigger modifiers, its steps as chips, recorded from the keyboard or typed
/// as text, and its playback speed, checked against the manual's rules as it changes.
/// </summary>
public sealed partial class MacroEditorViewModel : ViewModelBase
{
    private static readonly string[] ModifierKeys = ["lshift", "rshift", "lctrl", "rctrl", "lalt", "ralt", "lwin", "rwin"];

    private readonly KeyboardPack _pack;
    private readonly LayoutFile _file;
    private readonly string _keyLocation;

    public MacroEditorViewModel(KeyboardPack pack, LayoutFile file, KeyboardLayer layer, string keyId, Macro? existing)
    {
        _pack = pack;
        _file = file;
        _keyLocation = layer.Keys[keyId].Location;
        Original = existing;

        // Modifiers are named by the location tokens of this layer, so keypad-layer macros use kp- triggers.
        Modifiers = ModifierKeys
            .Where(layer.Keys.ContainsKey)
            .Select(modifier => new TriggerModifier(layer.Keys[modifier].Location, ModifierLabel(modifier)))
            .ToList();
        SpeedOptions = [new SpeedOption(null), .. Enumerable.Range(1, 9).Select(speed => new SpeedOption(speed))];
        Speed = SpeedOptions[0];

        if (existing is not null)
            Load(existing);

        foreach (var modifier in Modifiers)
            modifier.PropertyChanged += (_, _) => Recheck();
        Steps.CollectionChanged += (_, _) => Recheck();
        Recheck();
    }

    /// <summary>Shows errors in red and warnings in amber.</summary>
    public static Avalonia.Data.Converters.IValueConverter SeverityBrush { get; } =
        new Avalonia.Data.Converters.FuncValueConverter<MacroProblemSeverity, Avalonia.Media.IBrush>(severity =>
            new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse(severity == MacroProblemSeverity.Error ? "#E5484D" : "#D9822B")));

    public Macro? Original { get; }

    public IReadOnlyList<TriggerModifier> Modifiers { get; }

    public IReadOnlyList<SpeedOption> SpeedOptions { get; }

    public ObservableCollection<MacroChip> Steps { get; } = [];

    public string KeyLegend => _pack.LegendFor(_keyLocation.StartsWith("kp-", StringComparison.OrdinalIgnoreCase) ? _keyLocation[3..] : _keyLocation);

    [ObservableProperty]
    public partial SpeedOption Speed { get; set; }

    [ObservableProperty]
    public partial string Text { get; set; } = "";

    [ObservableProperty]
    public partial bool IsRecording { get; private set; }

    [ObservableProperty]
    public partial IReadOnlyList<MacroProblem> Problems { get; private set; } = [];

    [ObservableProperty]
    public partial bool CanSave { get; private set; }

    public string TriggerDescription => string.Join(" + ",
        Modifiers.Where(modifier => modifier.IsOn).Select(modifier => modifier.Label).Append(KeyLegend));

    public Macro Build()
    {
        var trigger = Modifiers.Where(modifier => modifier.IsOn)
            .Select(modifier => new LocationToken(modifier.Location))
            .Append(new LocationToken(_keyLocation))
            .ToList();

        var playback = new List<ActionToken>();
        if (Speed.Speed is { } speed)
            playback.Add(new MacroToken(MacroTokenKind.Speed, speed.ToString()).ToAction());
        playback.AddRange(Steps.Select(step => step.Token.ToAction()));
        return new Macro(trigger, playback);
    }

    [RelayCommand]
    private void ToggleRecording() => IsRecording = !IsRecording;

    [RelayCommand]
    private void AddText()
    {
        var shifted = false;
        foreach (var character in Text)
        {
            if (CharacterActions.ActionFor(character) is not { } action)
                continue;

            // Consecutive capitals and symbols share one Shift hold, as a person would type them.
            var needsShift = CharacterActions.NeedsShift(character);
            if (needsShift != shifted)
            {
                Add(new MacroToken(needsShift ? MacroTokenKind.Press : MacroTokenKind.Release, "lshift"));
                shifted = needsShift;
            }

            Add(new MacroToken(MacroTokenKind.Key, action));
        }

        if (shifted)
            Add(new MacroToken(MacroTokenKind.Release, "lshift"));
        Text = "";
    }

    [RelayCommand]
    private void AddDelay(string milliseconds) => Add(new MacroToken(MacroTokenKind.Delay, milliseconds));

    [RelayCommand]
    private void RemoveStep(MacroChip chip) => Steps.Remove(chip);

    [RelayCommand]
    private void ClearSteps() => Steps.Clear();

    /// <summary>While recording, a modifier going down holds it and coming up lets it go; any other key is tapped.</summary>
    public bool KeyDown(PhysicalKey key)
    {
        if (!IsRecording || KeyCapture.ActionFor(key) is not { } action)
            return IsRecording;

        Add(new MacroToken(Macros.Modifiers.Contains(action) ? MacroTokenKind.Press : MacroTokenKind.Key, action));
        return true;
    }

    public bool KeyUp(PhysicalKey key)
    {
        if (!IsRecording || KeyCapture.ActionFor(key) is not { } action)
            return IsRecording;

        if (Macros.Modifiers.Contains(action))
            Add(new MacroToken(MacroTokenKind.Release, action));
        return true;
    }

    partial void OnSpeedChanged(SpeedOption value) => Recheck();

    private void Load(Macro macro)
    {
        foreach (var token in macro.Trigger)
        {
            if (Modifiers.FirstOrDefault(modifier => string.Equals(modifier.Location, token.Name, StringComparison.OrdinalIgnoreCase)) is { } modifier)
                modifier.IsOn = true;
        }

        var steps = macro.Playback.Select(MacroToken.Read).ToList();
        if (steps is [{ Kind: MacroTokenKind.Speed } first, ..] && int.TryParse(first.Value, out var speed) && speed is >= 1 and <= 9)
        {
            Speed = SpeedOptions[speed];
            steps.RemoveAt(0);
        }

        foreach (var step in steps)
            Add(step);
    }

    private void Add(MacroToken token) => Steps.Add(new MacroChip(token, Describe(token)));

    private void Recheck()
    {
        var macro = Build();
        var file = Original is null ? _file : Macros.Remove(_file, Original.Trigger);
        Problems = Macros.Check(macro, file, _pack);
        CanSave = Problems.All(problem => problem.Severity != MacroProblemSeverity.Error);
        OnPropertyChanged(nameof(TriggerDescription));
    }

    private string Describe(MacroToken token) => token.Kind switch
    {
        MacroTokenKind.Press => $"{ShortModifierLabel(token.Value)} ↓",
        MacroTokenKind.Release => $"{ShortModifierLabel(token.Value)} ↑",
        MacroTokenKind.Speed => $"speed {token.Value}",
        MacroTokenKind.Delay => $"pause {token.Value} ms",
        _ => token.Value == "space" ? "Space" : _pack.LegendFor(token.Value),
    };

    private string ShortModifierLabel(string modifier) =>
        (modifier[0] is 'l' ? "L " : modifier[0] is 'r' ? "R " : "") + _pack.LegendFor(modifier);

    private string ModifierLabel(string modifier) =>
        (modifier[0] is 'l' ? "Left " : modifier[0] is 'r' ? "Right " : "") + _pack.LegendFor(modifier);
}
