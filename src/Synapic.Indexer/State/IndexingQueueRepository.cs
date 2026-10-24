using System.Data;
using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Synapic.Indexer.State;

/// <summary>
/// The one place the job queue is read or written. Every detector goes through
/// <see cref="EnqueueAsync"/>; phase 7 claims work and writes outcomes back
/// through the <c>Mark*</c> methods. No SQL exists outside this class except the
/// DDL in <see cref="QueueSchema"/>.
/// </summary>
/// <remarks>
/// Concurrency model (D-11-06): one short-lived pooled connection per operation,
/// writes serialized behind a process-wide lock on this instance, and
/// <c>BEGIN IMMEDIATE</c> for claiming so a second process (a stray
/// <c>--console</c>) cannot take a row this one is about to claim. Reads open
/// their own connection and never wait behind the write lock.
/// </remarks>
public sealed class IndexingQueueRepository
{
    private const int MaxErrorLength = 1000;
    private const int RecentFailureLimit = 5;

    private const string JobColumns =
        "id, key, source, path, daminion_id, file_name, size, mtime_utc, content_hash, " +
        "state, attempts, last_error, enqueued_at, updated_at, next_attempt_at";

    private readonly string _databasePath;
    private readonly TimeProvider _clock;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public IndexingQueueRepository(string databasePath, TimeProvider? clock = null)
    {
        if (string.IsNullOrWhiteSpace(databasePath))
            throw new ArgumentException("A queue database path is required.", nameof(databasePath));

        _databasePath = databasePath;
        _clock = clock ?? TimeProvider.System;
    }

    public string DatabasePath => _databasePath;

    /// <summary>Creates the schema (and its directory), preserving any existing rows.</summary>
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        var directory = Path.GetDirectoryName(_databasePath);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var connection = Open();
            QueueSchema.Ensure(connection);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    // ---------------------------------------------------------------- reads

    public async Task<JobRecord?> GetAsync(long id, CancellationToken ct = default)
    {
        using var connection = Open();
        return await SelectByIdAsync(connection, null, id, ct).ConfigureAwait(false);
    }

    public async Task<JobRecord?> GetByKeyAsync(string key, CancellationToken ct = default)
    {
        using var connection = Open();
        return await SelectByKeyAsync(connection, null, key, ct).ConfigureAwait(false);
    }

    /// <summary>Counts per state plus the five newest failure rows, for <c>--status</c>.</summary>
    public async Task<QueueSummary> GetSummaryAsync(CancellationToken ct = default)
    {
        using var connection = Open();
        return await IndexerQueueReader.ReadSummaryAsync(connection, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// The idle timer's view (D-11-18): work exists while any row is Pending,
    /// InFlight or TransientFailure, and the newest of those rows is the last time
    /// anything moved.
    /// </summary>
    public Task<QueueActivity> GetActivityAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(GetActivity());
    }

    /// <summary>
    /// The synchronous form. The idle timer reads this from a timer callback, so
    /// a blocking call here is the intended use — there is no UI to starve.
    /// </summary>
    public QueueActivity GetActivity()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            $"SELECT COUNT(*), MAX(updated_at) FROM jobs WHERE state IN {OpenStateList};";

        using var reader = command.ExecuteReader();
        if (!reader.Read()) return QueueActivity.Idle;

        var hasPending = reader.GetInt64(0) > 0;
        var lastWorkAt = reader.IsDBNull(1) ? null : ParseTimestamp(reader.GetString(1));
        return new QueueActivity(hasPending, lastWorkAt);
    }

    private const string OpenStateList = "('Pending','InFlight','TransientFailure')";

    // --------------------------------------------------------------- writes

    /// <summary>
    /// Upserts a batch by key in one transaction, applying the idempotency matrix
    /// from the CONTEXT: already-queued work is a no-op, completed-unchanged is a
    /// no-op, and anything that changed comes back as Pending.
    /// </summary>
    public async Task<IntakeResult> EnqueueAsync(IReadOnlyList<JobCandidate> candidates, CancellationToken ct = default)
    {
        if (candidates.Count == 0) return IntakeResult.Empty;

        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var connection = Open();
            using var transaction = connection.BeginTransaction(IsolationLevel.Serializable, deferred: false);

            var now = Now();
            var enqueuedKeys = new List<string>();

            foreach (var candidate in candidates)
            {
                var outcome = await EnqueueOneAsync(connection, transaction, candidate, now, ct).ConfigureAwait(false);
                if (outcome is not null) enqueuedKeys.Add(outcome);
            }

            transaction.Commit();
            return new IntakeResult(enqueuedKeys.Count, candidates.Count - enqueuedKeys.Count, enqueuedKeys);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>
    /// Claims up to <paramref name="max"/> due Pending rows as InFlight, oldest
    /// first, in one immediate transaction. Returns the claimed rows already
    /// carrying their incremented attempt count.
    /// </summary>
    public async Task<IReadOnlyList<JobRecord>> ClaimBatchAsync(int max, CancellationToken ct = default)
    {
        if (max <= 0) return Array.Empty<JobRecord>();

        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var connection = Open();
            using var transaction = connection.BeginTransaction(IsolationLevel.Serializable, deferred: false);
            var now = Now();

            // A retry whose time has come becomes claimable here rather than in
            // a timer of its own: Pending is the only state the executor takes
            // work from, so the promotion is part of claiming.
            await PromoteDueRetriesAsync(connection, transaction, now, ct).ConfigureAwait(false);

            var due = new List<JobRecord>();
            using (var select = connection.CreateCommand())
            {
                select.Transaction = transaction;
                select.CommandText =
                    $"SELECT {JobColumns} FROM jobs WHERE state = 'Pending' " +
                    "AND (next_attempt_at IS NULL OR next_attempt_at <= $now) " +
                    "ORDER BY id ASC LIMIT $max;";
                select.Parameters.AddWithValue("$now", FormatTimestamp(now));
                select.Parameters.AddWithValue("$max", max);
                using var reader = await select.ExecuteReaderAsync(ct).ConfigureAwait(false);
                while (await reader.ReadAsync(ct).ConfigureAwait(false))
                    due.Add(ReadJob(reader));
            }

            if (due.Count == 0)
            {
                transaction.Commit();
                return Array.Empty<JobRecord>();
            }

            var claimed = new List<JobRecord>(due.Count);
            foreach (var job in due)
            {
                JobStateMachine.EnsureTransition(job.State, JobState.InFlight, job.Key);
                await UpdateStateAsync(connection, transaction, job.Id, JobState.InFlight, now,
                    error: null, nextAttemptAt: null, ct).ConfigureAwait(false);

                // The attempt count belongs to the claim, not to the caller: the
                // classifier's "give up after N" rule reads it from the row.
                using (var bump = connection.CreateCommand())
                {
                    bump.Transaction = transaction;
                    bump.CommandText = "UPDATE jobs SET attempts = attempts + 1 WHERE id = $id;";
                    bump.Parameters.AddWithValue("$id", job.Id);
                    await bump.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                }

                await AppendEventAsync(connection, transaction, job.Id, now, job.State, JobState.InFlight,
                    "claimed", ct).ConfigureAwait(false);
                claimed.Add(job with { State = JobState.InFlight, Attempts = job.Attempts + 1, UpdatedAt = now });
            }

            transaction.Commit();
            return claimed;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private static async Task PromoteDueRetriesAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        DateTime now,
        CancellationToken ct)
    {
        var rows = new List<(long Id, string Key)>();
        using (var select = connection.CreateCommand())
        {
            select.Transaction = transaction;
            select.CommandText =
                "SELECT id, key FROM jobs WHERE state = 'TransientFailure' " +
                "AND (next_attempt_at IS NULL OR next_attempt_at <= $now) ORDER BY id;";
            select.Parameters.AddWithValue("$now", FormatTimestamp(now));
            using var reader = await select.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
                rows.Add((reader.GetInt64(0), reader.GetString(1)));
        }

        foreach (var (id, key) in rows)
        {
            JobStateMachine.EnsureTransition(JobState.TransientFailure, JobState.Pending, key);
            await UpdateStateAsync(connection, transaction, id, JobState.Pending, now,
                error: null, nextAttemptAt: null, ct).ConfigureAwait(false);
            await AppendEventAsync(connection, transaction, id, now, JobState.TransientFailure, JobState.Pending,
                "retry due", ct).ConfigureAwait(false);
        }
    }

    public Task<bool> MarkCompletedAsync(long id, string? detail = null, CancellationToken ct = default) =>
        MarkAsync(id, JobState.Completed, error: null, nextAttemptAt: null, detail, ct);

    public Task<bool> MarkTransientAsync(long id, string error, DateTime retryAtUtc, CancellationToken ct = default) =>
        MarkAsync(id, JobState.TransientFailure, error, retryAtUtc, detail: null, ct);

    public Task<bool> MarkFailedAsync(long id, string error, CancellationToken ct = default) =>
        MarkAsync(id, JobState.Failed, error, nextAttemptAt: null, detail: null, ct);

    /// <summary>
    /// Puts a claimed row back in the queue without counting an attempt as a
    /// failure — an abandoned batch, a paused catalog outage, or a cancellation.
    /// </summary>
    public Task<bool> ReturnToPendingAsync(long id, string detail, CancellationToken ct = default) =>
        MarkAsync(id, JobState.Pending, error: null, nextAttemptAt: null, detail, ct);

    /// <summary>
    /// Moves a transient row's next attempt, without counting a transition. Used
    /// when the reason to wait changed (a failed sidecar restart is longer than a
    /// locked file) but the state did not.
    /// </summary>
    public async Task<bool> RescheduleRetryAsync(long id, DateTime retryAtUtc, string error, CancellationToken ct = default)
    {
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var connection = Open();
            using var transaction = connection.BeginTransaction(IsolationLevel.Serializable, deferred: false);
            var existing = await SelectByIdAsync(connection, transaction, id, ct).ConfigureAwait(false);
            if (existing is not { State: JobState.TransientFailure })
            {
                transaction.Commit();
                return false;
            }

            await UpdateStateAsync(connection, transaction, id, JobState.TransientFailure, Now(),
                Truncate(error), retryAtUtc, ct).ConfigureAwait(false);
            transaction.Commit();
            return true;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>
    /// Stores the file's fingerprint after a successful write, so the next scan
    /// sees "same size, same time, same bytes" instead of enqueueing the file the
    /// service itself just changed (D-11-26). Only updates a Completed row: a row
    /// that was requeued in the meantime owns a newer fingerprint.
    /// </summary>
    public async Task<bool> UpdateFingerprintAsync(
        long id, long? size, DateTime? mtimeUtc, string? contentHash, CancellationToken ct = default)
    {
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE jobs SET size = $size, mtime_utc = $mtime, content_hash = $hash, updated_at = $now
                WHERE id = $id AND state = 'Completed';
                """;
            command.Parameters.AddWithValue("$size", (object?)size ?? DBNull.Value);
            command.Parameters.AddWithValue("$mtime", (object?)FormatTimestamp(mtimeUtc) ?? DBNull.Value);
            command.Parameters.AddWithValue("$hash", (object?)contentHash ?? DBNull.Value);
            command.Parameters.AddWithValue("$now", FormatTimestamp(Now()));
            command.Parameters.AddWithValue("$id", id);
            return await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false) > 0;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>
    /// Moves every InFlight row back to Pending, recording "service restart" on
    /// each (SCAN-06). Runs once at startup, before any scan claims work.
    /// </summary>
    public async Task<int> RecoverInFlightAsync(CancellationToken ct = default)
    {
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var connection = Open();
            using var transaction = connection.BeginTransaction(IsolationLevel.Serializable, deferred: false);
            var now = Now();

            var rows = new List<JobRecord>();
            using (var select = connection.CreateCommand())
            {
                select.Transaction = transaction;
                select.CommandText = $"SELECT {JobColumns} FROM jobs WHERE state = 'InFlight' ORDER BY id;";
                using var reader = await select.ExecuteReaderAsync(ct).ConfigureAwait(false);
                while (await reader.ReadAsync(ct).ConfigureAwait(false))
                    rows.Add(ReadJob(reader));
            }

            foreach (var job in rows)
            {
                JobStateMachine.EnsureTransition(job.State, JobState.Pending, job.Key);
                await UpdateStateAsync(connection, transaction, job.Id, JobState.Pending, now,
                    error: job.LastError, nextAttemptAt: null, ct).ConfigureAwait(false);
                await AppendEventAsync(connection, transaction, job.Id, now, JobState.InFlight, JobState.Pending,
                    "service restart", ct).ConfigureAwait(false);
            }

            transaction.Commit();
            return rows.Count;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>Drops pooled handles, so a test can delete its temporary directory.</summary>
    public void ClearPool()
    {
        if (!File.Exists(_databasePath)) return;
        using var connection = Open();
        SqliteConnection.ClearPool(connection);
    }

    // -------------------------------------------------------------- internals

    /// <summary>Returns the key when the candidate was enqueued, null when it was skipped.</summary>
    private async Task<string?> EnqueueOneAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        JobCandidate candidate,
        DateTime now,
        CancellationToken ct)
    {
        var existing = await SelectByKeyAsync(connection, transaction, candidate.Key, ct).ConfigureAwait(false);

        if (existing is null)
        {
            await InsertAsync(connection, transaction, candidate, now, ct).ConfigureAwait(false);
            return candidate.Key;
        }

        switch (existing.State)
        {
            case JobState.Pending or JobState.InFlight:
                // Already waiting or being worked on: enqueueing again would
                // duplicate work the queue already owns.
                return null;

            case JobState.Completed:
                if (existing.MatchesSizeAndTime(candidate.Size, candidate.MtimeUtc))
                    return null;

                if (existing.MatchesFingerprint(candidate.Size, candidate.MtimeUtc, candidate.ContentHash))
                {
                    await RefreshTimestampAsync(connection, transaction, existing.Id, candidate, now, ct)
                        .ConfigureAwait(false);
                    return null;
                }
                break;

            case JobState.TransientFailure:
            case JobState.Failed:
                // A corrupt file must not be retried forever: only a change on
                // disk earns a new attempt.
                if (existing.MatchesFingerprint(candidate.Size, candidate.MtimeUtc, candidate.ContentHash))
                    return null;
                break;
        }

        JobStateMachine.EnsureTransition(existing.State, JobState.Pending, existing.Key);
        await RequeueAsync(connection, transaction, existing, candidate, now, ct).ConfigureAwait(false);
        await AppendEventAsync(connection, transaction, existing.Id, now, existing.State, JobState.Pending,
            "changed since last attempt", ct).ConfigureAwait(false);
        return candidate.Key;
    }

    private static async Task InsertAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        JobCandidate candidate,
        DateTime now,
        CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO jobs
                (key, source, path, daminion_id, file_name, size, mtime_utc, content_hash,
                 state, attempts, last_error, enqueued_at, updated_at, next_attempt_at)
            VALUES
                ($key, $source, $path, $daminionId, $fileName, $size, $mtime, $hash,
                 'Pending', 0, NULL, $now, $now, NULL);
            """;
        command.Parameters.AddWithValue("$key", candidate.Key);
        command.Parameters.AddWithValue("$source", candidate.Source);
        command.Parameters.AddWithValue("$path", (object?)candidate.Path ?? DBNull.Value);
        command.Parameters.AddWithValue("$daminionId", (object?)candidate.DaminionId ?? DBNull.Value);
        command.Parameters.AddWithValue("$fileName", candidate.FileName);
        command.Parameters.AddWithValue("$size", (object?)candidate.Size ?? DBNull.Value);
        command.Parameters.AddWithValue("$mtime", (object?)FormatTimestamp(candidate.MtimeUtc) ?? DBNull.Value);
        command.Parameters.AddWithValue("$hash", (object?)candidate.ContentHash ?? DBNull.Value);
        command.Parameters.AddWithValue("$now", FormatTimestamp(now));
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

        using var idCommand = connection.CreateCommand();
        idCommand.Transaction = transaction;
        idCommand.CommandText = "SELECT last_insert_rowid();";
        var id = (long)(await idCommand.ExecuteScalarAsync(ct).ConfigureAwait(false) ?? 0L);
        await AppendEventAsync(connection, transaction, id, now, null, JobState.Pending, "detected", ct)
            .ConfigureAwait(false);
    }

    private static async Task RequeueAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        JobRecord existing,
        JobCandidate candidate,
        DateTime now,
        CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE jobs SET
                state = 'Pending',
                attempts = 0,
                last_error = NULL,
                next_attempt_at = NULL,
                file_name = $fileName,
                size = $size,
                mtime_utc = $mtime,
                content_hash = $hash,
                updated_at = $now
            WHERE id = $id;
            """;
        command.Parameters.AddWithValue("$fileName", candidate.FileName);
        command.Parameters.AddWithValue("$size", (object?)candidate.Size ?? DBNull.Value);
        command.Parameters.AddWithValue("$mtime", (object?)FormatTimestamp(candidate.MtimeUtc) ?? DBNull.Value);
        command.Parameters.AddWithValue("$hash", (object?)candidate.ContentHash ?? DBNull.Value);
        command.Parameters.AddWithValue("$now", FormatTimestamp(now));
        command.Parameters.AddWithValue("$id", existing.Id);
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Completed row, same bytes, moved timestamp: refresh the stored mtime so the
    /// next scan is a stat-only comparison again, and enqueue nothing.
    /// </summary>
    private static async Task RefreshTimestampAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long id,
        JobCandidate candidate,
        DateTime now,
        CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            "UPDATE jobs SET mtime_utc = $mtime, updated_at = $now WHERE id = $id;";
        command.Parameters.AddWithValue("$mtime", (object?)FormatTimestamp(candidate.MtimeUtc) ?? DBNull.Value);
        command.Parameters.AddWithValue("$now", FormatTimestamp(now));
        command.Parameters.AddWithValue("$id", id);
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private async Task<bool> MarkAsync(
        long id,
        JobState to,
        string? error,
        DateTime? nextAttemptAt,
        string? detail,
        CancellationToken ct)
    {
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var connection = Open();
            using var transaction = connection.BeginTransaction(IsolationLevel.Serializable, deferred: false);

            var existing = await SelectByIdAsync(connection, transaction, id, ct).ConfigureAwait(false);
            if (existing is null)
            {
                transaction.Commit();
                return false;
            }

            JobStateMachine.EnsureTransition(existing.State, to, existing.Key);
            var now = Now();
            await UpdateStateAsync(connection, transaction, id, to, now,
                Truncate(error), nextAttemptAt, ct).ConfigureAwait(false);
            await AppendEventAsync(connection, transaction, id, now, existing.State, to, detail ?? Truncate(error), ct)
                .ConfigureAwait(false);

            transaction.Commit();
            return true;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private static async Task UpdateStateAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long id,
        JobState state,
        DateTime now,
        string? error,
        DateTime? nextAttemptAt,
        CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE jobs SET
                state = $state,
                last_error = $error,
                next_attempt_at = $nextAttempt,
                updated_at = $now
            WHERE id = $id;
            """;
        command.Parameters.AddWithValue("$state", JobStates.ToStorage(state));
        command.Parameters.AddWithValue("$error", (object?)error ?? DBNull.Value);
        command.Parameters.AddWithValue("$nextAttempt", (object?)FormatTimestamp(nextAttemptAt) ?? DBNull.Value);
        command.Parameters.AddWithValue("$now", FormatTimestamp(now));
        command.Parameters.AddWithValue("$id", id);
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static async Task AppendEventAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        long jobId,
        DateTime at,
        JobState? from,
        JobState? to,
        string? detail,
        CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO job_events (job_id, at, from_state, to_state, detail)
            VALUES ($jobId, $at, $from, $to, $detail);
            """;
        command.Parameters.AddWithValue("$jobId", jobId);
        command.Parameters.AddWithValue("$at", FormatTimestamp(at));
        command.Parameters.AddWithValue("$from", from is null ? DBNull.Value : JobStates.ToStorage(from.Value));
        command.Parameters.AddWithValue("$to", to is null ? DBNull.Value : JobStates.ToStorage(to.Value));
        command.Parameters.AddWithValue("$detail", (object?)detail ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static async Task<JobRecord?> SelectByIdAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        long id,
        CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"SELECT {JobColumns} FROM jobs WHERE id = $id;";
        command.Parameters.AddWithValue("$id", id);
        using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await reader.ReadAsync(ct).ConfigureAwait(false) ? ReadJob(reader) : null;
    }

    private static async Task<JobRecord?> SelectByKeyAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string key,
        CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"SELECT {JobColumns} FROM jobs WHERE key = $key;";
        command.Parameters.AddWithValue("$key", key);
        using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await reader.ReadAsync(ct).ConfigureAwait(false) ? ReadJob(reader) : null;
    }

    private static JobRecord ReadJob(SqliteDataReader reader) => new()
    {
        Id = reader.GetInt64(0),
        Key = reader.GetString(1),
        Source = reader.GetString(2),
        Path = reader.IsDBNull(3) ? null : reader.GetString(3),
        DaminionId = reader.IsDBNull(4) ? null : reader.GetInt32(4),
        FileName = reader.GetString(5),
        Size = reader.IsDBNull(6) ? null : reader.GetInt64(6),
        MtimeUtc = reader.IsDBNull(7) ? null : ParseTimestamp(reader.GetString(7)),
        ContentHash = reader.IsDBNull(8) ? null : reader.GetString(8),
        State = JobStates.TryParse(reader.GetString(9), out var state)
            ? state
            : throw new InvalidOperationException($"Row {reader.GetInt64(0)} has an unknown state '{reader.GetString(9)}'."),
        Attempts = reader.GetInt32(10),
        LastError = reader.IsDBNull(11) ? null : reader.GetString(11),
        EnqueuedAt = ParseTimestamp(reader.GetString(12)) ?? default,
        UpdatedAt = ParseTimestamp(reader.GetString(13)) ?? default,
        NextAttemptAt = reader.IsDBNull(14) ? null : ParseTimestamp(reader.GetString(14)),
    };

    // ------------------------------------------------------------ connections

    private SqliteConnection Open()
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = _databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = true,
            DefaultTimeout = 5,
        };
        var connection = new SqliteConnection(builder.ToString());
        connection.Open();

        using var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA busy_timeout=5000;";
        pragma.ExecuteNonQuery();
        return connection;
    }

    // ---------------------------------------------------------------- helpers

    private DateTime Now() => _clock.GetUtcNow().UtcDateTime;

    private static string FormatTimestamp(DateTime value) =>
        value.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture);

    private static string? FormatTimestamp(DateTime? value) =>
        value is null ? null : FormatTimestamp(value.Value);

    private static DateTime? ParseTimestamp(string value) =>
        DateTime.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    private static string? Truncate(string? text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        return text.Length <= MaxErrorLength ? text : text[..MaxErrorLength];
    }
}
