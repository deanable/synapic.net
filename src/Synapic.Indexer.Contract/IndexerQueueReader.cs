using Microsoft.Data.Sqlite;

namespace Synapic.Indexer.Contract;

/// <summary>
/// The read half of the job queue, shared by <c>--status</c> and the desktop
/// app's Service tab. It opens the database the same safe cross-process way
/// (never creating companions while the service is stopped, reusing the
/// service's own <c>-shm</c> while it runs) and owns no write path — a reader
/// that can write is a reader that can corrupt.
/// </summary>
public static class IndexerQueueReader
{
    /// <summary>
    /// Opens a read-only connection, or returns null when the database does not
    /// exist yet. The caller disposes it; nothing here creates a file or a
    /// directory.
    /// </summary>
    public static SqliteConnection? TryOpenReadOnly(string databasePath)
    {
        if (string.IsNullOrWhiteSpace(databasePath) || !File.Exists(databasePath)) return null;

        // With no -wal file there is no writer, so SQLite can treat the file as
        // immutable: that reads the committed database without creating -shm or
        // -wal companions, which is what keeps --status from adding files. While
        // the service is running its -wal exists, so the ordinary read-only path
        // (reusing the service's own shm) is used instead.
        var hasWriteAheadLog = File.Exists(databasePath + "-wal");
        var dataSource = hasWriteAheadLog
            ? databasePath
            : new Uri(databasePath).AbsoluteUri + "?immutable=1";

        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = dataSource,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
            DefaultTimeout = 5,
        }.ToString());

        try
        {
            connection.Open();
            return connection;
        }
        catch (SqliteException)
        {
            // A WAL database whose -shm file is gone cannot be opened read-only
            // (SQLite needs to rebuild it). Fall back to a normal connection with
            // query_only, which makes writes fail at the engine instead.
            connection.Dispose();
        }

        var fallback = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false,
            DefaultTimeout = 5,
        }.ToString());
        fallback.Open();
        using var queryOnly = fallback.CreateCommand();
        queryOnly.CommandText = "PRAGMA query_only=1;";
        queryOnly.ExecuteNonQuery();
        return fallback;
    }

    /// <summary>
    /// The read-only check a status view uses before querying: never create,
    /// never repair. The schema's owner (the service's <c>QueueSchema</c>)
    /// creates these tables; a drift test pins both against each other.
    /// </summary>
    public static bool IsInitialized(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name IN ('jobs','job_events');";
        return Convert.ToInt64(command.ExecuteScalar()) == 2;
    }

    /// <summary>Counts per state plus the newest failure rows.</summary>
    public static async Task<QueueSummary> ReadSummaryAsync(
        SqliteConnection connection, CancellationToken ct = default, int failureLimit = 5)
    {
        var counts = new Dictionary<JobState, int>();
        using (var count = connection.CreateCommand())
        {
            count.CommandText = "SELECT state, COUNT(*) FROM jobs GROUP BY state;";
            using var reader = await count.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                if (JobStates.TryParse(reader.GetString(0), out var state))
                    counts[state] = (int)reader.GetInt64(1);
            }
        }

        var failures = new List<QueueFailure>(Math.Max(0, failureLimit));
        if (failureLimit > 0)
        {
            using var recent = connection.CreateCommand();
            recent.CommandText =
                "SELECT id, key, last_error, attempts FROM jobs " +
                "WHERE state IN ('Failed','TransientFailure') " +
                "ORDER BY updated_at DESC, id DESC LIMIT $limit;";
            recent.Parameters.AddWithValue("$limit", failureLimit);
            using var reader = await recent.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                failures.Add(new QueueFailure(
                    reader.GetInt64(0),
                    reader.GetString(1),
                    reader.IsDBNull(2) ? null : reader.GetString(2),
                    (int)reader.GetInt64(3)));
            }
        }

        return new QueueSummary
        {
            Pending = counts.GetValueOrDefault(JobState.Pending),
            InFlight = counts.GetValueOrDefault(JobState.InFlight),
            Completed = counts.GetValueOrDefault(JobState.Completed),
            TransientFailure = counts.GetValueOrDefault(JobState.TransientFailure),
            Failed = counts.GetValueOrDefault(JobState.Failed),
            RecentFailures = failures,
        };
    }

    /// <summary>
    /// The newest audit rows — what the queue did, newest first. The events
    /// table is append-only, so this is the service's history without a log file.
    /// </summary>
    public static async Task<IReadOnlyList<QueueEvent>> ReadRecentEventsAsync(
        SqliteConnection connection, int limit, CancellationToken ct = default)
    {
        if (limit <= 0) return Array.Empty<QueueEvent>();

        var events = new List<QueueEvent>(limit);
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT e.job_id, j.file_name, e.at, e.from_state, e.to_state, e.detail " +
            "FROM job_events e JOIN jobs j ON j.id = e.job_id " +
            "ORDER BY e.id DESC LIMIT $limit;";
        command.Parameters.AddWithValue("$limit", limit);
        using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            events.Add(new QueueEvent(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5)));
        }
        return events;
    }
}
