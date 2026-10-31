using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace Rewire.Services;

/// <summary>A layout file Rewire manages, such as <c>qwerty.txt</c> or the Program+2 profile <c>2_qwerty.txt</c>.</summary>
/// <param name="Working">The copy the user is editing.</param>
/// <param name="Keyboard">The bytes last read from or written to the keyboard, which the next apply expects to find.</param>
public sealed record Profile(string FileName, string Name, byte[] Working, byte[] Keyboard, DateTimeOffset SyncedAt)
{
    public bool HasChanges => !Working.AsSpan().SequenceEqual(Keyboard);
}

public enum ImportOutcome
{
    Added,
    Updated,
    Unchanged,

    /// <summary>The keyboard copy was refreshed but the user's unapplied edits were kept.</summary>
    KeptLocalChanges,
}

/// <summary>The local working copies of every layout file, kept in Rewire's SQLite database.</summary>
public sealed partial class ProfileStore : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly TimeProvider _time;

    public ProfileStore(string databasePath, TimeProvider? time = null)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(databasePath))!);
        _connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = databasePath }.ToString());
        _connection.Open();
        _time = time ?? TimeProvider.System;
        CreateSchema();
    }

    public static string DefaultPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Rewire", "rewire.db");

    public IReadOnlyList<Profile> List()
    {
        using var command = Command("SELECT file_name, name, working, keyboard, synced_at FROM profiles");
        using var reader = command.ExecuteReader();
        var profiles = new List<Profile>();
        while (reader.Read())
        {
            profiles.Add(new Profile(
                reader.GetString(0),
                reader.GetString(1),
                (byte[])reader[2],
                (byte[])reader[3],
                DateTimeOffset.Parse(reader.GetString(4), System.Globalization.CultureInfo.InvariantCulture)));
        }

        return profiles.OrderBy(profile => SortKey(profile.FileName), StringComparer.OrdinalIgnoreCase).ToList();
    }

    public Profile? Find(string fileName) =>
        List().FirstOrDefault(profile => string.Equals(profile.FileName, fileName, StringComparison.OrdinalIgnoreCase));

    /// <summary>Takes in the layout files read from the keyboard without losing any edits not yet applied.</summary>
    public IReadOnlyList<(string FileName, ImportOutcome Outcome)> Import(IReadOnlyDictionary<string, byte[]> keyboardFiles)
    {
        var outcomes = new List<(string, ImportOutcome)>();

        foreach (var (fileName, bytes) in keyboardFiles.OrderBy(pair => SortKey(pair.Key), StringComparer.OrdinalIgnoreCase))
        {
            var existing = Find(fileName);
            if (existing is null)
            {
                Upsert(new Profile(fileName, DefaultName(fileName), bytes, bytes, _time.GetUtcNow()));
                outcomes.Add((fileName, ImportOutcome.Added));
            }
            else if (existing.HasChanges && !existing.Working.AsSpan().SequenceEqual(bytes))
            {
                Upsert(existing with { Keyboard = bytes, SyncedAt = _time.GetUtcNow() });
                outcomes.Add((fileName, ImportOutcome.KeptLocalChanges));
            }
            else
            {
                Upsert(existing with { Working = bytes, Keyboard = bytes, SyncedAt = _time.GetUtcNow() });
                outcomes.Add((fileName, existing.Keyboard.AsSpan().SequenceEqual(bytes) ? ImportOutcome.Unchanged : ImportOutcome.Updated));
            }
        }

        return outcomes;
    }

    public void SaveWorking(string fileName, byte[] working)
    {
        using var command = Command("UPDATE profiles SET working = $working WHERE file_name = $file_name");
        command.Parameters.AddWithValue("$working", working);
        command.Parameters.AddWithValue("$file_name", fileName);
        command.ExecuteNonQuery();
    }

    /// <summary>Records that <paramref name="bytes"/> are now on the keyboard, so both copies match it.</summary>
    public void MarkWritten(string fileName, byte[] bytes)
    {
        if (Find(fileName) is { } profile)
            Upsert(profile with { Working = bytes, Keyboard = bytes, SyncedAt = _time.GetUtcNow() });
    }

    public void Rename(string fileName, string name)
    {
        using var command = Command("UPDATE profiles SET name = $name WHERE file_name = $file_name");
        command.Parameters.AddWithValue("$name", name);
        command.Parameters.AddWithValue("$file_name", fileName);
        command.ExecuteNonQuery();
    }

    public void Dispose() => _connection.Dispose();

    public static string DefaultName(string fileName)
    {
        var match = ProfileFileName().Match(fileName);
        if (!match.Success)
            return fileName;

        var family = match.Groups["family"].Value.Equals("dvorak", StringComparison.OrdinalIgnoreCase) ? "Dvorak" : "QWERTY";
        return match.Groups["hotkey"].Success
            ? $"Program+{match.Groups["hotkey"].Value.ToUpperInvariant()} ({family})"
            : family;
    }

    // QWERTY, the factory default, comes first, then Dvorak, then the Program+X profiles in hotkey order.
    private static string SortKey(string fileName) =>
        ProfileFileName().Match(fileName) is { Success: true } match && !match.Groups["hotkey"].Success
            ? match.Groups["family"].Value.Equals("qwerty", StringComparison.OrdinalIgnoreCase) ? "0" : "1"
            : $"2{fileName}";

    private void Upsert(Profile profile)
    {
        using var command = Command("""
            INSERT INTO profiles (file_name, name, working, keyboard, synced_at)
            VALUES ($file_name, $name, $working, $keyboard, $synced_at)
            ON CONFLICT (file_name) DO UPDATE SET
                name = excluded.name, working = excluded.working,
                keyboard = excluded.keyboard, synced_at = excluded.synced_at
            """);
        command.Parameters.AddWithValue("$file_name", profile.FileName);
        command.Parameters.AddWithValue("$name", profile.Name);
        command.Parameters.AddWithValue("$working", profile.Working);
        command.Parameters.AddWithValue("$keyboard", profile.Keyboard);
        command.Parameters.AddWithValue("$synced_at", profile.SyncedAt.ToString("O"));
        command.ExecuteNonQuery();
    }

    private void CreateSchema()
    {
        using var command = Command("""
            CREATE TABLE IF NOT EXISTS profiles (
                file_name TEXT PRIMARY KEY COLLATE NOCASE,
                name TEXT NOT NULL,
                working BLOB NOT NULL,
                keyboard BLOB NOT NULL,
                synced_at TEXT NOT NULL
            );
            PRAGMA user_version = 1;
            """);
        command.ExecuteNonQuery();
    }

    private SqliteCommand Command(string sql)
    {
        var command = _connection.CreateCommand();
        command.CommandText = sql;
        return command;
    }

    [GeneratedRegex(@"^((?<hotkey>[0-9a-z])_)?(?<family>qwerty|dvorak)\.txt$", RegexOptions.IgnoreCase)]
    private static partial Regex ProfileFileName();
}
