namespace Rewire.Trainer;

/// <summary>
/// The characters drills use, and the action token of the key that types each one. A shifted character,
/// such as ( or {, maps to its key on a US layout; drills judge the key, not whether Shift was held.
/// </summary>
public static class CharacterActions
{
    // Declared first: the tables below are built from it as the class initialises.
    private static readonly (char Shifted, char Unshifted)[] Shifted =
    [
        ('~', '`'), ('!', '1'), ('@', '2'), ('#', '3'), ('$', '4'), ('%', '5'), ('^', '6'), ('&', '7'), ('*', '8'),
        ('(', '9'), (')', '0'), ('_', '-'), ('+', '='), ('{', '['), ('}', ']'), ('|', '\\'), (':', ';'), ('"', '\''),
        ('<', ','), ('>', '.'), ('?', '/'),
    ];

    private static readonly Dictionary<char, string> ActionsByCharacter = Build();
    private static readonly Dictionary<string, char> CharactersByAction = ActionsByCharacter
        .Where(pair => !char.IsAsciiLetterUpper(pair.Key) && !Shifted.Any(shift => shift.Shifted == pair.Key))
        .ToDictionary(pair => pair.Value, pair => pair.Key, StringComparer.OrdinalIgnoreCase);

    public static string? ActionFor(char character) => ActionsByCharacter.GetValueOrDefault(character);

    /// <summary>The character a key types with Shift held on a US layout, such as ( for 9.</summary>
    public static char? ShiftedCharacterFor(string action)
    {
        if (CharacterFor(action) is not { } character)
            return null;
        if (char.IsAsciiLetterLower(character))
            return char.ToUpperInvariant(character);
        return Shifted.FirstOrDefault(shift => shift.Unshifted == character) is { Shifted: not '\0' } match ? match.Shifted : null;
    }

    /// <summary>Whether typing <paramref name="character"/> on a US layout needs Shift held.</summary>
    public static bool NeedsShift(char character) =>
        char.IsAsciiLetterUpper(character) || Shifted.Any(shift => shift.Shifted == character);

    public static char? CharacterFor(string action) =>
        CharactersByAction.TryGetValue(action, out var character) ? character : null;

    private static Dictionary<char, string> Build()
    {
        var actions = new Dictionary<char, string>
        {
            [' '] = "space",
            ['-'] = "hyphen",
            ['['] = "obrack",
            [']'] = "cbrack",
            ['='] = "=",
            ['\\'] = @"\",
            [';'] = ";",
            ['\''] = "'",
            [','] = ",",
            ['.'] = ".",
            ['/'] = "/",
            ['`'] = "`",
        };

        for (var letter = 'a'; letter <= 'z'; letter++)
            actions[letter] = actions[char.ToUpperInvariant(letter)] = letter.ToString();
        for (var digit = '0'; digit <= '9'; digit++)
            actions[digit] = digit.ToString();

        foreach (var (shifted, unshifted) in Shifted)
            actions[shifted] = actions[unshifted];
        return actions;
    }
}
