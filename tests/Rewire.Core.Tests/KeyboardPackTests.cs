using Rewire.Core.Keyboards;

namespace Rewire.Core.Tests;

public class KeyboardPackTests : IDisposable
{
    private static readonly string KeyboardsDirectory = Path.Combine(AppContext.BaseDirectory, "keyboards");

    private readonly string _temporary = Path.Combine(Path.GetTempPath(), "rewire-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void Every_shipped_pack_loads_without_problems()
    {
        var packs = KeyboardPackLoader.LoadAll(KeyboardsDirectory);

        Assert.Contains(packs, pack => pack.Id == "kinesis-advantage2");
    }

    [Fact]
    public void The_advantage2_has_the_168_locations_the_manual_describes()
    {
        var pack = KeyboardPackLoader.Load(Path.Combine(KeyboardsDirectory, "kinesis-advantage2"));

        Assert.Equal(168, pack.Layers.Sum(layer => layer.Keys.Count));
        Assert.Equal(["base", "keypad"], pack.Layers.Select(layer => layer.Id));
    }

    [Theory]
    [InlineData("base", "caps", "caps")]
    [InlineData("base", @"intl-\", @"intl-\")]
    [InlineData("keypad", "kp-caps", "caps")]
    [InlineData("keypad", "kp7", "u")]
    [InlineData("keypad", "KP4", "j")]
    [InlineData("keypad", "kp0", "space")]
    [InlineData("keypad", "kp-insert", @"intl-\")]
    public void Finds_the_physical_key_for_a_location_token(string layer, string location, string keyId)
    {
        var pack = KeyboardPackLoader.Load(Path.Combine(KeyboardsDirectory, "kinesis-advantage2"));

        Assert.Equal(keyId, pack.Layer(layer).KeyIdFor(location));
    }

    [Theory]
    [InlineData("`", "1")]
    [InlineData(@"intl-\", "2")]
    [InlineData("left", "3")]
    [InlineData("right", "4")]
    [InlineData("up", "7")]
    [InlineData("down", "8")]
    [InlineData("obrack", "9")]
    [InlineData("cbrack", "0")]
    public void Puts_each_bottom_row_key_under_the_column_the_manual_shows(string bottomKey, string numberKey)
    {
        var pack = KeyboardPackLoader.Load(Path.Combine(KeyboardsDirectory, "kinesis-advantage2"));

        Assert.Equal(pack.Key(numberKey).X, pack.Key(bottomKey).X);
        Assert.Equal(pack.Key(numberKey).Finger, pack.Key(bottomKey).Finger);
    }

    [Theory]
    [InlineData("bspace", "Bksp")]
    [InlineData("BSPACE", "Bksp")]
    [InlineData("q", "Q")]
    [InlineData("t&h250", "t&h250")]
    public void Gives_each_action_a_legend(string action, string legend)
    {
        var pack = KeyboardPackLoader.Load(Path.Combine(KeyboardsDirectory, "kinesis-advantage2"));

        Assert.Equal(legend, pack.LegendFor(action));
    }

    [Fact]
    public void Reports_every_problem_in_a_broken_pack()
    {
        var directory = WritePack(
            "broken",
            """
            { "id": "broken", "keys": [
              { "id": "a", "x": 0, "y": 0, "hand": "left", "finger": "index", "cluster": "main" },
              { "id": "a", "x": 1, "y": 0, "hand": "left", "finger": "index", "cluster": "main" },
              { "id": "b", "x": 2, "y": 0, "hand": "left", "cluster": "main" },
              { "id": "c", "x": 3, "y": 0, "hand": "left", "finger": "ring", "cluster": "main" }
            ] }
            """,
            """
            { "layers": [ { "id": "base", "keys": {
              "a": { "action": "a" },
              "z": { "action": "z" },
              "b": { "location": "a", "action": "b" }
            } } ] }
            """);

        var error = Assert.Throws<KeyboardPackException>(() => KeyboardPackLoader.Load(directory));

        Assert.Contains("Key \"a\" appears more than once.", error.Problems);
        Assert.Contains("Key \"b\" has no finger.", error.Problems);
        Assert.Contains("Layer \"base\" refers to key \"z\", which keyboard.json does not have.", error.Problems);
        Assert.Contains("Layer \"base\" uses the location \"a\" more than once.", error.Problems);
    }

    [Fact]
    public void Requires_the_base_layer_to_cover_every_remappable_key()
    {
        var directory = WritePack(
            "gaps",
            """
            { "id": "gaps", "keys": [
              { "id": "a", "x": 0, "y": 0, "hand": "left", "finger": "index", "cluster": "main" },
              { "id": "b", "x": 1, "y": 0, "hand": "left", "finger": "index", "cluster": "main" },
              { "id": "fn", "x": 2, "y": 0, "hand": "left", "finger": "index", "cluster": "function", "remappable": false }
            ] }
            """,
            """{ "layers": [ { "id": "base", "keys": { "a": { "action": "a" } } } ] }""");

        var error = Assert.Throws<KeyboardPackException>(() => KeyboardPackLoader.Load(directory));

        Assert.Equal(["Key \"b\" is remappable but missing from the \"base\" layer."], error.Problems);
    }

    [Fact]
    public void Requires_the_pack_id_to_match_its_folder()
    {
        var directory = WritePack(
            "folder-name",
            """{ "id": "other-name", "keys": [ { "id": "a", "x": 0, "y": 0, "hand": "left", "finger": "index", "cluster": "main" } ] }""",
            """{ "layers": [ { "id": "base", "keys": { "a": { "action": "a" } } } ] }""");

        var error = Assert.Throws<KeyboardPackException>(() => KeyboardPackLoader.Load(directory));

        Assert.Contains("The pack id \"other-name\" does not match its folder name.", error.Problems);
    }

    public void Dispose()
    {
        if (Directory.Exists(_temporary))
            Directory.Delete(_temporary, recursive: true);
    }

    private string WritePack(string id, string keyboard, string layout)
    {
        var directory = Path.Combine(_temporary, id);
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, KeyboardPackLoader.KeyboardFileName), keyboard);
        File.WriteAllText(Path.Combine(directory, KeyboardPackLoader.LayoutFileName), layout);
        return directory;
    }
}
