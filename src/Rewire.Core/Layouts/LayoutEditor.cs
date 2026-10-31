using Rewire.Core.Keyboards;

namespace Rewire.Core.Layouts;

/// <summary>
/// The edits the configurator makes, expressed as changes to a layout file so that every
/// line the user did not touch stays exactly as it was.
/// </summary>
public static class LayoutEditor
{
    /// <summary>
    /// Makes a key perform <paramref name="action"/>, replacing any remap or tap-and-hold it already has.
    /// Choosing the key's default action removes its remap altogether.
    /// </summary>
    public static LayoutFile SetAction(LayoutFile file, KeyboardPack pack, string layerId, string keyId, string action)
    {
        var entry = pack.Layer(layerId).Keys[keyId];
        var existing = LinesFor(file, entry.Location);
        var isDefault = string.Equals(action, entry.Action, StringComparison.OrdinalIgnoreCase);

        if (existing.Count == 0)
            return isDefault ? file : file.Add(new Remap(new LocationToken(entry.Location), new ActionToken(action)));

        // The last line for a location is the one that takes effect, so it is the one rewritten in place;
        // earlier duplicates would only mislead whoever reads the file next.
        var last = existing[^1];
        var edited = isDefault
            ? file.RemoveAt(last)
            : file.Replace(last, ReplacementFor(file[last], entry.Location, action));

        foreach (var index in existing.SkipLast(1).Reverse())
            edited = edited.RemoveAt(index);
        return edited;
    }

    public static LayoutFile ResetToDefault(LayoutFile file, KeyboardPack pack, string layerId, string keyId) =>
        SetAction(file, pack, layerId, keyId, pack.Layer(layerId).Keys[keyId].Action);

    /// <summary>Swaps what two keys on the same layer do.</summary>
    public static LayoutFile Swap(LayoutFile file, KeyboardPack pack, string layerId, string firstKeyId, string secondKeyId)
    {
        var layout = EffectiveLayout.Build(pack, file);
        var first = layout.Find(layerId, firstKeyId)!.Action;
        var second = layout.Find(layerId, secondKeyId)!.Action;

        var swapped = SetAction(file, pack, layerId, firstKeyId, second);
        return SetAction(swapped, pack, layerId, secondKeyId, first);
    }

    // Keeps the line's own spelling of the location, so only the action part of it changes.
    private static Remap ReplacementFor(LayoutLine line, string location, string action)
    {
        var token = line switch
        {
            Remap remap => remap.Location,
            TapHold tapHold => tapHold.Location,
            _ => new LocationToken(location),
        };
        return new Remap(token, new ActionToken(action));
    }

    private static List<int> LinesFor(LayoutFile file, string location)
    {
        var indices = new List<int>();
        for (var i = 0; i < file.Count; i++)
        {
            var lineLocation = file[i] switch
            {
                Remap remap => remap.Location.Name,
                TapHold tapHold => tapHold.Location.Name,
                _ => null,
            };
            if (string.Equals(lineLocation, location, StringComparison.OrdinalIgnoreCase))
                indices.Add(i);
        }

        return indices;
    }
}
