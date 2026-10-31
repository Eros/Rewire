using System.Text.Json;
using System.Text.Json.Serialization;

namespace Rewire.Core.Keyboards;

public sealed class KeyboardPackException(string packDirectory, IReadOnlyList<string> problems)
    : Exception($"The keyboard pack in {packDirectory} has problems:{Environment.NewLine}  {string.Join($"{Environment.NewLine}  ", problems)}")
{
    public IReadOnlyList<string> Problems { get; } = problems;
}

/// <summary>
/// Reads a keyboard pack folder: <c>keyboard.json</c> for the physical keys and
/// <c>default-layout.json</c> for the factory layers and legends.
/// </summary>
public static class KeyboardPackLoader
{
    public const string KeyboardFileName = "keyboard.json";
    public const string LayoutFileName = "default-layout.json";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>Every pack found in the subfolders of <paramref name="keyboardsDirectory"/>.</summary>
    public static IReadOnlyList<KeyboardPack> LoadAll(string keyboardsDirectory) =>
        Directory.EnumerateDirectories(keyboardsDirectory)
            .Where(directory => File.Exists(Path.Combine(directory, KeyboardFileName)))
            .Order(StringComparer.Ordinal)
            .Select(Load)
            .ToList();

    public static KeyboardPack Load(string packDirectory)
    {
        var keyboard = Read<KeyboardJson>(Path.Combine(packDirectory, KeyboardFileName));
        var layout = Read<DefaultLayoutJson>(Path.Combine(packDirectory, LayoutFileName));

        var problems = new List<string>();
        var keys = ReadKeys(keyboard, problems);
        var layers = ReadLayers(layout, keys, problems);

        if (string.IsNullOrWhiteSpace(keyboard.Id))
            problems.Add("keyboard.json has no id.");
        else if (keyboard.Id != Path.GetFileName(Path.TrimEndingDirectorySeparator(packDirectory)))
            problems.Add($"The pack id \"{keyboard.Id}\" does not match its folder name.");

        if (problems.Count > 0)
            throw new KeyboardPackException(packDirectory, problems);

        return new KeyboardPack(
            keyboard.Id!,
            keyboard.Name ?? keyboard.Id!,
            keyboard.Vendor ?? "",
            keyboard.Backend,
            keys,
            layers,
            layout.Legends ?? []);
    }

    private static List<PhysicalKey> ReadKeys(KeyboardJson keyboard, List<string> problems)
    {
        var keys = new List<PhysicalKey>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var key in keyboard.Keys ?? [])
        {
            if (string.IsNullOrWhiteSpace(key.Id))
            {
                problems.Add("A key has no id.");
                continue;
            }

            if (!seen.Add(key.Id))
                problems.Add($"Key \"{key.Id}\" appears more than once.");
            if (key.Hand is null)
                problems.Add($"Key \"{key.Id}\" has no hand.");
            if (key.Finger is null)
                problems.Add($"Key \"{key.Id}\" has no finger.");
            if (key.Cluster is null)
                problems.Add($"Key \"{key.Id}\" has no cluster.");
            var width = key.Width ?? 1;
            var height = key.Height ?? 1;
            if (width <= 0 || height <= 0)
                problems.Add($"Key \"{key.Id}\" has a size of zero or less.");

            if (key is { Hand: { } hand, Finger: { } finger, Cluster: { } cluster })
            {
                keys.Add(new PhysicalKey(
                    key.Id, key.X, key.Y, width, height, hand, finger, cluster,
                    key.Rotation, key.RotationOrigin?.ElementAtOrDefault(0) ?? 0, key.RotationOrigin?.ElementAtOrDefault(1) ?? 0,
                    key.Remappable ?? true));
            }
        }

        if (keys.Count == 0)
            problems.Add("keyboard.json has no keys.");
        return keys;
    }

    private static List<KeyboardLayer> ReadLayers(DefaultLayoutJson layout, List<PhysicalKey> keys, List<string> problems)
    {
        var keyIds = keys.Select(key => key.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var layers = new List<KeyboardLayer>();

        foreach (var layer in layout.Layers ?? [])
        {
            if (string.IsNullOrWhiteSpace(layer.Id))
            {
                problems.Add("A layer has no id.");
                continue;
            }

            var problemsBefore = problems.Count;
            var entries = new Dictionary<string, LayerKey>(StringComparer.OrdinalIgnoreCase);
            var locations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (keyId, entry) in layer.Keys ?? [])
            {
                if (!keyIds.Contains(keyId))
                    problems.Add($"Layer \"{layer.Id}\" refers to key \"{keyId}\", which keyboard.json does not have.");
                if (string.IsNullOrWhiteSpace(entry.Action))
                    problems.Add($"Layer \"{layer.Id}\" gives key \"{keyId}\" no action.");

                // Most location tokens are the key's own id, so the file only spells out the ones that differ.
                var location = entry.Location ?? keyId;
                if (!locations.Add(location))
                    problems.Add($"Layer \"{layer.Id}\" uses the location \"{location}\" more than once.");

                entries[keyId] = new LayerKey(location, entry.Action ?? "");
            }

            // A layer with problems may have clashing locations, which a KeyboardLayer cannot index.
            if (problems.Count == problemsBefore)
                layers.Add(new KeyboardLayer(layer.Id, layer.Name ?? layer.Id, entries));
        }

        if (layout.Layers is null or [])
        {
            problems.Add("default-layout.json has no layers.");
            return layers;
        }

        // The first layer is the base layer, which must give every remappable key an action.
        if (layers.FirstOrDefault()?.Id == layout.Layers[0].Id)
        {
            var baseLayer = layers[0];
            foreach (var key in keys.Where(key => key.Remappable && !baseLayer.Keys.ContainsKey(key.Id)))
                problems.Add($"Key \"{key.Id}\" is remappable but missing from the \"{baseLayer.Id}\" layer.");
        }

        return layers;
    }

    private static T Read<T>(string path)
    {
        try
        {
            return JsonSerializer.Deserialize<T>(File.ReadAllText(path), Json)
                ?? throw new KeyboardPackException(Path.GetDirectoryName(path)!, [$"{Path.GetFileName(path)} is empty."]);
        }
        catch (JsonException exception)
        {
            throw new KeyboardPackException(Path.GetDirectoryName(path)!, [$"{Path.GetFileName(path)}: {exception.Message}"]);
        }
    }

    private sealed record KeyboardJson(string? Id, string? Name, string? Vendor, string? Backend, List<KeyEntry>? Keys);

    private sealed record KeyEntry(
        string? Id,
        double X,
        double Y,
        double? Width,
        double? Height,
        Hand? Hand,
        Finger? Finger,
        Cluster? Cluster,
        double Rotation,
        double[]? RotationOrigin,
        bool? Remappable);

    private sealed record DefaultLayoutJson(List<LayerEntry>? Layers, Dictionary<string, string>? Legends);

    private sealed record LayerEntry(string? Id, string? Name, Dictionary<string, LayerKeyEntry>? Keys);

    private sealed record LayerKeyEntry(string? Location, string? Action);
}
