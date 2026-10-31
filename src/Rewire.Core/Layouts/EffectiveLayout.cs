using Rewire.Core.Keyboards;

namespace Rewire.Core.Layouts;

public enum KeyChange
{
    None,
    Remapped,
    TapHold,
}

/// <summary>What one physical key does on one layer once a layout file is applied.</summary>
/// <param name="Location">The token a layout file uses for this key on this layer.</param>
/// <param name="Macros">Macros this key triggers, alone or with modifiers.</param>
public sealed record EffectiveKey(
    PhysicalKey Key,
    string Location,
    string DefaultAction,
    string Action,
    KeyChange Change,
    TapHold? TapHold,
    IReadOnlyList<Macro> Macros);

/// <summary>
/// A keyboard's default layout with a layout file applied, answering "what does this physical key
/// do now?" for every key on every layer. The configurator and the trainer both read this.
/// </summary>
public sealed class EffectiveLayout
{
    // A macro can only be triggered by a non-modifier key, optionally held with modifiers.
    private static readonly HashSet<string> ModifierLocations = new(
        ["lshift", "rshift", "lctrl", "rctrl", "lalt", "ralt", "lwin", "rwin"],
        StringComparer.OrdinalIgnoreCase);

    private readonly Dictionary<(string Layer, string KeyId), EffectiveKey> _keys;

    private EffectiveLayout(KeyboardPack pack, Dictionary<(string, string), EffectiveKey> keys, IReadOnlyList<LayoutLine> unplaced)
    {
        Pack = pack;
        _keys = keys;
        UnplacedLines = unplaced;
    }

    public KeyboardPack Pack { get; }

    /// <summary>Lines whose location matches no key on any layer, such as foot pedals the pack does not draw.</summary>
    public IReadOnlyList<LayoutLine> UnplacedLines { get; }

    public EffectiveKey? Find(string layerId, string keyId) => _keys.GetValueOrDefault((layerId, keyId));

    public static EffectiveLayout Build(KeyboardPack pack, LayoutFile file)
    {
        var keys = new Dictionary<(string, string), EffectiveKey>();
        foreach (var layer in pack.Layers)
        {
            foreach (var (keyId, entry) in layer.Keys)
                keys[(layer.Id, keyId)] = new EffectiveKey(pack.Key(keyId), entry.Location, entry.Action, entry.Action, KeyChange.None, null, []);
        }

        var unplaced = new List<LayoutLine>();

        // Later lines win, matching the order in which the keyboard reads the file.
        foreach (var line in file.Lines)
        {
            var location = line switch
            {
                Remap remap => remap.Location.Name,
                TapHold tapHold => tapHold.Location.Name,
                Macro macro => TriggerKey(macro).Name,
                _ => null,
            };
            if (location is null)
                continue;

            if (Locate(pack, location) is not { } place)
            {
                unplaced.Add(line);
                continue;
            }

            var current = keys[place];
            keys[place] = line switch
            {
                Remap remap => current with { Action = remap.Action.Name, Change = KeyChange.Remapped, TapHold = null },
                TapHold tapHold => current with { Action = tapHold.Tap.Name, Change = KeyChange.TapHold, TapHold = tapHold },
                Macro macro => current with { Macros = [.. current.Macros, macro] },
                _ => current,
            };
        }

        return new EffectiveLayout(pack, keys, unplaced);
    }

    private static (string LayerId, string KeyId)? Locate(KeyboardPack pack, string location)
    {
        foreach (var layer in pack.Layers)
        {
            if (layer.KeyIdFor(location) is { } keyId)
                return (layer.Id, keyId);
        }

        return null;
    }

    private static LocationToken TriggerKey(Macro macro)
    {
        foreach (var token in macro.Trigger)
        {
            var name = token.Name.StartsWith("kp-", StringComparison.OrdinalIgnoreCase) ? token.Name[3..] : token.Name;
            if (!ModifierLocations.Contains(name))
                return token;
        }

        return macro.Trigger[^1];
    }
}
