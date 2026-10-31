using System.Text;

namespace Rewire.Core.Layouts;

public abstract record LayoutLine
{
    public abstract string Render();
}

/// <summary><c>[caps]>[escape]</c></summary>
public sealed record Remap(LocationToken Location, ActionToken Action) : LayoutLine
{
    public override string Render() => $"[{Location}]>[{Action}]";
}

/// <summary><c>[lshift]>[escape][t&amp;h250][lshift]</c>: escape when tapped, shift when held past the delay.</summary>
public sealed record TapHold(LocationToken Location, ActionToken Tap, int DelayMilliseconds, ActionToken Hold) : LayoutLine
{
    public override string Render() => $"[{Location}]>[{Tap}][t&h{DelayMilliseconds}][{Hold}]";
}

/// <summary><c>{lctrl}{f1}>{h}{i}</c></summary>
public sealed record Macro(IReadOnlyList<LocationToken> Trigger, IReadOnlyList<ActionToken> Playback) : LayoutLine
{
    public override string Render()
    {
        var text = new StringBuilder();
        foreach (var token in Trigger)
            text.Append('{').Append(token.Name).Append('}');
        text.Append('>');
        foreach (var token in Playback)
            text.Append('{').Append(token.Name).Append('}');
        return text.ToString();
    }

    public bool Equals(Macro? other) =>
        other is not null && Trigger.SequenceEqual(other.Trigger) && Playback.SequenceEqual(other.Playback);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var token in Trigger)
            hash.Add(token);
        hash.Add('>');
        foreach (var token in Playback)
            hash.Add(token);
        return hash.ToHashCode();
    }
}

/// <summary>A blank, malformed or unrecognised line, kept exactly as read.</summary>
public sealed record UnknownLine(string RawText) : LayoutLine
{
    public override string Render() => RawText;
}
