namespace Rewire.Core.Keyboards;

public enum Hand
{
    Left,
    Right,
}

public enum Finger
{
    Pinky,
    Ring,
    Middle,
    Index,
    Thumb,
}

public enum Cluster
{
    Main,
    Thumb,
    Function,
}

/// <summary>
/// One physical key. Positions and sizes are in key units; a rotated key is placed at (X, Y)
/// as if unrotated, then turned by <see cref="Rotation"/> degrees clockwise about the rotation origin,
/// as in keyboard-layout-editor.
/// </summary>
public sealed record PhysicalKey(
    string Id,
    double X,
    double Y,
    double Width,
    double Height,
    Hand Hand,
    Finger Finger,
    Cluster Cluster,
    double Rotation = 0,
    double RotationOriginX = 0,
    double RotationOriginY = 0,
    bool Remappable = true);

/// <summary>What a key does by default on one layer, and the token a layout file uses to name it there.</summary>
public sealed record LayerKey(string Location, string Action);

public sealed record KeyboardLayer(string Id, string Name, IReadOnlyDictionary<string, LayerKey> Keys)
{
    private readonly Dictionary<string, string> _keyIdsByLocation = Keys
        .ToDictionary(pair => pair.Value.Location, pair => pair.Key, StringComparer.OrdinalIgnoreCase);

    /// <summary>The physical key a layout file's location token refers to on this layer.</summary>
    public string? KeyIdFor(string location) => _keyIdsByLocation.GetValueOrDefault(location);
}

/// <summary>
/// Everything Rewire knows about one keyboard model: its physical keys, its factory layout
/// and the legends for its action tokens. Loaded from a folder under <c>keyboards/</c>.
/// </summary>
public sealed class KeyboardPack
{
    private readonly Dictionary<string, PhysicalKey> _keysById;
    private readonly Dictionary<string, string> _legends;

    public KeyboardPack(
        string id,
        string name,
        string vendor,
        string? backend,
        IReadOnlyList<PhysicalKey> keys,
        IReadOnlyList<KeyboardLayer> layers,
        IReadOnlyDictionary<string, string> legends)
    {
        Id = id;
        Name = name;
        Vendor = vendor;
        Backend = backend;
        Keys = keys;
        Layers = layers;
        _keysById = keys.ToDictionary(key => key.Id);
        _legends = new Dictionary<string, string>(legends, StringComparer.OrdinalIgnoreCase);
        KnownActions = layers.SelectMany(layer => layer.Keys.Values.Select(entry => entry.Action))
            .Concat(legends.Keys)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public string Id { get; }

    public string Name { get; }

    public string Vendor { get; }

    /// <summary>The config backend that reads and writes this keyboard, or null for a trainer-only pack.</summary>
    public string? Backend { get; }

    public IReadOnlyList<PhysicalKey> Keys { get; }

    public IReadOnlyList<KeyboardLayer> Layers { get; }

    /// <summary>Every action this keyboard's defaults or legends mention, for offering in a picker.</summary>
    public IReadOnlyList<string> KnownActions { get; }

    public PhysicalKey Key(string id) => _keysById[id];

    public KeyboardLayer Layer(string id) => Layers.Single(layer => layer.Id == id);

    /// <summary>The text to print on a key that performs <paramref name="action"/>.</summary>
    public string LegendFor(string action) =>
        _legends.TryGetValue(action, out var legend) ? legend
        : action.Length == 1 ? action.ToUpperInvariant()
        : action;
}
