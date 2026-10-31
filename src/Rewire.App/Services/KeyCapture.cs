using Avalonia.Input;

namespace Rewire.Services;

/// <summary>
/// Turns a key pressed on the computer into the Advantage2 action token for the same key,
/// so a remap can be chosen by pressing the key it should produce.
/// </summary>
/// <remarks>Physical key codes are used, so the result does not depend on the OS keyboard language.</remarks>
public static class KeyCapture
{
    private static readonly Dictionary<PhysicalKey, string> Tokens = Build();

    public static string? ActionFor(PhysicalKey key) => Tokens.GetValueOrDefault(key);

    /// <summary>Every action a key press on the computer can report, which is everything the trainer can drill.</summary>
    public static IReadOnlySet<string> Actions { get; } = Tokens.Values.ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static Dictionary<PhysicalKey, string> Build()
    {
        var tokens = new Dictionary<PhysicalKey, string>
        {
            [PhysicalKey.Backquote] = "`",
            [PhysicalKey.Backslash] = @"\",
            [PhysicalKey.BracketLeft] = "obrack",
            [PhysicalKey.BracketRight] = "cbrack",
            [PhysicalKey.Comma] = ",",
            [PhysicalKey.Equal] = "=",
            [PhysicalKey.IntlBackslash] = @"intl-\",
            [PhysicalKey.Minus] = "hyphen",
            [PhysicalKey.Period] = ".",
            [PhysicalKey.Quote] = "'",
            [PhysicalKey.Semicolon] = ";",
            [PhysicalKey.Slash] = "/",
            [PhysicalKey.AltLeft] = "lalt",
            [PhysicalKey.AltRight] = "ralt",
            [PhysicalKey.Backspace] = "bspace",
            [PhysicalKey.CapsLock] = "caps",
            [PhysicalKey.ContextMenu] = "menu",
            [PhysicalKey.ControlLeft] = "lctrl",
            [PhysicalKey.ControlRight] = "rctrl",
            [PhysicalKey.Enter] = "enter",
            [PhysicalKey.MetaLeft] = "lwin",
            [PhysicalKey.MetaRight] = "rwin",
            [PhysicalKey.ShiftLeft] = "lshift",
            [PhysicalKey.ShiftRight] = "rshift",
            [PhysicalKey.Space] = "space",
            [PhysicalKey.Tab] = "tab",
            [PhysicalKey.Delete] = "delete",
            [PhysicalKey.End] = "end",
            [PhysicalKey.Home] = "home",
            [PhysicalKey.Insert] = "insert",
            [PhysicalKey.PageDown] = "pdown",
            [PhysicalKey.PageUp] = "pup",
            [PhysicalKey.ArrowDown] = "down",
            [PhysicalKey.ArrowLeft] = "left",
            [PhysicalKey.ArrowRight] = "right",
            [PhysicalKey.ArrowUp] = "up",
            [PhysicalKey.NumLock] = "numlk",
            [PhysicalKey.NumPadAdd] = "kpplus",
            [PhysicalKey.NumPadDecimal] = "kp.",
            [PhysicalKey.NumPadDivide] = "kpdiv",
            [PhysicalKey.NumPadEnter] = "kpenter1",
            [PhysicalKey.NumPadEqual] = "kp=mac",
            [PhysicalKey.NumPadMultiply] = "kpmult",
            [PhysicalKey.NumPadSubtract] = "kpmin",
            [PhysicalKey.Escape] = "escape",
            [PhysicalKey.PrintScreen] = "prtscr",
            [PhysicalKey.ScrollLock] = "scroll",
            [PhysicalKey.Pause] = "pause",
            [PhysicalKey.MediaPlayPause] = "play",
            [PhysicalKey.MediaTrackNext] = "next",
            [PhysicalKey.MediaTrackPrevious] = "prev",
            [PhysicalKey.AudioVolumeDown] = "vol-",
            [PhysicalKey.AudioVolumeMute] = "mute",
            [PhysicalKey.AudioVolumeUp] = "vol+",
        };

        for (var letter = 'A'; letter <= 'Z'; letter++)
            tokens[Enum.Parse<PhysicalKey>(letter.ToString())] = char.ToLowerInvariant(letter).ToString();
        for (var digit = 0; digit <= 9; digit++)
        {
            tokens[Enum.Parse<PhysicalKey>($"Digit{digit}")] = digit.ToString();
            tokens[Enum.Parse<PhysicalKey>($"NumPad{digit}")] = $"kp{digit}";
        }

        for (var number = 1; number <= 24; number++)
            tokens[Enum.Parse<PhysicalKey>($"F{number}")] = $"f{number}";

        return tokens;
    }
}
