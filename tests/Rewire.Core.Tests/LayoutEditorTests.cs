using System.Text;
using Rewire.Core.Keyboards;
using Rewire.Core.Layouts;

namespace Rewire.Core.Tests;

public class LayoutEditorTests
{
    private static readonly KeyboardPack Advantage2 =
        KeyboardPackLoader.Load(Path.Combine(AppContext.BaseDirectory, "keyboards", "kinesis-advantage2"));

    private static LayoutFile Parse(string text) => LayoutFile.Parse(Encoding.Latin1.GetBytes(text));

    private static string Text(LayoutFile file) => Encoding.Latin1.GetString(file.ToBytes());

    [Fact]
    public void Adds_a_remap_for_a_key_that_has_none()
    {
        var edited = LayoutEditor.SetAction(Parse("[tab]>[caps]\r\n"), Advantage2, "base", "caps", "escape");

        Assert.Equal("[tab]>[caps]\r\n[caps]>[escape]\r\n", Text(edited));
    }

    [Fact]
    public void Rewrites_an_existing_remap_in_place()
    {
        var edited = LayoutEditor.SetAction(Parse("[caps]>[escape]\r\n[tab]>[caps]\r\n"), Advantage2, "base", "caps", "lctrl");

        Assert.Equal("[caps]>[lctrl]\r\n[tab]>[caps]\r\n", Text(edited));
    }

    [Fact]
    public void Choosing_the_default_action_removes_the_remap()
    {
        var edited = LayoutEditor.SetAction(Parse("[caps]>[escape]\r\n[tab]>[caps]\r\n"), Advantage2, "base", "caps", "caps");

        Assert.Equal("[tab]>[caps]\r\n", Text(edited));
    }

    [Fact]
    public void Choosing_the_default_for_an_unmapped_key_changes_nothing()
    {
        var file = Parse("[tab]>[caps]\r\n");

        Assert.Same(file, LayoutEditor.SetAction(file, Advantage2, "base", "caps", "CAPS"));
    }

    [Fact]
    public void Removes_earlier_duplicates_and_keeps_the_one_that_took_effect()
    {
        var edited = LayoutEditor.SetAction(Parse("[caps]>[tab]\r\n[q]>[a]\r\n[CAPS]>[escape]\r\n"), Advantage2, "base", "caps", "lctrl");

        Assert.Equal("[q]>[a]\r\n[CAPS]>[lctrl]\r\n", Text(edited));
    }

    [Fact]
    public void Replaces_a_tap_and_hold_with_a_plain_remap()
    {
        var edited = LayoutEditor.SetAction(Parse("[lshift]>[escape][t&h250][lshift]\r\n"), Advantage2, "base", "lshift", "caps");

        Assert.Equal("[lshift]>[caps]\r\n", Text(edited));
    }

    [Fact]
    public void Uses_the_keypad_layer_location_token()
    {
        var edited = LayoutEditor.SetAction(LayoutFile.Empty, Advantage2, "keypad", "j", "down");

        Assert.Equal("[kp4]>[down]\r\n", Text(edited));
    }

    [Fact]
    public void Leaves_macros_on_the_key_alone()
    {
        var edited = LayoutEditor.SetAction(Parse("{g}>{h}{i}\r\n"), Advantage2, "base", "g", "escape");

        Assert.Equal("{g}>{h}{i}\r\n[g]>[escape]\r\n", Text(edited));
    }

    [Fact]
    public void Swaps_two_keys_including_ones_already_remapped()
    {
        var edited = LayoutEditor.Swap(Parse("[bspace]>[space]\r\n"), Advantage2, "base", "bspace", "delete");

        var layout = EffectiveLayout.Build(Advantage2, edited);
        Assert.Equal("delete", layout.Find("base", "bspace")!.Action);
        Assert.Equal("space", layout.Find("base", "delete")!.Action);
    }

    [Fact]
    public void Swapping_twice_restores_the_file()
    {
        var file = Parse("[caps]>[escape]\r\n");

        var twice = LayoutEditor.Swap(LayoutEditor.Swap(file, Advantage2, "base", "caps", "tab"), Advantage2, "base", "caps", "tab");

        Assert.Equal(Text(file), Text(twice));
    }
}

public class LayoutDiffTests
{
    private static readonly KeyboardPack Advantage2 =
        KeyboardPackLoader.Load(Path.Combine(AppContext.BaseDirectory, "keyboards", "kinesis-advantage2"));

    private static IReadOnlyList<LayoutChange> Compare(string before, string after) =>
        LayoutDiff.Compare(
            LayoutFile.Parse(Encoding.Latin1.GetBytes(before)),
            LayoutFile.Parse(Encoding.Latin1.GetBytes(after)),
            Advantage2);

    [Fact]
    public void Identical_files_have_no_changes()
    {
        Assert.Empty(Compare("[caps]>[escape]\r\n", "[caps]>[escape]\r\n"));
    }

    [Fact]
    public void Describes_a_new_remap_by_keycap_and_legend()
    {
        var change = Assert.Single(Compare("", "[caps]>[escape]\r\n"));

        Assert.Equal(LayoutChangeKind.Added, change.Kind);
        Assert.Equal("Caps → Esc", change.Description);
    }

    [Fact]
    public void Pairs_a_removed_and_added_line_for_the_same_key_into_one_change()
    {
        var change = Assert.Single(Compare("[caps]>[escape]\r\n[tab]>[q]\r\n", "[tab]>[q]\r\n[caps]>[lctrl]\r\n"));

        Assert.Equal(LayoutChangeKind.Changed, change.Kind);
        Assert.Equal("Caps: Esc → Ctrl", change.Description);
    }

    [Fact]
    public void Describes_a_removed_remap_as_going_back_to_the_default()
    {
        var change = Assert.Single(Compare("[kp4]>[down]\r\n", ""));

        Assert.Equal("Keypad layer: J back to 4", change.Description);
    }

    [Fact]
    public void Describes_macros_by_their_trigger()
    {
        var change = Assert.Single(Compare("", "{rctrl}{g}>{g}{o}\r\n"));

        Assert.Equal("New macro on Ctrl+G", change.Description);
    }

    [Fact]
    public void Does_not_report_whitespace_only_differences_in_an_unchanged_line()
    {
        Assert.Empty(Compare("[caps]>[escape]\r\n", " [caps]>[escape] \r\n"));
    }
}
