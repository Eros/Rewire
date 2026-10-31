using System.Text.Json;
using Rewire.Core.Keyboards;

namespace Rewire.Core.Layouts;

public enum PresetState
{
    NotApplied,
    PartlyApplied,
    Applied,
}

/// <summary>A one-click bundle of remaps, such as Esc on Caps Lock, from a pack's <c>presets</c> folder.</summary>
public sealed record Preset(string Id, string Name, string Description, IReadOnlyList<Remap> Remaps)
{
    public const string FolderName = "presets";

    public static IReadOnlyList<Preset> Load(string packDirectory)
    {
        var directory = Path.Combine(packDirectory, FolderName);
        if (!Directory.Exists(directory))
            return [];

        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        return Directory.EnumerateFiles(directory, "*.json")
            .Order(StringComparer.Ordinal)
            .Select(path =>
            {
                var file = JsonSerializer.Deserialize<PresetJson>(File.ReadAllText(path), options)
                    ?? throw new InvalidDataException($"{path} is empty.");
                return new Preset(
                    Path.GetFileNameWithoutExtension(path),
                    file.Name ?? Path.GetFileNameWithoutExtension(path),
                    file.Description ?? "",
                    (file.Remaps ?? []).Select(remap => new Remap(new LocationToken(remap.Location), new ActionToken(remap.Action))).ToList());
            })
            .ToList();
    }

    public PresetState StateIn(EffectiveLayout layout)
    {
        var matching = Remaps.Count(remap => Find(layout, remap) is { } key
            && string.Equals(key.Action, remap.Action.Name, StringComparison.OrdinalIgnoreCase));
        return matching == Remaps.Count ? PresetState.Applied : matching == 0 ? PresetState.NotApplied : PresetState.PartlyApplied;
    }

    /// <summary>Keys this preset would change that the user has already remapped to something else.</summary>
    public IReadOnlyList<EffectiveKey> Overrides(EffectiveLayout layout) =>
        Remaps.Select(remap => (Remap: remap, Key: Find(layout, remap)))
            .Where(pair => pair.Key is { Change: not KeyChange.None } key
                && !string.Equals(key.Action, pair.Remap.Action.Name, StringComparison.OrdinalIgnoreCase))
            .Select(pair => pair.Key!)
            .ToList();

    public LayoutFile Apply(LayoutFile file, KeyboardPack pack)
    {
        foreach (var remap in Remaps)
        {
            if (Locate(pack, remap.Location.Name) is var (layerId, keyId))
                file = LayoutEditor.SetAction(file, pack, layerId, keyId, remap.Action.Name);
        }

        return file;
    }

    /// <summary>Puts back the defaults of every key that still does what this preset set, leaving keys changed since alone.</summary>
    public LayoutFile Remove(LayoutFile file, KeyboardPack pack)
    {
        var layout = EffectiveLayout.Build(pack, file);
        foreach (var remap in Remaps)
        {
            if (Locate(pack, remap.Location.Name) is not var (layerId, keyId)
                || layout.Find(layerId, keyId) is not { } key
                || !string.Equals(key.Action, remap.Action.Name, StringComparison.OrdinalIgnoreCase))
                continue;

            file = LayoutEditor.ResetToDefault(file, pack, layerId, keyId);
        }

        return file;
    }

    private static EffectiveKey? Find(EffectiveLayout layout, Remap remap) =>
        Locate(layout.Pack, remap.Location.Name) is var (layerId, keyId) ? layout.Find(layerId, keyId) : null;

    private static (string LayerId, string KeyId)? Locate(KeyboardPack pack, string location)
    {
        foreach (var layer in pack.Layers)
        {
            if (layer.KeyIdFor(location) is { } keyId)
                return (layer.Id, keyId);
        }

        return null;
    }

    private sealed record PresetJson(string? Name, string? Description, List<RemapJson>? Remaps);

    private sealed record RemapJson(string Location, string Action);
}
