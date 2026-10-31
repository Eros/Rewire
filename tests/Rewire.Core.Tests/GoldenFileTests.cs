using Rewire.Core.Layouts;

namespace Rewire.Core.Tests;

/// <summary>
/// Every layout file in the Golden folder must survive a parse and serialise unchanged.
/// Add real files from keyboards and SmartSet exports there to grow the corpus.
/// </summary>
public class GoldenFileTests
{
    private static readonly string GoldenDirectory = Path.Combine(AppContext.BaseDirectory, "Golden");

    public static TheoryData<string> GoldenFiles()
    {
        var data = new TheoryData<string>();
        foreach (var path in Directory.EnumerateFiles(GoldenDirectory, "*.txt").Order())
            data.Add(Path.GetFileName(path));
        return data;
    }

    [Theory]
    [MemberData(nameof(GoldenFiles))]
    public void Round_trips_byte_for_byte(string fileName)
    {
        var bytes = File.ReadAllBytes(Path.Combine(GoldenDirectory, fileName));

        Assert.Equal(bytes, LayoutFile.Parse(bytes).ToBytes());
    }

    [Theory]
    [MemberData(nameof(GoldenFiles))]
    public void Reparses_to_the_same_lines_after_every_line_is_rerendered(string fileName)
    {
        var original = LayoutFile.Parse(File.ReadAllBytes(Path.Combine(GoldenDirectory, fileName)));

        var rerendered = LayoutFile.Empty;
        foreach (var line in original.Lines)
            rerendered = rerendered.Add(line);
        var reparsed = LayoutFile.Parse(rerendered.ToBytes());

        Assert.Equal(original.Lines.Select(Normalise), reparsed.Lines.Select(Normalise));
    }

    // Unknown lines are compared trimmed because re-rendering a known line drops its padding,
    // which can turn an indented blank into an empty one.
    private static LayoutLine Normalise(LayoutLine line) =>
        line is UnknownLine unknown ? unknown with { RawText = unknown.RawText.Trim() } : line;
}

public class RealLayoutTests
{
    private static LayoutFile Load(string fileName) =>
        LayoutFile.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Golden", fileName)));

    [Fact]
    public void Recognises_every_line_of_a_smartset_written_file()
    {
        var file = Load("advantage2-george_qwerty.txt");

        Assert.DoesNotContain(file.Lines, line => line is UnknownLine);
        Assert.Equal(34, file.Lines.OfType<Remap>().Count());
        Assert.Equal(3, file.Lines.OfType<Macro>().Count());
    }

    [Fact]
    public void Reads_modifier_holds_and_speed_in_a_macro()
    {
        var macro = Load("advantage2-george_qwerty.txt").Lines.OfType<Macro>()
            .Single(line => line.Trigger.SequenceEqual([new LocationToken("kp-s")]));

        Assert.Equal(["speed5", "-lwin", "s", "+lwin"], macro.Playback.Select(token => token.Name));
    }
}
