using Rewire.Core.Keyboards;
using Rewire.Core.Layouts;

namespace Rewire.Trainer.Tests;

internal static class TrainerFixtures
{
    public static readonly string PackDirectory = Path.Combine(AppContext.BaseDirectory, "keyboards", "kinesis-advantage2");

    public static readonly KeyboardPack Advantage2 = KeyboardPackLoader.Load(PackDirectory);

    public static readonly IReadOnlyList<Stage> Stages = Curriculum.Load(PackDirectory);

    /// <summary>What the computer can see; keypad shift, for one, never reaches it.</summary>
    public static readonly IReadOnlySet<string> Typable = new HashSet<string>(
        "abcdefghijklmnopqrstuvwxyz0123456789".Select(character => character.ToString())
            .Concat(["space", "hyphen", "obrack", "cbrack", "=", @"\", ";", "'", ",", ".", "/", "`", @"intl-\",
                     "bspace", "delete", "enter", "tab", "escape", "home", "end", "pup", "pdown",
                     "left", "right", "up", "down", "lctrl", "rctrl", "lalt", "ralt", "lwin", "rwin", "lshift", "rshift"]),
        StringComparer.OrdinalIgnoreCase);

    public static EffectiveLayout Factory => EffectiveLayout.Build(Advantage2, LayoutFile.Empty);

    public static EffectiveLayout Georges =>
        EffectiveLayout.Build(Advantage2, LayoutFile.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "advantage2-george_qwerty.txt"))));

    public static Stage Stage(string id) => Stages.Single(stage => stage.Id == id);
}

internal sealed class ManualClock : TimeProvider
{
    private DateTimeOffset _now = new(2026, 10, 31, 9, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now += by;
}
