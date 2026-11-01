using System.Reflection;
using Rewire.Core.Keyboards;
using Rewire.Core.Layouts;

namespace Rewire.Trainer;

/// <summary>One key to press: a physical key, the action it currently sends, and what to show for it.</summary>
public sealed record DrillTarget(string KeyId, string Action, string Display, bool IsCharacter);

public sealed record Drill(Stage Stage, IReadOnlyList<DrillTarget> Targets);

/// <summary>
/// Builds drills from the effective layout, so a remapped key is drilled where it now is and with
/// what it now does, and keys whose action never reaches the computer are left out.
/// </summary>
public sealed class DrillGenerator
{
    private const string BaseLayer = "base";
    private const int WordsPerDrill = 24;
    private const int BaselineWords = 400;

    private static readonly IReadOnlyList<string> Words = LoadWords();

    private readonly EffectiveLayout _layout;
    private readonly IReadOnlySet<string> _typableActions;
    private readonly Random _random;
    private readonly Dictionary<string, string> _keysByAction;

    /// <param name="typableActions">Actions the computer can see, such as letters and Enter but not keypad shift.</param>
    public DrillGenerator(EffectiveLayout layout, IReadOnlySet<string> typableActions, Random? random = null)
    {
        _layout = layout;
        _typableActions = typableActions;
        _random = random ?? Random.Shared;
        _keysByAction = MapActionsToKeys(layout);
    }

    /// <summary>How much each key and pair of keys needs practice; words rich in weak ones are picked more often.</summary>
    public SkillWeights Weights { get; set; } = SkillWeights.None;

    /// <summary>The physical key that sends <paramref name="action"/> on the top layer, if any does.</summary>
    public string? KeyFor(string action) => _keysByAction.GetValueOrDefault(action);

    /// <param name="earlierKeys">Keys from stages already passed, which drills may use alongside the stage's own.</param>
    public Drill Generate(Stage stage, IEnumerable<string> earlierKeys)
    {
        var focus = Targets(stage.Keys);
        var known = Targets(stage.Keys.Concat(earlierKeys)).Concat(focus).ToList();

        var targets = stage.Drill switch
        {
            DrillKind.Baseline or DrillKind.Timed => Text(string.Join(' ', RandomWords())),
            DrillKind.Columns => [.. ColumnRuns(stage), .. Space(), .. WordDrill(known, focus)],
            DrillKind.Commands => CommandDrill(known, focus),
            _ => WordDrill(known, focus),
        };

        return new Drill(stage, targets);
    }

    private List<DrillTarget> WordDrill(IReadOnlyList<DrillTarget> known, IReadOnlyList<DrillTarget> focus)
    {
        var words = PickWords(known, focus, WordsPerDrill);
        return Text(string.Join(' ', words));
    }

    private List<DrillTarget> CommandDrill(IReadOnlyList<DrillTarget> known, IReadOnlyList<DrillTarget> focus)
    {
        var commands = focus.Where(target => !target.IsCharacter).ToList();
        var symbols = focus.Where(target => target.IsCharacter && !char.IsAsciiLetterOrDigit(target.Display[0]) && target.Display != " ").ToList();
        var words = PickWords(known, focus, WordsPerDrill);

        // With no letters learned yet there are no words to carry the commands, so they are drilled on their own.
        if (words.Count == 0)
            return [.. Enumerable.Repeat(commands, 3).SelectMany(round => round.SelectMany(command => new[] { command }.Concat(Space())))];

        var targets = new List<DrillTarget>();
        for (var i = 0; i < words.Count; i++)
        {
            if (i > 0)
                targets.AddRange(Space());

            targets.AddRange(Text(Decorate(words[i], symbols, i)));

            // Cycling through the commands makes sure every one of them comes up in each drill.
            if (commands.Count > 0 && i % 2 == 0)
                targets.Add(commands[i / 2 % commands.Count]);
        }

        return targets;
    }

    private string Decorate(string word, IReadOnlyList<DrillTarget> symbols, int index)
    {
        if (symbols.Count == 0 || index % 2 == 1)
            return word;

        var symbol = symbols[index / 2 % symbols.Count].Display;
        var closing = symbol switch
        {
            "[" when symbols.Any(other => other.Display == "]") => "]",
            _ => null,
        };
        return closing is null ? $"{word}{symbol}{Different(Words, word, list => list[_random.Next(list.Count)])}" : $"{symbol}{word}{closing}";
    }

    // Runs down each column of the stage's keys, one finger at a time, through every column twice
    // rather than each column twice in a row, so no run follows itself.
    private List<DrillTarget> ColumnRuns(Stage stage)
    {
        var pack = _layout.Pack;
        var columns = stage.Keys
            .Select(pack.Key)
            .Where(key => key.Cluster == Cluster.Main)
            .GroupBy(key => (key.Hand, key.X))
            .OrderBy(column => column.Key.Hand)
            .ThenBy(column => column.Key.Hand == Hand.Left ? column.Key.X : -column.Key.X);

        var runs = columns
            .Select(column => Targets(column.OrderBy(key => key.Y).Select(key => key.Id)))
            .Where(run => run.Count > 0)
            .ToList();

        var targets = new List<DrillTarget>();
        foreach (var run in runs.Concat(runs))
        {
            if (targets.Count > 0)
                targets.AddRange(Space());
            targets.AddRange(run);
        }

        return targets;
    }

    private List<string> PickWords(IReadOnlyList<DrillTarget> known, IReadOnlyList<DrillTarget> focus, int count)
    {
        var allowed = known.Where(target => target.IsCharacter).Select(target => target.Display[0]).ToHashSet();
        var focusLetters = focus.Where(target => target.IsCharacter && char.IsAsciiLetter(target.Display[0]))
            .Select(target => target.Display[0]).ToHashSet();

        var candidates = Words.Where(word => word.All(allowed.Contains)).ToList();
        var picked = new List<string>();
        for (var i = 0; i < count; i++)
        {
            // Too few real words fit the keys learned so far, so made-up ones fill the gaps.
            if (candidates.Count < 12 && focusLetters.Count > 0 && (candidates.Count == 0 || _random.NextDouble() < 0.6))
            {
                picked.Add(Different(picked.LastOrDefault(), () => PseudoWord(focusLetters, allowed)));
                continue;
            }

            if (candidates.Count == 0)
                break;
            picked.Add(Different(candidates, picked.LastOrDefault(),
                options => WeightedPick(options, word => (1 + 4.0 * word.Count(focusLetters.Contains) / word.Length) * Need(word))));
        }

        return picked;
    }

    private IEnumerable<string> RandomWords()
    {
        string? previous = null;
        for (var i = 0; i < BaselineWords; i++)
            yield return previous = Different(Words, previous, list => list[_random.Next(list.Count)]);
    }

    // Picks from the words other than the one just used, so no word comes twice in a row.
    private static string Different(IReadOnlyList<string> words, string? previous, Func<IReadOnlyList<string>, string> pick)
    {
        var others = words.Where(word => word != previous).ToList();
        return pick(others.Count > 0 ? others : words);
    }

    // Made-up words are drawn again until one differs from the last; a few tries is plenty.
    private static string Different(string? previous, Func<string> make)
    {
        var word = make();
        for (var attempt = 0; attempt < 5 && word == previous; attempt++)
            word = make();
        return word;
    }

    private string PseudoWord(IReadOnlySet<char> focusLetters, IReadOnlySet<char> allowed)
    {
        var focus = focusLetters.Select(letter => letter.ToString()).ToList();
        var others = allowed.Where(char.IsAsciiLetter).Select(letter => letter.ToString()).ToList();
        var length = _random.Next(3, 6);
        return string.Concat(Enumerable.Range(0, length).Select(_ =>
            WeightedPick(_random.NextDouble() < 0.7 || others.Count == 0 ? focus : others, letter => Need(letter))));
    }

    /// <summary>
    /// How much practice <paramref name="text"/> would give the keys that need it: the mean need of its keys,
    /// scaled by the mean need of the key pairs inside it.
    /// </summary>
    public double Need(string text)
    {
        var keys = text.Select(character => CharacterActions.ActionFor(character) is { } action ? KeyFor(action) : null).OfType<string>().ToList();
        if (keys.Count == 0)
            return 1;

        var keyNeed = keys.Average(Weights.Key);
        var bigramNeed = keys.Count > 1 ? keys.Zip(keys.Skip(1), Weights.Bigram).Average() : 1;
        return keyNeed * bigramNeed;
    }

    private T WeightedPick<T>(IReadOnlyList<T> items, Func<T, double> weight)
    {
        var total = items.Sum(weight);
        var roll = _random.NextDouble() * total;
        foreach (var item in items)
        {
            roll -= weight(item);
            if (roll <= 0)
                return item;
        }

        return items[^1];
    }

    private List<DrillTarget> Text(string text)
    {
        var targets = new List<DrillTarget>();
        foreach (var character in text)
        {
            if (CharacterActions.ActionFor(character) is { } action && KeyFor(action) is { } keyId)
                targets.Add(new DrillTarget(keyId, action, character.ToString(), IsCharacter: true));
        }

        return targets;
    }

    private IEnumerable<DrillTarget> Space() => Text(" ");

    private List<DrillTarget> Targets(IEnumerable<string> keyIds)
    {
        var targets = new List<DrillTarget>();
        foreach (var keyId in keyIds.Distinct())
        {
            if (_layout.Find(BaseLayer, keyId) is not { } key || !_typableActions.Contains(key.Action))
                continue;

            targets.Add(CharacterActions.CharacterFor(key.Action) is { } character
                ? new DrillTarget(keyId, key.Action, character.ToString(), IsCharacter: true)
                : new DrillTarget(keyId, key.Action, _layout.Pack.LegendFor(key.Action), IsCharacter: false));
        }

        return targets;
    }

    // Where several keys send the same action, the one still doing its default is assumed.
    private static Dictionary<string, string> MapActionsToKeys(EffectiveLayout layout)
    {
        var keys = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var candidates = layout.Pack.Keys
            .Select(key => layout.Find(BaseLayer, key.Id))
            .OfType<EffectiveKey>()
            .OrderBy(key => key.Change == KeyChange.None ? 0 : 1);

        foreach (var key in candidates)
            keys.TryAdd(key.Action, key.Key.Id);
        return keys;
    }

    private static List<string> LoadWords()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("Rewire.Trainer.Words.txt")
            ?? throw new InvalidOperationException("The word list is missing from the trainer assembly.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd()
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct()
            .ToList();
    }
}
