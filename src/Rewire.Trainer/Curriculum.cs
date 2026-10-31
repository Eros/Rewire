using System.Text.Json;
using System.Text.Json.Serialization;

namespace Rewire.Trainer;

public enum DrillKind
{
    /// <summary>Ordinary words for a fixed time, typed on the user's old keyboard.</summary>
    Baseline,

    Words,

    /// <summary>Each finger's column top to bottom, then words.</summary>
    Columns,

    /// <summary>Words with Enter, Backspace, arrows and similar keys mixed in.</summary>
    Commands,

    /// <summary>A real code snippet, revealed in its syntax colours as it is typed.</summary>
    Code,

    /// <summary>A prompt such as "Go to declaration", answered by pressing its key binding.</summary>
    Shortcuts,
}

/// <param name="Keys">The physical keys this stage drills, by key id.</param>
/// <param name="SpeedShare">The share of baseline speed needed, with 95% accuracy, to pass.</param>
public sealed record Stage(
    string Id,
    string Title,
    string Description,
    DrillKind Drill,
    IReadOnlyList<string> Keys,
    double SpeedShare,
    TimeSpan? TimeLimit);

/// <summary>The ordered stages from a keyboard pack's <c>curriculum.json</c>.</summary>
public static class Curriculum
{
    public const string FileName = "curriculum.json";

    public const double PassingAccuracy = 0.95;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public static IReadOnlyList<Stage> Load(string packDirectory)
    {
        var path = Path.Combine(packDirectory, FileName);
        var file = JsonSerializer.Deserialize<CurriculumJson>(File.ReadAllText(path), Json)
            ?? throw new InvalidDataException($"{path} is empty.");

        return (file.Stages ?? [])
            .Select(stage => new Stage(
                stage.Id ?? throw new InvalidDataException($"A stage in {path} has no id."),
                stage.Title ?? stage.Id,
                stage.Description ?? "",
                stage.Drill ?? DrillKind.Words,
                stage.Keys ?? [],
                stage.SpeedShare ?? 0,
                stage.Seconds is { } seconds ? TimeSpan.FromSeconds(seconds) : null))
            .ToList();
    }

    private sealed record CurriculumJson(List<StageJson>? Stages);

    private sealed record StageJson(
        string? Id,
        string? Title,
        string? Description,
        DrillKind? Drill,
        List<string>? Keys,
        double? SpeedShare,
        int? Seconds);
}
