namespace Rewire.Trainer;

public enum PressResult
{
    Correct,
    Wrong,

    /// <summary>A modifier pressed on its own, or a press after the drill ended, which is not judged.</summary>
    Ignored,
}

/// <param name="PressedKeyId">The physical key that sends the pressed action, when the layout has one.</param>
/// <param name="Latency">Time since the previous correct press, or since the first press for the first target.</param>
/// <param name="OldHabit">For a wrong press, the old keyboard's key the user was reaching for, if that explains it.</param>
public sealed record Keystroke(
    DateTimeOffset At,
    string IntendedKeyId,
    string IntendedAction,
    string? PressedKeyId,
    string PressedAction,
    TimeSpan Latency,
    bool Correct,
    int Position,
    string? OldHabit = null);

/// <summary>
/// One run through a drill. A wrong key is recorded and the cursor waits for the right one,
/// so the user always finishes by typing every target correctly.
/// </summary>
public sealed class TypingSession(
    Drill drill,
    Func<string, string?> keyForAction,
    TimeProvider? time = null,
    OldHabitDetector? oldHabits = null)
{
    private static readonly HashSet<string> Modifiers = new(
        ["lshift", "rshift", "lctrl", "rctrl", "lalt", "ralt", "lwin", "rwin"],
        StringComparer.OrdinalIgnoreCase);

    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly List<Keystroke> _keystrokes = [];
    private readonly HashSet<int> _missed = [];
    private DateTimeOffset? _lastCorrectAt;

    public Drill Drill { get; } = drill;

    public IReadOnlyList<DrillTarget> Targets => Drill.Targets;

    public int Position { get; private set; }

    public DrillTarget? Current => Position < Targets.Count && !IsTimeUp ? Targets[Position] : null;

    public DateTimeOffset? StartedAt { get; private set; }

    public DateTimeOffset? EndedAt { get; private set; }

    public IReadOnlyList<Keystroke> Keystrokes => _keystrokes;

    /// <summary>The most recent press, so a view can explain a mistake the moment it happens.</summary>
    public Keystroke? LastKeystroke => _keystrokes.Count > 0 ? _keystrokes[^1] : null;

    /// <summary>Positions where at least one wrong key was pressed before the right one.</summary>
    public IReadOnlySet<int> Missed => _missed;

    public bool IsComplete => EndedAt is not null;

    public TimeSpan Elapsed => StartedAt is { } start ? (EndedAt ?? _time.GetUtcNow()) - start : TimeSpan.Zero;

    private bool IsTimeUp => Drill.Stage.TimeLimit is { } limit && Elapsed >= limit;

    /// <summary>Words per minute, counting five correct targets as a word.</summary>
    public double WordsPerMinute => Elapsed.TotalMinutes > 0 ? Position / 5.0 / Elapsed.TotalMinutes : 0;

    public double Accuracy
    {
        get
        {
            var judged = _keystrokes.Count;
            return judged == 0 ? 1 : (double)_keystrokes.Count(keystroke => keystroke.Correct) / judged;
        }
    }

    public PressResult Press(string action)
    {
        if (IsComplete)
            return PressResult.Ignored;

        if (IsTimeUp)
        {
            Finish();
            return PressResult.Ignored;
        }

        var target = Targets[Position];
        var matches = string.Equals(action, target.Action, StringComparison.OrdinalIgnoreCase);
        if (!matches && Modifiers.Contains(action))
            return PressResult.Ignored;

        var now = _time.GetUtcNow();
        StartedAt ??= now;
        var latency = now - (_lastCorrectAt ?? StartedAt.Value);

        // A key sending the right action counts as the right key, even where several keys share it.
        var pressedKeyId = matches ? target.KeyId : keyForAction(action);
        var oldHabit = matches ? null : oldHabits?.Classify(target.Action, target.KeyId, pressedKeyId);
        _keystrokes.Add(new Keystroke(now, target.KeyId, target.Action, pressedKeyId, action, latency, matches, Position, oldHabit));

        if (!matches)
        {
            _missed.Add(Position);
            return PressResult.Wrong;
        }

        _lastCorrectAt = now;
        Position++;
        if (Position == Targets.Count)
            Finish();
        return PressResult.Correct;
    }

    /// <summary>Ends a timed drill once its time is up, even if no key is pressed after that.</summary>
    public bool CheckTime()
    {
        if (!IsComplete && IsTimeUp)
            Finish();
        return IsComplete;
    }

    private void Finish()
    {
        var now = _time.GetUtcNow();
        EndedAt = Drill.Stage.TimeLimit is { } limit && StartedAt is { } start && now - start > limit ? start + limit : now;
    }
}
