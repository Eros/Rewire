using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Rewire.Trainer;

/// <summary>How much practice each key and each pair of keys needs; 1 is ordinary, higher means more.</summary>
public sealed record SkillWeights(IReadOnlyDictionary<string, double> Keys, IReadOnlyDictionary<string, double> Bigrams)
{
    public static SkillWeights None { get; } = new(new Dictionary<string, double>(), new Dictionary<string, double>());

    public double Key(string keyId) => Keys.GetValueOrDefault(keyId, 1);

    public double Bigram(string firstKeyId, string secondKeyId) => Bigrams.GetValueOrDefault(BigramId(firstKeyId, secondKeyId), 1);

    public static string BigramId(string firstKeyId, string secondKeyId) => $"{firstKeyId}>{secondKeyId}";
}

/// <summary>
/// Per-key and per-bigram scores combining error rate and speed, with spaced repetition: a mastered key
/// is drilled less and less often, and a key the user has just remapped is drilled more until it is relearnt.
/// </summary>
public sealed class SkillScores
{
    private const double Smoothing = 0.3;
    private const double MasteredErrorRate = 0.03;
    private const int MinimumPresses = 5;
    private const int RelearnPresses = 40;
    private const double NotDueWeight = 0.25;
    private const double RelearnWeight = 3;

    private readonly SqliteConnection _connection;

    internal SkillScores(SqliteConnection connection)
    {
        _connection = connection;
        using var command = Command("""
            CREATE TABLE IF NOT EXISTS key_scores (
                subject TEXT PRIMARY KEY,
                error_rate REAL NOT NULL,
                latency_ms REAL NOT NULL,
                presses INTEGER NOT NULL,
                streak INTEGER NOT NULL,
                next_review TEXT NOT NULL,
                relearn_remaining INTEGER NOT NULL DEFAULT 0
            );
            CREATE TABLE IF NOT EXISTS trained_layout (
                key_id TEXT PRIMARY KEY,
                action TEXT NOT NULL
            );
            """);
        command.ExecuteNonQuery();
    }

    public void Update(TypingSession session, DateTimeOffset now)
    {
        var samples = new Dictionary<string, (int Presses, int Errors, double LatencyTotal, int Correct)>();

        void Add(string subject, Keystroke keystroke)
        {
            var (presses, errors, latency, correct) = samples.GetValueOrDefault(subject);
            samples[subject] = keystroke.Correct
                ? (presses + 1, errors, latency + keystroke.Latency.TotalMilliseconds, correct + 1)
                : (presses + 1, errors + 1, latency, correct);
        }

        foreach (var keystroke in session.Keystrokes)
        {
            Add(keystroke.IntendedKeyId, keystroke);
            if (keystroke.Position > 0)
                Add(SkillWeights.BigramId(session.Targets[keystroke.Position - 1].KeyId, keystroke.IntendedKeyId), keystroke);
        }

        var latencies = samples.Values.Where(sample => sample.Correct > 0).Select(sample => sample.LatencyTotal / sample.Correct).Order().ToList();
        var median = latencies.Count > 0 ? latencies[latencies.Count / 2] : 0;

        using var transaction = _connection.BeginTransaction();
        foreach (var (subject, sample) in samples)
        {
            var errorRate = (double)sample.Errors / sample.Presses;
            var latency = sample.Correct > 0 ? sample.LatencyTotal / sample.Correct : median;
            var previous = Read(subject, transaction);

            var mastered = sample.Presses >= MinimumPresses && errorRate < MasteredErrorRate && latency <= median * 1.2;
            var streak = mastered ? (previous?.Streak ?? 0) + 1 : 0;
            var nextReview = mastered ? now.AddDays(Math.Pow(2, streak - 1)) : now;

            using var upsert = Command("""
                INSERT INTO key_scores (subject, error_rate, latency_ms, presses, streak, next_review, relearn_remaining)
                VALUES ($subject, $error_rate, $latency, $presses, $streak, $next_review, $relearn)
                ON CONFLICT (subject) DO UPDATE SET
                    error_rate = excluded.error_rate, latency_ms = excluded.latency_ms, presses = excluded.presses,
                    streak = excluded.streak, next_review = excluded.next_review, relearn_remaining = excluded.relearn_remaining
                """, transaction);
            upsert.Parameters.AddWithValue("$subject", subject);
            upsert.Parameters.AddWithValue("$error_rate", previous is null ? errorRate : Blend(previous.Value.ErrorRate, errorRate));
            upsert.Parameters.AddWithValue("$latency", previous is null ? latency : Blend(previous.Value.Latency, latency));
            upsert.Parameters.AddWithValue("$presses", (previous?.Presses ?? 0) + sample.Presses);
            upsert.Parameters.AddWithValue("$streak", streak);
            upsert.Parameters.AddWithValue("$next_review", nextReview.ToString("O"));
            upsert.Parameters.AddWithValue("$relearn", Math.Max(0, (previous?.Relearn ?? 0) - sample.Correct));
            upsert.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    public SkillWeights Weights(DateTimeOffset now)
    {
        using var command = Command("SELECT subject, error_rate, latency_ms, next_review, relearn_remaining FROM key_scores");
        using var reader = command.ExecuteReader();
        var rows = new List<(string Subject, double ErrorRate, double Latency, DateTimeOffset NextReview, int Relearn)>();
        while (reader.Read())
        {
            rows.Add((reader.GetString(0), reader.GetDouble(1), reader.GetDouble(2),
                DateTimeOffset.Parse(reader.GetString(3), CultureInfo.InvariantCulture), reader.GetInt32(4)));
        }

        var latencies = rows.Select(row => row.Latency).Where(latency => latency > 0).Order().ToList();
        var median = latencies.Count > 0 ? latencies[latencies.Count / 2] : 1;

        var keys = new Dictionary<string, double>();
        var bigrams = new Dictionary<string, double>();
        foreach (var row in rows)
        {
            var weight = (1 + 8 * row.ErrorRate + Math.Max(0, row.Latency / median - 1))
                * (row.NextReview <= now ? 1 : NotDueWeight)
                * (row.Relearn > 0 ? RelearnWeight : 1);
            (row.Subject.Contains('>') ? bigrams : keys)[row.Subject] = weight;
        }

        return new SkillWeights(keys, bigrams);
    }

    /// <summary>Keys still being relearnt after a remap.</summary>
    public IReadOnlyList<string> Relearning()
    {
        using var command = Command("SELECT subject FROM key_scores WHERE relearn_remaining > 0 AND subject NOT LIKE '%>%' ORDER BY subject");
        using var reader = command.ExecuteReader();
        var keys = new List<string>();
        while (reader.Read())
            keys.Add(reader.GetString(0));
        return keys;
    }

    /// <summary>
    /// Compares what each key does now with what it did when last trained, and queues every key whose action
    /// changed for relearning. The first layout seen is only recorded.
    /// </summary>
    public IReadOnlyList<string> NoteLayout(IReadOnlyDictionary<string, string> actionsByKey, DateTimeOffset now)
    {
        var previous = new Dictionary<string, string>();
        using (var read = Command("SELECT key_id, action FROM trained_layout"))
        using (var reader = read.ExecuteReader())
        {
            while (reader.Read())
                previous[reader.GetString(0)] = reader.GetString(1);
        }

        var changed = previous.Count == 0
            ? []
            : actionsByKey.Where(pair => previous.TryGetValue(pair.Key, out var action) && !string.Equals(action, pair.Value, StringComparison.OrdinalIgnoreCase))
                .Select(pair => pair.Key)
                .Order()
                .ToList();

        using var transaction = _connection.BeginTransaction();
        foreach (var (keyId, action) in actionsByKey)
        {
            using var save = Command("INSERT INTO trained_layout (key_id, action) VALUES ($key, $action) ON CONFLICT (key_id) DO UPDATE SET action = excluded.action", transaction);
            save.Parameters.AddWithValue("$key", keyId);
            save.Parameters.AddWithValue("$action", action);
            save.ExecuteNonQuery();
        }

        foreach (var keyId in changed)
        {
            using var relearn = Command("""
                INSERT INTO key_scores (subject, error_rate, latency_ms, presses, streak, next_review, relearn_remaining)
                VALUES ($key, 0, 0, 0, 0, $now, $relearn)
                ON CONFLICT (subject) DO UPDATE SET relearn_remaining = excluded.relearn_remaining, streak = 0, next_review = excluded.next_review
                """, transaction);
            relearn.Parameters.AddWithValue("$key", keyId);
            relearn.Parameters.AddWithValue("$now", now.ToString("O"));
            relearn.Parameters.AddWithValue("$relearn", RelearnPresses);
            relearn.ExecuteNonQuery();
        }

        transaction.Commit();
        return changed;
    }

    private static double Blend(double previous, double latest) => previous * (1 - Smoothing) + latest * Smoothing;

    private (double ErrorRate, double Latency, int Presses, int Streak, int Relearn)? Read(string subject, SqliteTransaction transaction)
    {
        using var command = Command("SELECT error_rate, latency_ms, presses, streak, relearn_remaining FROM key_scores WHERE subject = $subject", transaction);
        command.Parameters.AddWithValue("$subject", subject);
        using var reader = command.ExecuteReader();
        return reader.Read()
            ? (reader.GetDouble(0), reader.GetDouble(1), reader.GetInt32(2), reader.GetInt32(3), reader.GetInt32(4))
            : null;
    }

    private SqliteCommand Command(string sql, SqliteTransaction? transaction = null)
    {
        var command = _connection.CreateCommand();
        command.CommandText = sql;
        command.Transaction = transaction;
        return command;
    }
}
