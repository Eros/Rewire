using System.Text;

namespace Rewire.Device.Advantage2;

/// <summary>
/// The keyboard's <c>state.txt</c>: thumb mode, macro speed, startup layout and similar settings.
/// </summary>
/// <remarks>
/// The keyboard owns this file, so Rewire only ever reads it. Settings are kept as raw
/// key-value pairs until each key's meaning is confirmed against the Kinesis manual.
/// </remarks>
public sealed class StateFile
{
    private readonly Dictionary<string, string> _settings;

    private StateFile(Dictionary<string, string> settings) => _settings = settings;

    public IReadOnlyDictionary<string, string> Settings => _settings;

    public string? this[string key] => _settings.GetValueOrDefault(key);

    public static StateFile Parse(ReadOnlySpan<byte> bytes)
    {
        var settings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in Encoding.Latin1.GetString(bytes).Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = line.IndexOf('=');
            if (separator > 0)
                settings[line[..separator].Trim()] = line[(separator + 1)..].Trim();
        }

        return new StateFile(settings);
    }
}
