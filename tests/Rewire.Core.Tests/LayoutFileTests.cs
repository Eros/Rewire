using System.Text;
using Rewire.Core.Layouts;

namespace Rewire.Core.Tests;

public class LayoutFileTests
{
    private static readonly Remap CapsToEscape = new(new LocationToken("caps"), new ActionToken("escape"));
    private static readonly Remap TabToCaps = new(new LocationToken("tab"), new ActionToken("caps"));

    [Theory]
    [InlineData("[caps]>[escape]\r\n[tab]>[caps]\r\n")]
    [InlineData("[caps]>[escape]\n[tab]>[caps]\n")]
    [InlineData("[caps]>[escape]\r\n[tab]>[caps]\n\r")]
    [InlineData("[caps]>[escape]\r\n[tab]>[caps]")]
    [InlineData("\r\n\r\n")]
    [InlineData("")]
    [InlineData("  [caps] > [escape]  \r\n")]
    public void Round_trips_line_endings_and_odd_lines(string text)
    {
        var bytes = Encoding.Latin1.GetBytes(text);

        Assert.Equal(bytes, LayoutFile.Parse(bytes).ToBytes());
    }

    [Fact]
    public void Round_trips_a_byte_order_mark_and_non_ascii_bytes()
    {
        byte[] bytes = [0xEF, 0xBB, 0xBF, .. "[caps]>[escape]\r\n"u8, 0x00, 0xFF, 0x80, (byte)'\r', (byte)'\n'];

        var file = LayoutFile.Parse(bytes);

        Assert.Equal(CapsToEscape, file[0]);
        Assert.Equal(bytes, file.ToBytes());
    }

    [Fact]
    public void Rewrites_only_the_line_that_changed()
    {
        var file = Parse("[caps]>[escape]\r\n  [tab]>[caps]  \r\nnot understood\r\n");

        var edited = file.Replace(0, new Remap(new LocationToken("caps"), new ActionToken("lctrl")));

        Assert.Equal("[caps]>[lctrl]\r\n  [tab]>[caps]  \r\nnot understood\r\n", Text(edited));
    }

    [Fact]
    public void Replacing_a_line_with_an_equal_one_keeps_its_original_text()
    {
        var file = Parse(" [caps]>[escape] \r\n");

        Assert.Same(file, file.Replace(0, CapsToEscape));
    }

    [Fact]
    public void Added_lines_use_the_file_line_ending()
    {
        var file = Parse("[caps]>[escape]\n");

        Assert.Equal("[caps]>[escape]\n[tab]>[caps]\n", Text(file.Add(TabToCaps)));
    }

    [Fact]
    public void Appending_to_an_unterminated_file_keeps_it_unterminated()
    {
        var file = Parse("[caps]>[escape]\r\n[escape]>[caps]");

        Assert.Equal("[caps]>[escape]\r\n[escape]>[caps]\r\n[tab]>[caps]", Text(file.Add(TabToCaps)));
    }

    [Fact]
    public void A_new_file_uses_crlf()
    {
        var file = LayoutFile.Empty.Add(CapsToEscape).Add(TabToCaps);

        Assert.Equal("[caps]>[escape]\r\n[tab]>[caps]\r\n", Text(file));
    }

    [Fact]
    public void Removing_a_line_leaves_the_others_untouched()
    {
        var file = Parse("[caps]>[escape]\r\n junk \r\n[tab]>[caps]\r\n");

        Assert.Equal("[caps]>[escape]\r\n[tab]>[caps]\r\n", Text(file.RemoveAt(1)));
    }

    [Fact]
    public void Edits_return_a_new_file_and_leave_the_original_alone()
    {
        var file = Parse("[caps]>[escape]\r\n");

        file.Add(TabToCaps);

        Assert.Equal(1, file.Count);
    }

    private static LayoutFile Parse(string text) => LayoutFile.Parse(Encoding.Latin1.GetBytes(text));

    private static string Text(LayoutFile file) => Encoding.Latin1.GetString(file.ToBytes());
}
