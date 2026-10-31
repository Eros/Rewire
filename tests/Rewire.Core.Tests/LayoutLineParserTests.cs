using Rewire.Core.Layouts;

namespace Rewire.Core.Tests;

public class LayoutLineParserTests
{
    [Fact]
    public void Parses_a_remap()
    {
        var line = LayoutLineParser.Parse("[caps]>[escape]");

        Assert.Equal(new Remap(new LocationToken("caps"), new ActionToken("escape")), line);
    }

    [Fact]
    public void Parses_a_remap_whose_token_contains_a_backslash()
    {
        var line = LayoutLineParser.Parse(@"[intl-\]>[bslash]");

        Assert.Equal(new Remap(new LocationToken(@"intl-\"), new ActionToken("bslash")), line);
    }

    [Fact]
    public void Parses_a_macro()
    {
        var line = LayoutLineParser.Parse("{lctrl}{f1}>{h}{i}");

        var expected = new Macro(
            [new LocationToken("lctrl"), new LocationToken("f1")],
            [new ActionToken("h"), new ActionToken("i")]);
        Assert.Equal(expected, line);
    }

    [Fact]
    public void Ignores_surrounding_whitespace()
    {
        Assert.IsType<Remap>(LayoutLineParser.Parse("  [caps]>[escape]\t"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("caps>escape")]
    [InlineData("[caps]>")]
    [InlineData(">[escape]")]
    [InlineData("[caps]>{escape}")]
    [InlineData("[caps][tab]>[escape]")]
    [InlineData("[caps] > [escape]")]
    [InlineData("[caps]>[escape]>[tab]")]
    [InlineData("[]>[escape]")]
    [InlineData("[caps>[escape]")]
    [InlineData("{lctrl}{f1>{h}")]
    public void Keeps_anything_else_verbatim(string text)
    {
        Assert.Equal(new UnknownLine(text), LayoutLineParser.Parse(text));
    }

    [Theory]
    [InlineData("[caps]>[escape]")]
    [InlineData("{lctrl}{f1}>{h}{e}{l}{l}{o}")]
    [InlineData("not a layout line")]
    public void Renders_what_it_parsed(string text)
    {
        Assert.Equal(text, LayoutLineParser.Parse(text).Render());
    }
}

public class TapHoldParserTests
{
    [Fact]
    public void Parses_a_tap_and_hold()
    {
        var line = LayoutLineParser.Parse("[lshift]>[esc][t&h250][lshift]");

        Assert.Equal(new TapHold(new LocationToken("lshift"), new ActionToken("esc"), 250, new ActionToken("lshift")), line);
        Assert.Equal("[lshift]>[esc][t&h250][lshift]", line.Render());
    }

    [Theory]
    [InlineData("[lshift]>[esc][t&h0][lshift]")]
    [InlineData("[lshift]>[esc][t&h1000][lshift]")]
    [InlineData("[lshift]>[esc][t&hx][lshift]")]
    [InlineData("[lshift]>[esc][delay][lshift]")]
    [InlineData("[lshift]>[esc][lshift]")]
    public void Keeps_other_multi_action_remaps_verbatim(string text)
    {
        Assert.IsType<UnknownLine>(LayoutLineParser.Parse(text));
    }
}
