using Microsoft.Data.Sqlite;

namespace Synapic.Indexer.State;

/// <summary>
/// Schema v1 of the job store (D-11-06). Raw DDL, created idempotently on every
/// service start: no ORM, no migration runner, because the only migration this
/// build knows is "v1 exists".
/// </summary>
/// <remarks>
/// <c>key</c> is <c>COLLATE NOCASE</c> so Windows paths compare the way Windows
/// compares them: <c>C:\Photos\A.JPG</c> and <c>c:\photos\a.jpg</c> are one row,
/// and the database enforces it even if a caller forgets.
/// </remarks>
public static class QueueSchema
{
    public const int Version = 1;

    private const string JobsTable = """
        CREATE TABLE IF NOT EXISTS jobs (
            id             INTEGER PRIMARY KEY AUTOINCREMENT,
            key            TEXT NOT NULL COLLATE NOCASE UNIQUE,
            source         TEXT NOT NULL DEFAULT '',
            path           TEXT,
            daminion_id    INTEGER,
            file_name      TEXT NOT NULL,
            size           INTEGER,
            mtime_utc      TEXT,
            content_hash   TEXT,
            state          TEXT NOT NULL,
            attempts       INTEGER NOT NULL DEFAULT 0,
            last_error     TEXT,
            enqueued_at    TEXT NOT NULL,
            updated_at     TEXT NOT NULL,
            next_attempt_at TEXT
        );
        """;

    private const string JobEventsTable = """
        CREATE TABLE IF NOT EXISTS job_events (
            id         INTEGER PRIMARY KEY AUTOINCREMENT,
            job_id     INTEGER NOT NULL REFERENCES jobs(id),
            at         TEXT NOT NULL,
            from_state TEXT,
            to_state   TEXT,
            detail     TEXT
        );
        """;

    private const string StateIndex = "CREATE INDEX IF NOT EXISTS ix_jobs_state ON jobs(state);";

    private const string UpdatedIndex =
        "CREATE INDEX IF NOT EXISTS ix_jobs_updated_at ON jobs(updated_at);";

    /// <summary>
    /// Creates the tables, indexes and WAL mode. Running it twice on the same file
    /// is a no-op that keeps every row.
    /// </summary>
    public static void Ensure(SqliteConnection connection)
    {
        using (var wal = connection.CreateCommand())
        {
            wal.CommandText = "PRAGMA journal_mode=WAL;";
            wal.ExecuteNonQuery();
        }

        using (var create = connection.CreateCommand())
        {
            create.CommandText = $"{JobsTable}\n{JobEventsTable}\n{StateIndex}\n{UpdatedIndex}";
            create.ExecuteNonQuery();
        }

        using var version = connection.CreateCommand();
        version.CommandText = $"PRAGMA user_version={Version};";
        version.ExecuteNonQuery();
    }

    /// <summary>The read-only check <c>--status</c> uses: never create, never repair.</summary>
    public static bool IsInitialized(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name IN ('jobs','job_events');";
        return Convert.ToInt64(command.ExecuteScalar()) == 2;
    }
}
