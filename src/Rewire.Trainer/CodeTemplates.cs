namespace Rewire.Trainer;

/// <summary>A source file from <c>templates/&lt;language&gt;/&lt;level&gt;/</c>, to be typed in a code drill.</summary>
public sealed record CodeTemplate(string Language, int Level, string Name, string Text);

/// <summary>One line of a code drill: how far it is indented, and its targets' positions in the drill.</summary>
public sealed record CodeLine(int Indent, int FirstTarget, int TargetCount);

/// <param name="Offsets">For each target, its character offset in the template, or -1 for the Enter that ends a line.</param>
public sealed record CodeDrill(Drill Drill, CodeTemplate Template, IReadOnlyList<int> Offsets, IReadOnlyList<CodeLine> Lines);

public static class CodeTemplates
{
    public const int Levels = 5;

    /// <summary>
    /// Level names for the progression; the folders are numbered 1 to 5 to keep contributing simple.
    /// </summary>
    public static readonly IReadOnlyList<string> LevelNames =
        ["Single statements", "Control flow", "Functions and classes", "Generics, lambdas and collections", "Realistic snippets"];

    public static IReadOnlyList<CodeTemplate> Load(string templatesDirectory)
    {
        if (!Directory.Exists(templatesDirectory))
            return [];

        var templates = new List<CodeTemplate>();
        foreach (var languageDirectory in Directory.EnumerateDirectories(templatesDirectory))
        {
            var language = Path.GetFileName(languageDirectory);
            foreach (var levelDirectory in Directory.EnumerateDirectories(languageDirectory))
            {
                if (!int.TryParse(Path.GetFileName(levelDirectory), out var level) || level is < 1 or > Levels)
                    continue;

                foreach (var file in Directory.EnumerateFiles(levelDirectory))
                    templates.Add(new CodeTemplate(language, level, Path.GetFileName(file), File.ReadAllText(file).ReplaceLineEndings("\n")));
            }
        }

        return templates.OrderBy(template => template.Language).ThenBy(template => template.Level).ThenBy(template => template.Name).ToList();
    }

    public static string StageId(string language, int level) => $"code:{language}:{level}";

    // Editors add imports automatically, so typing them is not worth practising.
    private static bool IsImport(string language, string line, ref bool inImportBlock)
    {
        // Go groups imports in a block: import ( ... ).
        if (inImportBlock)
        {
            inImportBlock = line != ")";
            return true;
        }

        if (language == "go" && line == "import (")
        {
            inImportBlock = true;
            return true;
        }

        return language switch
        {
            // A using directive names a namespace; using var or using ( is an ordinary statement.
            "csharp" => line.StartsWith("using ") && line.EndsWith(';') && !line.StartsWith("using var ") && !line.StartsWith("using ("),
            "java" => line.StartsWith("import "),
            "python" => line.StartsWith("import ") || (line.StartsWith("from ") && line.Contains(" import ")),
            "javascript" or "typescript" => line.StartsWith("import ") || (line.StartsWith("const ") && line.Contains("require(")),
            "cpp" => line.StartsWith("#include"),
            "rust" => line.StartsWith("use "),
            "go" => line.StartsWith("import "),
            _ => false,
        };
    }

    /// <summary>
    /// Turns a template into targets: blank lines, imports and leading indentation are skipped, trailing spaces
    /// dropped, and Enter ends every line but the last. Characters no key in the layout can type are left out.
    /// </summary>
    public static CodeDrill Build(CodeTemplate template, Func<string, string?> keyForAction, string enterLegend)
    {
        var stage = new Stage(StageId(template.Language, template.Level), $"{template.Language} level {template.Level}",
            template.Name, DrillKind.Code, [], 0, null);

        var targets = new List<DrillTarget>();
        var offsets = new List<int>();
        var lines = new List<CodeLine>();
        var enterKey = keyForAction("enter");

        var lineStart = 0;
        var inImportBlock = false;
        foreach (var line in template.Text.Split('\n'))
        {
            var content = line.TrimEnd();
            var indent = content.Length - content.TrimStart().Length;
            var isImport = IsImport(template.Language, content.Trim(), ref inImportBlock);
            if (content.Trim().Length > 0 && !isImport)
            {
                if (lines.Count > 0 && enterKey is not null)
                {
                    targets.Add(new DrillTarget(enterKey, "enter", enterLegend, IsCharacter: false));
                    offsets.Add(-1);
                }

                var first = targets.Count;
                for (var i = indent; i < content.Length; i++)
                {
                    if (CharacterActions.ActionFor(content[i]) is { } action && keyForAction(action) is { } keyId)
                    {
                        targets.Add(new DrillTarget(keyId, action, content[i].ToString(), IsCharacter: true));
                        offsets.Add(lineStart + i);
                    }
                }

                // Tabs count as four columns, so Go's tab-indented templates line up like the rest.
                var columns = line[..indent].Sum(character => character == '\t' ? 4 : 1);
                lines.Add(new CodeLine(columns, first, targets.Count - first));
            }

            lineStart += line.Length + 1;
        }

        // Each line's Enter belongs at the end of the line before it.
        for (var i = 0; i < lines.Count - 1; i++)
            lines[i] = lines[i] with { TargetCount = lines[i + 1].FirstTarget - lines[i].FirstTarget };

        return new CodeDrill(new Drill(stage, targets), template, offsets, lines);
    }
}
