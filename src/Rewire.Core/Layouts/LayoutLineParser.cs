namespace Rewire.Core.Layouts;

public static class LayoutLineParser
{
    public static LayoutLine Parse(string text)
    {
        var content = text.Trim();
        var separator = content.IndexOf('>');
        if (separator < 0)
            return new UnknownLine(text);

        var left = ReadTokens(content.AsSpan(0, separator));
        var right = ReadTokens(content.AsSpan(separator + 1));
        if (left is null || right is null || left.Value.Open != right.Value.Open)
            return new UnknownLine(text);

        var (open, triggerNames) = left.Value;
        var playbackNames = right.Value.Names;

        if (open == '[')
        {
            if (triggerNames.Count != 1)
                return new UnknownLine(text);

            var location = new LocationToken(triggerNames[0]);
            return playbackNames switch
            {
                [var action] => new Remap(location, new ActionToken(action)),
                [var tap, var delay, var hold] when TryReadTapHoldDelay(delay, out var milliseconds) =>
                    new TapHold(location, new ActionToken(tap), milliseconds, new ActionToken(hold)),
                _ => new UnknownLine(text),
            };
        }

        return new Macro(
            triggerNames.Select(name => new LocationToken(name)).ToArray(),
            playbackNames.Select(name => new ActionToken(name)).ToArray());
    }

    // The manual allows 1 to 999 ms, written as t&h followed by the number.
    private static bool TryReadTapHoldDelay(string token, out int milliseconds)
    {
        milliseconds = 0;
        return token.StartsWith("t&h", StringComparison.OrdinalIgnoreCase)
            && token.Length is > 3 and <= 6
            && token.AsSpan(3).IndexOfAnyExceptInRange('0', '9') < 0
            && int.TryParse(token.AsSpan(3), out milliseconds)
            && milliseconds is >= 1 and <= 999;
    }

    // Reads a run of tokens that all use the same bracket style, with nothing between them.
    // Returns null for anything else so the caller can keep the line verbatim.
    private static (char Open, List<string> Names)? ReadTokens(ReadOnlySpan<char> text)
    {
        if (text.IsEmpty)
            return null;

        var open = text[0];
        var close = open switch
        {
            '[' => ']',
            '{' => '}',
            _ => '\0',
        };
        if (close == '\0')
            return null;

        var names = new List<string>();
        while (!text.IsEmpty)
        {
            if (text[0] != open)
                return null;

            var end = text.IndexOf(close);
            if (end <= 1)
                return null;

            var name = text[1..end];
            if (name.IndexOfAny('[', '{') >= 0)
                return null;

            names.Add(name.ToString());
            text = text[(end + 1)..];
        }

        return (open, names);
    }
}
