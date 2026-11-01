using System.Text.Json;

namespace Rewire.Trainer;

public sealed record Shortcut(string Action, string Chord);

/// <summary>A set of key bindings to learn, such as an IDE's keymap, from <c>shortcuts/&lt;id&gt;.json</c>.</summary>
public sealed record ShortcutPack(string Id, string Name, string Platform, IReadOnlyList<Shortcut> Shortcuts)
{
    /// <summary>A macOS pack writes the Command key as cmd and Option as alt; other packs write Meta as win.</summary>
    public bool IsMac => Platform.Equals("macos", StringComparison.OrdinalIgnoreCase);

    public static IReadOnlyList<ShortcutPack> Load(string directory, string platform)
    {
        if (!Directory.Exists(directory))
            return [];

        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        return Directory.EnumerateFiles(directory, "*.json")
            .Select(path => (Path: path, File: JsonSerializer.Deserialize<PackJson>(File.ReadAllText(path), options)))
            .Where(pack => pack.File is { Platform: { } packPlatform } && packPlatform.Equals(platform, StringComparison.OrdinalIgnoreCase))
            .Select(pack => new ShortcutPack(
                Path.GetFileNameWithoutExtension(pack.Path),
                pack.File!.Name ?? Path.GetFileNameWithoutExtension(pack.Path),
                pack.File.Platform!,
                (pack.File.Shortcuts ?? []).Select(shortcut => new Shortcut(shortcut.Action, Chords.Normalise(shortcut.Keys))).ToList()))
            .OrderBy(pack => pack.Name)
            .ToList();
    }

    private sealed record PackJson(string? Name, string? Platform, List<ShortcutJson>? Shortcuts);

    private sealed record ShortcutJson(string Action, string Keys);
}

/// <summary>Key combinations written as modifier+key tokens, such as <c>cmd+shift+a</c>, in one canonical order.</summary>
public static class Chords
{
    private static readonly string[] ModifierOrder = ["ctrl", "alt", "shift", "cmd", "win"];

    public static string Normalise(string chord)
    {
        var parts = chord.ToLowerInvariant().Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var modifiers = parts.Where(part => ModifierOrder.Contains(part)).Distinct().OrderBy(part => Array.IndexOf(ModifierOrder, part));
        var keys = parts.Where(part => !ModifierOrder.Contains(part));
        return string.Join('+', modifiers.Concat(keys));
    }

    public static string Describe(string chord, bool mac, Func<string, string> legend) =>
        string.Join(" + ", chord.Split('+').Select(part => part switch
        {
            "cmd" => "Cmd",
            "ctrl" => "Ctrl",
            "alt" => mac ? "Option" : "Alt",
            "shift" => "Shift",
            "win" => "Win",
            _ => legend(part),
        }));
}

public enum ShortcutResult
{
    Correct,
    Wrong,
}

/// <summary>
/// Asks for each shortcut in turn. A wrong combination is counted and the prompt stays; after two misses
/// the answer is shown so the user can press it and move on.
/// </summary>
public sealed class ShortcutSession(ShortcutPack pack, IReadOnlyList<Shortcut> prompts, TimeProvider? time = null)
{
    public ShortcutPack Pack { get; } = pack;

    private const int MissesBeforeReveal = 2;

    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private int _misses;
    private int _rightFirstTime;

    public string StageId { get; } = $"shortcuts:{pack.Id}";

    public IReadOnlyList<Shortcut> Prompts { get; } = prompts;

    public int Position { get; private set; }

    public Shortcut? Current => Position < Prompts.Count ? Prompts[Position] : null;

    public bool IsRevealed => _misses >= MissesBeforeReveal;

    public DateTimeOffset? StartedAt { get; private set; }

    public DateTimeOffset? EndedAt { get; private set; }

    public bool IsComplete => EndedAt is not null;

    /// <summary>The share of shortcuts pressed correctly at the first attempt.</summary>
    public double Accuracy => Position == 0 ? 1 : (double)_rightFirstTime / Position;

    public ShortcutResult Press(string chord)
    {
        if (Current is not { } current)
            throw new InvalidOperationException("The session has finished.");

        StartedAt ??= _time.GetUtcNow();
        if (Chords.Normalise(chord) != current.Chord)
        {
            _misses++;
            return ShortcutResult.Wrong;
        }

        if (_misses == 0)
            _rightFirstTime++;
        _misses = 0;
        Position++;
        if (Position == Prompts.Count)
            EndedAt = _time.GetUtcNow();
        return ShortcutResult.Correct;
    }
}
