using System.Collections.Immutable;
using System.Text;

namespace Rewire.Core.Layouts;

/// <summary>
/// A layout file such as <c>qwerty.txt</c> or <c>2_qwerty.txt</c>: an ordered list of remaps and macros
/// layered over the keyboard's default layout.
/// </summary>
/// <remarks>
/// Lines the user has not changed are written back exactly as they were read, byte for byte,
/// so that saving a file can never alter something the app does not understand.
/// </remarks>
public sealed class LayoutFile
{
    private static readonly byte[] Utf8Bom = [0xEF, 0xBB, 0xBF];

    // Latin-1 maps every byte to exactly one char and back, so any byte the keyboard
    // or another editor left in the file survives a round trip unchanged.
    private static readonly Encoding ByteFaithful = Encoding.Latin1;

    private readonly ImmutableList<Entry> _entries;
    private readonly bool _hasBom;
    private readonly string _newLine;

    private LayoutFile(ImmutableList<Entry> entries, bool hasBom, string newLine)
    {
        _entries = entries;
        _hasBom = hasBom;
        _newLine = newLine;
    }

    public static LayoutFile Empty { get; } = new([], hasBom: false, newLine: "\r\n");

    public IReadOnlyList<LayoutLine> Lines => _entries.Select(entry => entry.Line).ToList();

    public int Count => _entries.Count;

    public LayoutLine this[int index] => _entries[index].Line;

    public static LayoutFile Parse(ReadOnlySpan<byte> bytes)
    {
        var hasBom = bytes.StartsWith(Utf8Bom);
        if (hasBom)
            bytes = bytes[Utf8Bom.Length..];

        var text = ByteFaithful.GetString(bytes);
        var entries = ImmutableList.CreateBuilder<Entry>();
        string? firstNewLine = null;

        var start = 0;
        while (start < text.Length)
        {
            var end = text.AsSpan(start).IndexOfAny('\r', '\n');
            if (end < 0)
            {
                entries.Add(Entry.Read(text[start..], newLine: ""));
                break;
            }

            end += start;
            var newLine = text[end] == '\r' && end + 1 < text.Length && text[end + 1] == '\n' ? "\r\n" : text[end].ToString();
            entries.Add(Entry.Read(text[start..end], newLine));
            firstNewLine ??= newLine;
            start = end + newLine.Length;
        }

        return new LayoutFile(entries.ToImmutable(), hasBom, firstNewLine ?? "\r\n");
    }

    public byte[] ToBytes()
    {
        var text = new StringBuilder();
        foreach (var entry in _entries)
            text.Append(entry.SourceText ?? entry.Line.Render()).Append(entry.NewLine);

        var body = ByteFaithful.GetBytes(text.ToString());
        return _hasBom ? [.. Utf8Bom, .. body] : body;
    }

    public LayoutFile Replace(int index, LayoutLine line)
    {
        var existing = _entries[index];
        if (existing.Line.Equals(line))
            return this;

        return With(_entries.SetItem(index, existing with { Line = line, SourceText = null }));
    }

    public LayoutFile Insert(int index, LayoutLine line)
    {
        var entries = _entries;
        var newLine = _newLine;

        // Appending after an unterminated last line: terminate it, and leave the new
        // last line unterminated instead so the file keeps its original ending.
        if (index == entries.Count && index > 0 && entries[^1].NewLine.Length == 0)
        {
            entries = entries.SetItem(index - 1, entries[^1] with { NewLine = _newLine });
            newLine = "";
        }

        return With(entries.Insert(index, new Entry(line, SourceText: null, newLine)));
    }

    public LayoutFile Add(LayoutLine line) => Insert(_entries.Count, line);

    public LayoutFile RemoveAt(int index) => With(_entries.RemoveAt(index));

    private LayoutFile With(ImmutableList<Entry> entries) => new(entries, _hasBom, _newLine);

    private sealed record Entry(LayoutLine Line, string? SourceText, string NewLine)
    {
        public static Entry Read(string text, string newLine) => new(LayoutLineParser.Parse(text), text, newLine);
    }
}
