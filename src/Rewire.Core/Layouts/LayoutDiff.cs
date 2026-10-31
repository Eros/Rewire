using Rewire.Core.Keyboards;

namespace Rewire.Core.Layouts;

public enum LayoutChangeKind
{
    Added,
    Removed,
    Changed,
}

/// <summary>One difference between two versions of a layout file, described for the user.</summary>
/// <param name="Description">Plain language, such as "Caps → Esc" or "Keypad layer: J back to 4".</param>
public sealed record LayoutChange(LayoutChangeKind Kind, LayoutLine? Before, LayoutLine? After, string Description);

public static class LayoutDiff
{
    public static IReadOnlyList<LayoutChange> Compare(LayoutFile before, LayoutFile after, KeyboardPack pack)
    {
        var removed = new List<LayoutLine>();
        var added = new List<LayoutLine>();
        foreach (var (line, inBefore) in LineDifferences(before.Lines, after.Lines))
            (inBefore ? removed : added).Add(line);

        var describer = new Describer(pack);
        var changes = new List<LayoutChange>();

        // A removed and an added line for the same key read better as one change to that key.
        foreach (var line in added)
        {
            var subject = Subject(line);
            var previous = subject is null ? null : removed.FirstOrDefault(old => string.Equals(Subject(old), subject, StringComparison.OrdinalIgnoreCase));
            if (previous is not null)
            {
                removed.Remove(previous);
                changes.Add(new LayoutChange(LayoutChangeKind.Changed, previous, line, describer.Changed(previous, line)));
            }
            else
            {
                changes.Add(new LayoutChange(LayoutChangeKind.Added, null, line, describer.Added(line)));
            }
        }

        changes.AddRange(removed.Select(line => new LayoutChange(LayoutChangeKind.Removed, line, null, describer.Removed(line))));
        return changes;
    }

    // The key a line configures; macros are keyed by their whole trigger, since a key can have several.
    private static string? Subject(LayoutLine line) => line switch
    {
        Remap remap => remap.Location.Name,
        TapHold tapHold => tapHold.Location.Name,
        Macro macro => string.Concat(macro.Trigger.Select(token => $"{{{token.Name}}}")),
        _ => null,
    };

    // A longest-common-subsequence diff, so lines that merely moved are not reported twice.
    private static IEnumerable<(LayoutLine Line, bool InBefore)> LineDifferences(IReadOnlyList<LayoutLine> before, IReadOnlyList<LayoutLine> after)
    {
        var common = new int[before.Count + 1, after.Count + 1];
        for (var i = before.Count - 1; i >= 0; i--)
        for (var j = after.Count - 1; j >= 0; j--)
        {
            common[i, j] = before[i].Equals(after[j])
                ? common[i + 1, j + 1] + 1
                : Math.Max(common[i + 1, j], common[i, j + 1]);
        }

        int b = 0, a = 0;
        while (b < before.Count && a < after.Count)
        {
            if (before[b].Equals(after[a]))
            {
                b++;
                a++;
            }
            else if (common[b + 1, a] >= common[b, a + 1])
                yield return (before[b++], true);
            else
                yield return (after[a++], false);
        }

        while (b < before.Count)
            yield return (before[b++], true);
        while (a < after.Count)
            yield return (after[a++], false);
    }

    private sealed class Describer(KeyboardPack pack)
    {
        public string Added(LayoutLine line) => line switch
        {
            Remap remap => $"{Key(remap.Location)} → {Legend(remap.Action)}",
            TapHold tapHold => $"{Key(tapHold.Location)} → {Legend(tapHold.Tap)} when tapped, {Legend(tapHold.Hold)} when held",
            Macro macro => $"New macro on {Trigger(macro)}",
            UnknownLine { RawText: var text } when string.IsNullOrWhiteSpace(text) => "Blank line added",
            _ => $"Unrecognised line added: {line.Render()}",
        };

        public string Removed(LayoutLine line) => line switch
        {
            Remap remap => $"{Key(remap.Location)} back to {Default(remap.Location)}",
            TapHold tapHold => $"{Key(tapHold.Location)} back to {Default(tapHold.Location)}",
            Macro macro => $"Macro on {Trigger(macro)} removed",
            UnknownLine { RawText: var text } when string.IsNullOrWhiteSpace(text) => "Blank line removed",
            _ => $"Unrecognised line removed: {line.Render()}",
        };

        public string Changed(LayoutLine before, LayoutLine after) => (before, after) switch
        {
            (Macro, Macro macro) => $"Macro on {Trigger(macro)} changed",
            _ => $"{Key(Location(after))}: {Action(before)} → {Action(after)}",
        };

        private string Action(LayoutLine line) => line switch
        {
            Remap remap => Legend(remap.Action),
            TapHold tapHold => $"{Legend(tapHold.Tap)} (tap), {Legend(tapHold.Hold)} (hold)",
            _ => line.Render(),
        };

        private static LocationToken Location(LayoutLine line) => line switch
        {
            Remap remap => remap.Location,
            TapHold tapHold => tapHold.Location,
            _ => default,
        };

        private string Trigger(Macro macro) => string.Join("+", macro.Trigger.Select(Key));

        // Keys are named by their keycap, with the layer added when it is not the top one.
        private string Key(LocationToken location)
        {
            foreach (var layer in pack.Layers)
            {
                if (layer.KeyIdFor(location.Name) is not { } keyId)
                    continue;

                var keycap = pack.LegendFor(pack.Layers[0].Keys.TryGetValue(keyId, out var top) ? top.Action : keyId);
                return layer == pack.Layers[0] ? keycap : $"{layer.Name}: {keycap}";
            }

            return location.Name;
        }

        private string Default(LocationToken location)
        {
            foreach (var layer in pack.Layers)
            {
                if (layer.KeyIdFor(location.Name) is { } keyId)
                    return pack.LegendFor(layer.Keys[keyId].Action);
            }

            return "its default";
        }

        private string Legend(ActionToken action) => pack.LegendFor(action.Name);
    }
}
