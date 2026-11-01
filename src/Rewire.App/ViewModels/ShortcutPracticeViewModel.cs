using Avalonia.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Rewire.Core.Keyboards;
using Rewire.Services;
using Rewire.Trainer;
using PhysicalKey = Avalonia.Input.PhysicalKey;

namespace Rewire.ViewModels;

/// <summary>Shortcut drills: a prompt such as "Go to declaration", answered by pressing its binding.</summary>
public sealed partial class ShortcutPracticeViewModel(
    KeyboardPack pack,
    IReadOnlyList<ShortcutPack> packs,
    TrainingStore training,
    TimeProvider time,
    Random? random = null) : ViewModelBase
{
    private static readonly HashSet<string> ModifierActions = new(
        ["lshift", "rshift", "lctrl", "rctrl", "lalt", "ralt", "lwin", "rwin"], StringComparer.OrdinalIgnoreCase);

    private readonly Random _random = random ?? Random.Shared;

    public IReadOnlyList<ShortcutPack> Packs { get; } = packs;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanStart))]
    public partial ShortcutPack? SelectedPack { get; set; } = packs.FirstOrDefault();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRunning), nameof(CanStart))]
    public partial ShortcutSession? Session { get; private set; }

    [ObservableProperty]
    public partial string Prompt { get; private set; } = "";

    [ObservableProperty]
    public partial string Feedback { get; private set; } = "";

    [ObservableProperty]
    public partial string Progress { get; private set; } = "";

    [ObservableProperty]
    public partial string? Result { get; private set; }

    public bool IsRunning => Session is { IsComplete: false };

    public bool CanStart => SelectedPack is not null && !IsRunning;

    [RelayCommand]
    private void Start()
    {
        if (SelectedPack is not { } shortcutPack)
            return;

        var prompts = shortcutPack.Shortcuts.OrderBy(_ => _random.Next()).ToList();
        Session = new ShortcutSession(shortcutPack, prompts, time);
        Result = null;
        Feedback = "";
        ShowPrompt();
    }

    [RelayCommand]
    private void Stop()
    {
        Session = null;
        Prompt = Feedback = Progress = "";
    }

    public bool KeyPressed(PhysicalKey key, KeyModifiers modifiers)
    {
        if (Session is not { IsComplete: false } session)
            return false;

        // Wait for the key that completes the combination; the modifiers alone say nothing yet.
        if (KeyCapture.ActionFor(key) is not { } action || ModifierActions.Contains(action))
            return true;

        var chord = Chord(action, modifiers, session.Pack.IsMac);
        if (session.Press(chord) == ShortcutResult.Correct)
        {
            Feedback = "";
            if (session.IsComplete)
                Finish(session);
            else
                ShowPrompt();
            return true;
        }

        Feedback = session.IsRevealed
            ? $"It's {Describe(session.Current!.Chord)}. Press it to move on."
            : $"Not quite: that was {Describe(chord)}.";
        return true;
    }

    private void Finish(ShortcutSession session)
    {
        OnPropertyChanged(nameof(IsRunning));
        OnPropertyChanged(nameof(CanStart));
        var saved = training.Save(session);
        Prompt = "";
        Progress = "";
        Result = $"{saved.Accuracy:0%} right first time, {session.Prompts.Count} shortcuts in {(session.EndedAt!.Value - session.StartedAt!.Value).TotalSeconds:0} seconds.";
    }

    private void ShowPrompt()
    {
        if (Session is not { Current: { } current } session)
            return;

        Prompt = current.Action;
        Progress = $"{session.Position + 1} of {session.Prompts.Count} · {session.Accuracy:0%} right first time";
    }

    // Meta is named as the pack names it, so a pack's chords match whichever system the drill runs on.
    private static string Chord(string action, KeyModifiers modifiers, bool mac)
    {
        var parts = new List<string>();
        if (modifiers.HasFlag(KeyModifiers.Control))
            parts.Add("ctrl");
        if (modifiers.HasFlag(KeyModifiers.Alt))
            parts.Add("alt");
        if (modifiers.HasFlag(KeyModifiers.Shift))
            parts.Add("shift");
        if (modifiers.HasFlag(KeyModifiers.Meta))
            parts.Add(mac ? "cmd" : "win");
        parts.Add(action);
        return Chords.Normalise(string.Join('+', parts));
    }

    private string Describe(string chord) => Chords.Describe(chord, Session?.Pack.IsMac ?? false, pack.LegendFor);
}
