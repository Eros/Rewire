using System.Text;
using Rewire.Core.Keyboards;
using Rewire.Core.Layouts;

namespace Rewire.Core.Tests;

public class MacroTests
{
    private static readonly KeyboardPack Advantage2 =
        KeyboardPackLoader.Load(Path.Combine(AppContext.BaseDirectory, "keyboards", "kinesis-advantage2"));

    private static LayoutFile Parse(string text) => LayoutFile.Parse(Encoding.Latin1.GetBytes(text));

    private static Macro Macro(string text) => (Macro)LayoutLineParser.Parse(text);

    [Theory]
    [InlineData("h", MacroTokenKind.Key, "h")]
    [InlineData("-lshift", MacroTokenKind.Press, "lshift")]
    [InlineData("+lshift", MacroTokenKind.Release, "lshift")]
    [InlineData("speed5", MacroTokenKind.Speed, "5")]
    [InlineData("d500", MacroTokenKind.Delay, "500")]
    [InlineData("delete", MacroTokenKind.Key, "delete")]
    [InlineData("-", MacroTokenKind.Key, "-")]
    public void Reads_each_kind_of_playback_token(string name, MacroTokenKind kind, string value)
    {
        var token = MacroToken.Read(new ActionToken(name));

        Assert.Equal(new MacroToken(kind, value), token);
        Assert.Equal(name, token.ToAction().Name);
    }

    [Fact]
    public void A_well_formed_macro_has_no_problems()
    {
        Assert.Empty(Macros.Check(Macro("{rctrl}{g}>{speed5}{-lshift}{h}{+lshift}{i}{d500}"), LayoutFile.Empty, Advantage2));
    }

    [Theory]
    [InlineData("{lshift}>{h}", "a modifier on its own cannot trigger one")]
    [InlineData("{g}{h}>{h}", "not by several keys")]
    [InlineData("{g}>{h}{d250}", "only has 125 ms and 500 ms delays")]
    [InlineData("{g}>{speed0}{h}", "speeds run from 1 to 9")]
    public void Rejects_what_the_keyboard_cannot_play(string text, string message)
    {
        var problem = Assert.Single(Macros.Check(Macro(text), LayoutFile.Empty, Advantage2));

        Assert.Equal(MacroProblemSeverity.Error, problem.Severity);
        Assert.Contains(message, problem.Message);
    }

    [Fact]
    public void Warns_about_a_modifier_left_held()
    {
        var problem = Assert.Single(Macros.Check(Macro("{g}>{-lshift}{h}"), LayoutFile.Empty, Advantage2));

        Assert.Equal(MacroProblemSeverity.Warning, problem.Severity);
        Assert.Contains("never let go", problem.Message);
    }

    [Fact]
    public void Warns_that_a_macro_overrides_a_remap_on_its_key()
    {
        var problem = Assert.Single(Macros.Check(Macro("{caps}>{h}"), Parse("[caps]>[escape]\r\n"), Advantage2));

        Assert.Contains("the macro takes priority", problem.Message);
    }

    [Fact]
    public void Replaces_a_macro_with_the_same_trigger_whatever_the_modifier_order()
    {
        var file = Parse("{rctrl}{lshift}{g}>{a}\r\n[caps]>[escape]\r\n");

        var edited = Macros.Set(file, Macro("{lshift}{RCTRL}{g}>{b}"));

        Assert.Equal("{lshift}{RCTRL}{g}>{b}\r\n[caps]>[escape]\r\n", Encoding.Latin1.GetString(edited.ToBytes()));
    }

    [Fact]
    public void Removes_only_the_macro_with_that_trigger()
    {
        var file = Parse("{g}>{a}\r\n{rctrl}{g}>{b}\r\n");

        var edited = Macros.Remove(file, [new LocationToken("g")]);

        Assert.Equal("{rctrl}{g}>{b}\r\n", Encoding.Latin1.GetString(edited.ToBytes()));
    }
}

public class PresetTests
{
    private static readonly string PackDirectory = Path.Combine(AppContext.BaseDirectory, "keyboards", "kinesis-advantage2");
    private static readonly KeyboardPack Advantage2 = KeyboardPackLoader.Load(PackDirectory);
    private static readonly IReadOnlyList<Preset> Presets = Preset.Load(PackDirectory);

    private static Preset Named(string name) => Presets.Single(preset => preset.Name == name);

    [Fact]
    public void Loads_the_shipped_presets_in_order()
    {
        Assert.Equal(["Esc on Caps Lock", "Mac thumb modifiers", "Brackets on the thumbs", "Colemak", "Workman"],
            Presets.Select(preset => preset.Name));
    }

    [Fact]
    public void Every_preset_refers_only_to_keys_the_pack_has()
    {
        foreach (var remap in Presets.SelectMany(preset => preset.Remaps))
            Assert.Contains(Advantage2.Layers, layer => layer.KeyIdFor(remap.Location.Name) is not null);
    }

    [Fact]
    public void Applying_a_preset_remaps_its_keys()
    {
        var file = Named("Esc on Caps Lock").Apply(LayoutFile.Empty, Advantage2);

        Assert.Equal("escape", EffectiveLayout.Build(Advantage2, file).Find("base", "caps")!.Action);
        Assert.Equal(PresetState.Applied, Named("Esc on Caps Lock").StateIn(EffectiveLayout.Build(Advantage2, file)));
    }

    [Fact]
    public void Applying_then_removing_a_letter_layout_leaves_the_file_as_it_was()
    {
        var original = LayoutFile.Parse("[caps]>[escape]\r\n"u8);

        var round = Named("Colemak").Remove(Named("Colemak").Apply(original, Advantage2), Advantage2);

        Assert.Equal(original.ToBytes(), round.ToBytes());
    }

    [Fact]
    public void Removing_leaves_keys_changed_since_alone()
    {
        var applied = Named("Brackets on the thumbs").Apply(LayoutFile.Empty, Advantage2);
        var changedSince = LayoutEditor.SetAction(applied, Advantage2, "base", "home", "end");

        var removed = Named("Brackets on the thumbs").Remove(changedSince, Advantage2);

        var layout = EffectiveLayout.Build(Advantage2, removed);
        Assert.Equal("end", layout.Find("base", "home")!.Action);
        Assert.Equal("pup", layout.Find("base", "pup")!.Action);
    }

    [Fact]
    public void Reports_the_remaps_a_preset_would_replace()
    {
        var georges = EffectiveLayout.Build(Advantage2, LayoutFile.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Golden", "advantage2-george_qwerty.txt"))));

        var overrides = Named("Brackets on the thumbs").Overrides(georges);

        Assert.Equal(["home", "pup"], overrides.Select(key => key.Key.Id));
    }

    [Fact]
    public void Recognises_a_preset_the_keyboard_itself_already_wrote()
    {
        // George set Mac mode on the keyboard, which wrote the same remaps as this preset.
        var georges = EffectiveLayout.Build(Advantage2, LayoutFile.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Golden", "advantage2-george_qwerty.txt"))));

        Assert.Equal(PresetState.Applied, Named("Mac thumb modifiers").StateIn(georges));
    }
}
