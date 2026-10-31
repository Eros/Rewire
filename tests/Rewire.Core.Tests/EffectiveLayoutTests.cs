using System.Text;
using Rewire.Core.Keyboards;
using Rewire.Core.Layouts;

namespace Rewire.Core.Tests;

public class EffectiveLayoutTests
{
    private static readonly KeyboardPack Advantage2 =
        KeyboardPackLoader.Load(Path.Combine(AppContext.BaseDirectory, "keyboards", "kinesis-advantage2"));

    private static EffectiveLayout Build(string text) =>
        EffectiveLayout.Build(Advantage2, LayoutFile.Parse(Encoding.Latin1.GetBytes(text)));

    [Fact]
    public void A_key_with_no_remap_does_its_default_action()
    {
        var key = Build("").Find("base", "caps")!;

        Assert.Equal("caps", key.Action);
        Assert.Equal(KeyChange.None, key.Change);
    }

    [Fact]
    public void A_remap_changes_the_key_at_that_location()
    {
        var key = Build("[caps]>[escape]\r\n").Find("base", "caps")!;

        Assert.Equal("escape", key.Action);
        Assert.Equal("caps", key.DefaultAction);
        Assert.Equal(KeyChange.Remapped, key.Change);
    }

    [Fact]
    public void Keypad_locations_change_the_keypad_layer_only()
    {
        var layout = Build("[kp4]>[down]\r\n[kp-h]>[left]\r\n");

        Assert.Equal("down", layout.Find("keypad", "j")!.Action);
        Assert.Equal("left", layout.Find("keypad", "h")!.Action);
        Assert.Equal("j", layout.Find("base", "j")!.Action);
    }

    [Fact]
    public void Location_tokens_are_not_case_sensitive()
    {
        Assert.Equal("escape", Build("[CAPS]>[escape]").Find("base", "caps")!.Action);
    }

    [Fact]
    public void The_last_remap_of_a_location_wins()
    {
        Assert.Equal("tab", Build("[caps]>[escape]\n[caps]>[tab]\n").Find("base", "caps")!.Action);
    }

    [Fact]
    public void A_macro_belongs_to_its_non_modifier_trigger_key()
    {
        var layout = Build("{rctrl}{g}>{g}{o}\r\n");

        Assert.Single(layout.Find("base", "g")!.Macros);
        Assert.Empty(layout.Find("base", "rctrl")!.Macros);
    }

    [Fact]
    public void A_tap_and_hold_shows_its_tap_action()
    {
        var key = Build("[lshift]>[escape][t&h250][lshift]\r\n").Find("base", "lshift")!;

        Assert.Equal(KeyChange.TapHold, key.Change);
        Assert.Equal("escape", key.Action);
        Assert.Equal(250, key.TapHold!.DelayMilliseconds);
    }

    [Fact]
    public void Lines_for_keys_the_pack_does_not_draw_are_reported()
    {
        var layout = Build("[lp-tab]>[escape]\r\n");

        Assert.Single(layout.UnplacedLines);
    }

    [Fact]
    public void Places_every_line_of_a_real_layout_file()
    {
        var file = LayoutFile.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Golden", "advantage2-george_qwerty.txt")));

        var layout = EffectiveLayout.Build(Advantage2, file);

        Assert.Empty(layout.UnplacedLines);
        Assert.Equal("shutdn", layout.Find("base", "scroll")!.Action);
        Assert.Equal("kpshft", layout.Find("base", "caps")!.Action);
        Assert.Equal("=", layout.Find("keypad", "u")!.Action);
        Assert.Single(layout.Find("keypad", "w")!.Macros);
    }
}
