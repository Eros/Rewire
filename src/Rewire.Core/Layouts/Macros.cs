using Rewire.Core.Keyboards;

namespace Rewire.Core.Layouts;

public enum MacroTokenKind
{
    /// <summary>Taps a key, such as <c>{h}</c>.</summary>
    Key,

    /// <summary>Holds a modifier down, such as <c>{-lshift}</c>.</summary>
    Press,

    /// <summary>Lets a held modifier go, such as <c>{+lshift}</c>.</summary>
    Release,

    /// <summary>Sets the playback speed from here on, <c>{speed1}</c> to <c>{speed9}</c>.</summary>
    Speed,

    /// <summary>Pauses playback, <c>{d125}</c> or <c>{d500}</c>.</summary>
    Delay,
}

/// <summary>A macro playback token read for its meaning, following the Kinesis manual's direct-programming section.</summary>
public readonly record struct MacroToken(MacroTokenKind Kind, string Value)
{
    public static MacroToken Read(ActionToken token)
    {
        var name = token.Name;
        if (name.Length > 1 && name[0] == '-')
            return new MacroToken(MacroTokenKind.Press, name[1..]);
        if (name.Length > 1 && name[0] == '+')
            return new MacroToken(MacroTokenKind.Release, name[1..]);
        if (name.StartsWith("speed", StringComparison.OrdinalIgnoreCase) && name.Length == 6 && char.IsAsciiDigit(name[5]))
            return new MacroToken(MacroTokenKind.Speed, name[5..]);
        if (name.Length > 1 && (name[0] is 'd' or 'D') && name[1..].All(char.IsAsciiDigit))
            return new MacroToken(MacroTokenKind.Delay, name[1..]);
        return new MacroToken(MacroTokenKind.Key, name);
    }

    public ActionToken ToAction() => new(Kind switch
    {
        MacroTokenKind.Press => $"-{Value}",
        MacroTokenKind.Release => $"+{Value}",
        MacroTokenKind.Speed => $"speed{Value}",
        MacroTokenKind.Delay => $"d{Value}",
        _ => Value,
    });
}

public enum MacroProblemSeverity
{
    /// <summary>The keyboard will reject or misplay the macro.</summary>
    Error,

    /// <summary>The macro will work, but probably not as the user expects.</summary>
    Warning,
}

public sealed record MacroProblem(MacroProblemSeverity Severity, string Message);

public static class Macros
{
    /// <summary>The manual's guide to how many keystrokes one macro can hold.</summary>
    public const int MaximumLength = 200;

    public static readonly IReadOnlySet<string> Modifiers = new HashSet<string>(
        ["lshift", "rshift", "lctrl", "rctrl", "lalt", "ralt", "lwin", "rwin"], StringComparer.OrdinalIgnoreCase);

    private static readonly IReadOnlySet<string> Delays = new HashSet<string> { "125", "500" };

    /// <summary>
    /// Checks a macro against the manual's rules and against the rest of the layout: what may trigger it,
    /// which delays and speeds exist, balanced modifier holds, its length, and clashes with other lines.
    /// </summary>
    public static IReadOnlyList<MacroProblem> Check(Macro macro, LayoutFile file, KeyboardPack pack)
    {
        var problems = new List<MacroProblem>();

        var triggerKeys = macro.Trigger.Where(token => !IsModifierLocation(token.Name)).ToList();
        if (triggerKeys.Count == 0)
            problems.Add(Error("A macro needs a trigger key besides modifiers; a modifier on its own cannot trigger one."));
        else if (triggerKeys.Count > 1)
            problems.Add(Error("A macro is triggered by one key, optionally held with modifiers, not by several keys."));

        if (macro.Playback.Count == 0)
            problems.Add(Error("The macro plays nothing yet."));
        else if (macro.Playback.Count > MaximumLength)
            problems.Add(Warning($"At {macro.Playback.Count} steps this macro is longer than the roughly {MaximumLength} the keyboard can store."));

        var held = new List<string>();
        foreach (var token in macro.Playback.Select(MacroToken.Read))
        {
            switch (token.Kind)
            {
                case MacroTokenKind.Delay when !Delays.Contains(token.Value):
                    problems.Add(Error($"The keyboard only has 125 ms and 500 ms delays, not {token.Value} ms."));
                    break;
                case MacroTokenKind.Speed when token.Value == "0":
                    problems.Add(Error("Macro speeds run from 1 to 9."));
                    break;
                case MacroTokenKind.Press:
                    held.Add(token.Value);
                    break;
                case MacroTokenKind.Release when !held.Remove(token.Value):
                    problems.Add(Warning($"{Legend(pack, token.Value)} is let go without having been held."));
                    break;
            }
        }

        foreach (var modifier in held.Distinct(StringComparer.OrdinalIgnoreCase))
            problems.Add(Warning($"{Legend(pack, modifier)} is held down and never let go, so it stays pressed after the macro."));

        problems.AddRange(Collisions(macro, file, pack));
        return problems;
    }

    /// <summary>Adds the macro, replacing any existing macro with exactly the same trigger.</summary>
    public static LayoutFile Set(LayoutFile file, Macro macro)
    {
        var existing = IndexOf(file, macro.Trigger);
        return existing < 0 ? file.Add(macro) : file.Replace(existing, macro);
    }

    public static LayoutFile Remove(LayoutFile file, IReadOnlyList<LocationToken> trigger)
    {
        var existing = IndexOf(file, trigger);
        return existing < 0 ? file : file.RemoveAt(existing);
    }

    /// <summary>The macro with exactly this trigger, ignoring case and the order of modifiers.</summary>
    public static int IndexOf(LayoutFile file, IReadOnlyList<LocationToken> trigger)
    {
        var wanted = TriggerKey(trigger);
        for (var i = 0; i < file.Count; i++)
        {
            if (file[i] is Macro macro && TriggerKey(macro.Trigger) == wanted)
                return i;
        }

        return -1;
    }

    private static IEnumerable<MacroProblem> Collisions(Macro macro, LayoutFile file, KeyboardPack pack)
    {
        var index = IndexOf(file, macro.Trigger);
        var others = Enumerable.Range(0, file.Count).Where(i => i != index).Select(i => file[i]);

        // A trigger with no key, or several, is already reported as an error and cannot clash meaningfully.
        var triggerKeys = macro.Trigger.Where(token => !IsModifierLocation(token.Name)).ToList();
        if (triggerKeys is not [{ Name: var triggerKey }])
            yield break;

        foreach (var line in others)
        {
            switch (line)
            {
                case Remap remap when string.Equals(remap.Location.Name, triggerKey, StringComparison.OrdinalIgnoreCase)
                                      && macro.Trigger.Count == 1:
                    yield return Warning($"This key is also remapped to {pack.LegendFor(remap.Action.Name)}; the macro takes priority, so the remap will not be used.");
                    break;
                case Macro other when TriggerKey(other.Trigger) == TriggerKey(macro.Trigger):
                    yield return Warning("Another macro already uses this trigger; the keyboard plays only the last one.");
                    break;
            }
        }
    }

    // Modifier locations on either layer, such as lshift or kp-lshift.
    private static bool IsModifierLocation(string location) =>
        Modifiers.Contains(location.StartsWith("kp-", StringComparison.OrdinalIgnoreCase) ? location[3..] : location);

    private static string TriggerKey(IEnumerable<LocationToken> trigger) =>
        string.Join('+', trigger.Select(token => token.Name.ToLowerInvariant()).Order(StringComparer.Ordinal));

    private static string Legend(KeyboardPack pack, string action) => pack.LegendFor(action);

    private static MacroProblem Error(string message) => new(MacroProblemSeverity.Error, message);

    private static MacroProblem Warning(string message) => new(MacroProblemSeverity.Warning, message);
}
