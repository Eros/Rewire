using Avalonia.Media;
using TextMateSharp.Grammars;
using TextMateSharp.Registry;

namespace Rewire.Services;

/// <summary>
/// Colours each character of a code template the way an editor would, using TextMate grammars, so a code
/// drill can reveal every character in its syntax colour as it is typed.
/// </summary>
public sealed class CodeHighlighter
{
    // Template folders are named after these TextMate language ids, apart from the ones mapped here.
    private static readonly Dictionary<string, string> LanguageIds = new()
    {
        ["csharp"] = "csharp",
        ["java"] = "java",
        ["python"] = "python",
        ["javascript"] = "javascript",
        ["typescript"] = "typescript",
        ["cpp"] = "cpp",
        ["rust"] = "rust",
        ["go"] = "go",
    };

    private readonly RegistryOptions _options;
    private readonly Registry _registry;

    public CodeHighlighter(bool dark)
    {
        _options = new RegistryOptions(dark ? ThemeName.DarkPlus : ThemeName.LightPlus);
        _registry = new Registry(_options);
    }

    /// <returns>A colour for each character of <paramref name="text"/>; null where the theme gives none.</returns>
    public IReadOnlyList<Color?> Colours(string language, string text)
    {
        var colours = new Color?[text.Length];
        var scope = _options.GetScopeByLanguageId(LanguageIds.GetValueOrDefault(language, language));
        if (scope is null || _registry.LoadGrammar(scope) is not { } grammar)
            return colours;

        var theme = _registry.GetTheme();
        var lineStart = 0;
        IStateStack? state = null;
        foreach (var line in text.Split('\n'))
        {
            var result = grammar.TokenizeLine(line, state, TimeSpan.FromSeconds(1));
            state = result.RuleStack;

            foreach (var token in result.Tokens)
            {
                var rule = theme.Match(token.Scopes).FirstOrDefault(match => match.foreground > 0);
                if (rule is null || !Color.TryParse(theme.GetColor(rule.foreground), out var colour))
                    continue;

                for (var i = token.StartIndex; i < Math.Min(token.EndIndex, line.Length); i++)
                    colours[lineStart + i] = colour;
            }

            lineStart += line.Length + 1;
        }

        return colours;
    }
}
