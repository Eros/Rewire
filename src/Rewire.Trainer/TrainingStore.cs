using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Rewire.Trainer;

public sealed record SessionSummary(
    long Id,
    string StageId,
    DrillKind Drill,
    DateTimeOffset StartedAt,
    DateTimeOffset EndedAt,
    double WordsPerMinute,
    double Accuracy);

/// <param name="ErrorRate">Wrong presses as a share of all presses aimed at this key.</param>
/// <param name="MeanLatency">Average time to press this key correctly.</param>
public sealed record KeyStatistics(string KeyId, int Presses, double ErrorRate, TimeSpan MeanLatency);

public enum StageState
{
    Locked,
    Open,
    Passed,
}

public sealed record StageStatus(Stage Stage, StageState State, SessionSummary? Best, double? RequiredWordsPerMinute);

/// <summary>Every finished practice session and keystroke, kept in Rewire's SQLite database.</summary>
public sealed class TrainingStore : IDisposable
{
    private readonly SqliteConnection _connection;

    public TrainingStore(string databasePath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(databasePath))!);
        _connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = databasePath }.ToString());
        _connection.Open();
        CreateSchema();
        Scores = new SkillScores(_connection);
    }

    public SkillScores Scores { get; }

    /// <summary>Saves a session that ran to the end; abandoned sessions are not worth keeping.</summary>
    public SessionSummary Save(TypingSession session)
    {
        if (session is not { IsComplete: true, StartedAt: { } startedAt, EndedAt: { } endedAt })
            throw new InvalidOperationException("Only a finished session can be saved.");

        using var transaction = _connection.BeginTransaction();
        using var insertSession = Command("""
            INSERT INTO sessions (stage, drill, started_at, ended_at, wpm, accuracy)
            VALUES ($stage, $drill, $started_at, $ended_at, $wpm, $accuracy)
            RETURNING id
            """, transaction);
        insertSession.Parameters.AddWithValue("$stage", session.Drill.Stage.Id);
        insertSession.Parameters.AddWithValue("$drill", session.Drill.Stage.Drill.ToString());
        insertSession.Parameters.AddWithValue("$started_at", startedAt.ToString("O"));
        insertSession.Parameters.AddWithValue("$ended_at", endedAt.ToString("O"));
        insertSession.Parameters.AddWithValue("$wpm", session.WordsPerMinute);
        insertSession.Parameters.AddWithValue("$accuracy", session.Accuracy);
        var id = (long)insertSession.ExecuteScalar()!;

        using var insertKeystroke = Command("""
            INSERT INTO keystrokes (session_id, at, intended_key, intended_action, pressed_key, pressed_action, latency_ms, correct, drill, old_habit)
            VALUES ($session_id, $at, $intended_key, $intended_action, $pressed_key, $pressed_action, $latency_ms, $correct, $drill, $old_habit)
            """, transaction);
        foreach (var keystroke in session.Keystrokes)
        {
            insertKeystroke.Parameters.Clear();
            insertKeystroke.Parameters.AddWithValue("$session_id", id);
            insertKeystroke.Parameters.AddWithValue("$at", keystroke.At.ToString("O"));
            insertKeystroke.Parameters.AddWithValue("$intended_key", keystroke.IntendedKeyId);
            insertKeystroke.Parameters.AddWithValue("$intended_action", keystroke.IntendedAction);
            insertKeystroke.Parameters.AddWithValue("$pressed_key", (object?)keystroke.PressedKeyId ?? DBNull.Value);
            insertKeystroke.Parameters.AddWithValue("$pressed_action", keystroke.PressedAction);
            insertKeystroke.Parameters.AddWithValue("$latency_ms", (int)keystroke.Latency.TotalMilliseconds);
            insertKeystroke.Parameters.AddWithValue("$correct", keystroke.Correct);
            insertKeystroke.Parameters.AddWithValue("$drill", session.Drill.Stage.Drill.ToString());
            insertKeystroke.Parameters.AddWithValue("$old_habit", (object?)keystroke.OldHabit ?? DBNull.Value);
            insertKeystroke.ExecuteNonQuery();
        }

        transaction.Commit();

        // The baseline is typed on the old keyboard, so it says nothing about skill on this one.
        if (session.Drill.Stage.Drill != DrillKind.Baseline)
            Scores.Update(session, endedAt);

        return new SessionSummary(id, session.Drill.Stage.Id, session.Drill.Stage.Drill, startedAt, endedAt, session.WordsPerMinute, session.Accuracy);
    }

    /// <summary>Finished sessions, newest first.</summary>
    public IReadOnlyList<SessionSummary> Sessions(string? stageId = null)
    {
        using var command = Command("""
            SELECT id, stage, drill, started_at, ended_at, wpm, accuracy FROM sessions
            WHERE $stage IS NULL OR stage = $stage
            ORDER BY started_at DESC
            """);
        command.Parameters.AddWithValue("$stage", (object?)stageId ?? DBNull.Value);
        using var reader = command.ExecuteReader();

        var sessions = new List<SessionSummary>();
        while (reader.Read())
        {
            sessions.Add(new SessionSummary(
                reader.GetInt64(0),
                reader.GetString(1),
                Enum.Parse<DrillKind>(reader.GetString(2)),
                DateTimeOffset.Parse(reader.GetString(3), CultureInfo.InvariantCulture),
                DateTimeOffset.Parse(reader.GetString(4), CultureInfo.InvariantCulture),
                reader.GetDouble(5),
                reader.GetDouble(6)));
        }

        return sessions;
    }

    /// <summary>The most recent baseline speed, which every later stage is measured against.</summary>
    public SessionSummary? Baseline() => Sessions().FirstOrDefault(session => session.Drill == DrillKind.Baseline);

    /// <summary>Per-key error rate and speed, leaving out the baseline, which was typed on another keyboard.</summary>
    public IReadOnlyList<KeyStatistics> KeyStatistics(DateTimeOffset since)
    {
        using var command = Command("""
            SELECT intended_key,
                   COUNT(*),
                   AVG(CASE WHEN correct THEN 0.0 ELSE 1.0 END),
                   AVG(CASE WHEN correct THEN latency_ms END)
            FROM keystrokes
            WHERE at >= $since AND drill <> $baseline
            GROUP BY intended_key
            """);
        command.Parameters.AddWithValue("$since", since.ToString("O"));
        command.Parameters.AddWithValue("$baseline", nameof(DrillKind.Baseline));
        using var reader = command.ExecuteReader();

        var statistics = new List<KeyStatistics>();
        while (reader.Read())
        {
            statistics.Add(new KeyStatistics(
                reader.GetString(0),
                reader.GetInt32(1),
                reader.GetDouble(2),
                TimeSpan.FromMilliseconds(reader.IsDBNull(3) ? 0 : reader.GetDouble(3))));
        }

        return statistics;
    }

    /// <summary>
    /// Level 1 is always open; each later level opens once the three most recent templates at the level
    /// below average 95% accuracy.
    /// </summary>
    public IReadOnlyList<StageState> CodeLevels(string language)
    {
        var states = new List<StageState>();
        var open = true;
        for (var level = 1; level <= CodeTemplates.Levels; level++)
        {
            var recent = Sessions(CodeTemplates.StageId(language, level)).Take(3).ToList();
            var passed = recent.Count == 3 && recent.Average(session => session.Accuracy) >= Curriculum.PassingAccuracy;
            states.Add(!open ? StageState.Locked : passed ? StageState.Passed : StageState.Open);
            open = passed;
        }

        return states;
    }

    /// <summary>Saves a finished shortcut drill; its speed is counted in shortcuts per minute.</summary>
    public SessionSummary Save(ShortcutSession session)
    {
        if (session is not { IsComplete: true, StartedAt: { } startedAt, EndedAt: { } endedAt })
            throw new InvalidOperationException("Only a finished session can be saved.");

        var minutes = Math.Max((endedAt - startedAt).TotalMinutes, 1e-6);
        var perMinute = session.Prompts.Count / minutes;
        using var insert = Command("""
            INSERT INTO sessions (stage, drill, started_at, ended_at, wpm, accuracy)
            VALUES ($stage, $drill, $started_at, $ended_at, $wpm, $accuracy)
            RETURNING id
            """);
        insert.Parameters.AddWithValue("$stage", session.StageId);
        insert.Parameters.AddWithValue("$drill", nameof(DrillKind.Shortcuts));
        insert.Parameters.AddWithValue("$started_at", startedAt.ToString("O"));
        insert.Parameters.AddWithValue("$ended_at", endedAt.ToString("O"));
        insert.Parameters.AddWithValue("$wpm", perMinute);
        insert.Parameters.AddWithValue("$accuracy", session.Accuracy);
        var id = (long)insert.ExecuteScalar()!;
        return new SessionSummary(id, session.StageId, DrillKind.Shortcuts, startedAt, endedAt, perMinute, session.Accuracy);
    }

    public string? Setting(string key)
    {
        using var command = Command("SELECT value FROM settings WHERE key = $key");
        command.Parameters.AddWithValue("$key", key);
        return command.ExecuteScalar() as string;
    }

    public void SetSetting(string key, string value)
    {
        using var command = Command("INSERT INTO settings (key, value) VALUES ($key, $value) ON CONFLICT (key) DO UPDATE SET value = excluded.value");
        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$value", value);
        command.ExecuteNonQuery();
    }

    /// <summary>How often each key was missed by reaching where its old keyboard had it, most frequent first.</summary>
    public IReadOnlyList<(string IntendedKey, string OldKey, int Count)> OldHabits(DateTimeOffset since)
    {
        using var command = Command("""
            SELECT intended_key, old_habit, COUNT(*) FROM keystrokes
            WHERE old_habit IS NOT NULL AND at >= $since
            GROUP BY intended_key, old_habit
            ORDER BY COUNT(*) DESC
            """);
        command.Parameters.AddWithValue("$since", since.ToString("O"));
        using var reader = command.ExecuteReader();

        var habits = new List<(string, string, int)>();
        while (reader.Read())
            habits.Add((reader.GetString(0), reader.GetString(1), reader.GetInt32(2)));
        return habits;
    }

    /// <summary>
    /// The first stage, the baseline, is always open. Each later stage opens once the one before it is passed:
    /// a finished session at 95% accuracy and at least that stage's share of baseline speed.
    /// </summary>
    public IReadOnlyList<StageStatus> Progress(IReadOnlyList<Stage> stages)
    {
        var baseline = Baseline();
        var statuses = new List<StageStatus>();
        var open = true;

        foreach (var stage in stages)
        {
            var required = stage.Drill == DrillKind.Baseline || baseline is null ? (double?)null : baseline.WordsPerMinute * stage.SpeedShare;
            var sessions = Sessions(stage.Id);
            var passing = sessions.Where(session => Passes(session, stage, required)).ToList();
            var best = (passing.Count > 0 ? passing : sessions).MaxBy(session => session.WordsPerMinute);
            var state = !open ? StageState.Locked : passing.Count > 0 ? StageState.Passed : StageState.Open;

            statuses.Add(new StageStatus(stage, state, best, required));
            open = state == StageState.Passed;
        }

        return statuses;
    }

    public void Dispose() => _connection.Dispose();

    private static bool Passes(SessionSummary session, Stage stage, double? required) =>
        stage.Drill == DrillKind.Baseline
        || (session.Accuracy >= Curriculum.PassingAccuracy && session.WordsPerMinute >= (required ?? 0));

    private void CreateSchema()
    {
        using var command = Command("""
            CREATE TABLE IF NOT EXISTS sessions (
                id INTEGER PRIMARY KEY,
                stage TEXT NOT NULL,
                drill TEXT NOT NULL,
                started_at TEXT NOT NULL,
                ended_at TEXT NOT NULL,
                wpm REAL NOT NULL,
                accuracy REAL NOT NULL
            );
            CREATE TABLE IF NOT EXISTS keystrokes (
                id INTEGER PRIMARY KEY,
                session_id INTEGER NOT NULL REFERENCES sessions (id),
                at TEXT NOT NULL,
                intended_key TEXT NOT NULL,
                intended_action TEXT NOT NULL,
                pressed_key TEXT,
                pressed_action TEXT NOT NULL,
                latency_ms INTEGER NOT NULL,
                correct INTEGER NOT NULL,
                drill TEXT NOT NULL,
                old_habit TEXT
            );
            CREATE INDEX IF NOT EXISTS keystrokes_by_time ON keystrokes (at);
            CREATE TABLE IF NOT EXISTS settings (
                key TEXT PRIMARY KEY,
                value TEXT NOT NULL
            );
            """);
        command.ExecuteNonQuery();
    }

    private SqliteCommand Command(string sql, SqliteTransaction? transaction = null)
    {
        var command = _connection.CreateCommand();
        command.CommandText = sql;
        command.Transaction = transaction;
        return command;
    }
}
