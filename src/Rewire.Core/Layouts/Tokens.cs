namespace Rewire.Core.Layouts;

/// <summary>Names a physical key, such as <c>caps</c>, <c>obrack</c> or <c>intl-\</c>.</summary>
public readonly record struct LocationToken(string Name)
{
    public override string ToString() => Name;
}

/// <summary>
/// What a key does when pressed: a key code, a modifier press or release, or a playback directive.
/// </summary>
/// <remarks>
/// Only the raw name is kept until the macro grammar is confirmed against the Kinesis manual,
/// so that no token is ever misread and rewritten differently.
/// </remarks>
public readonly record struct ActionToken(string Name)
{
    public override string ToString() => Name;
}
