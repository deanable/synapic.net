using Microsoft.Data.Sqlite;
using Synapic.Indexer.State;
using Xunit;

namespace Synapic.Indexer.Tests;

public sealed class IndexingQueueRepositoryTests : IDisposable
{
    private readonly string _directory;
    private readonly string _databasePath;
    private readonly TestClock _clock = new();
    private readonly IndexingQueueRepository _repository;

    private static readonly DateTime T1 = new(2026, 1, 1, 9, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime T2 = new(2026, 1, 2, 10, 30, 0, DateTimeKind.Utc);

    public IndexingQueueRepositoryTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "synapic-indexer-queue-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
        _databasePath = Path.Combine(_directory, "indexer.db");
        _repository = new IndexingQueueRepository(_databasePath, _clock);
        _repository.InitializeAsync().GetAwaiter().GetResult();
    }

    public void Dispose()
    {
        _repository.ClearPool();
        try { Directory.Delete(_directory, recursive: true); } catch (IOException) { }
    }

    private static JobCandidate Local(
        string name = "a.jpg",
        long size = 100,
        DateTime? mtime = null,
        string? hash = "hash-a") =>
        new(PathKeyFor(name), "local", "c:\\photos\\" + name, null, name, size, mtime ?? T1, hash);

    private static string PathKeyFor(string name) => "local|c:\\photos\\" + name;

    private async Task<JobRecord> EnqueueAndReadAsync(JobCandidate candidate)
    {
        await _repository.EnqueueAsync(new[] { candidate });
        return (await _repository.GetByKeyAsync(candidate.Key))!;
    }

    private async Task<JobRecord> CompleteAsync(JobCandidate candidate)
    {
        await _repository.EnqueueAsync(new[] { candidate });
        var claimed = await _repository.ClaimBatchAsync(1);
        Assert.Single(claimed);
        Assert.True(await _repository.MarkCompletedAsync(claimed[0].Id, "verified"));
        return (await _repository.GetByKeyAsync(candidate.Key))!;
    }

    // ------------------------------------------------------------- enqueue

    [Fact]
    public async Task First_enqueue_inserts_a_Pending_row_and_an_event()
    {
        var result = await _repository.EnqueueAsync(new[] { Local() });

        Assert.Equal(1, result.Enqueued);
        Assert.Equal(0, result.Skipped);
        var row = (await _repository.GetByKeyAsync(PathKeyFor("a.jpg")))!;
        Assert.Equal(JobState.Pending, row.State);
        Assert.Equal(0, row.Attempts);
        Assert.Equal("a.jpg", row.FileName);
        Assert.Equal(100, row.Size);
        Assert.Equal(T1, row.MtimeUtc);
        Assert.Equal("hash-a", row.ContentHash);
        Assert.Equal(T1, row.UpdatedAt);
        Assert.Equal(1, await CountEventsAsync(row.Id));
    }

    [Fact]
    public async Task Re_enqueueing_pending_work_is_a_no_op()
    {
        var first = await EnqueueAndReadAsync(Local());
        _clock.Advance(TimeSpan.FromMinutes(5));

        var second = await _repository.EnqueueAsync(new[] { Local(size: 200, hash: "different") });

        Assert.Equal(0, second.Enqueued);
        Assert.Equal(1, second.Skipped);
        var row = (await _repository.GetAsync(first.Id))!;
        Assert.Equal(first.UpdatedAt, row.UpdatedAt);
        Assert.Equal(JobState.Pending, row.State);
    }

    [Fact]
    public async Task Enqueueing_the_same_key_with_different_casing_maps_to_one_row()
    {
        await _repository.EnqueueAsync(new[]
        {
            new JobCandidate("local|C:\\Photos\\A.JPG", "local", "C:\\Photos\\A.JPG", null, "A.JPG", 100, T1, "h"),
        });
        var result = await _repository.EnqueueAsync(new[]
        {
            new JobCandidate("local|c:\\photos\\a.jpg", "local", "c:\\photos\\a.jpg", null, "a.jpg", 100, T1, "h"),
        });

        Assert.Equal(0, result.Enqueued);
        Assert.Equal(1, (await _repository.GetSummaryAsync()).Total);
    }

    [Fact]
    public async Task Completed_with_the_same_size_and_time_is_skipped_without_a_hash()
    {
        var candidate = Local();
        await CompleteAsync(candidate);

        var result = await _repository.EnqueueAsync(new[]
        {
            candidate with { ContentHash = null },
        });

        Assert.Equal(0, result.Enqueued);
        Assert.Equal(JobState.Completed, (await _repository.GetByKeyAsync(candidate.Key))!.State);
    }

    [Fact]
    public async Task Completed_with_a_moved_time_and_equal_hash_refreshes_the_time_without_enqueueing()
    {
        var candidate = Local();
        await CompleteAsync(candidate);
        _clock.Advance(TimeSpan.FromHours(1));

        var result = await _repository.EnqueueAsync(new[]
        {
            candidate with { MtimeUtc = T2 },
        });

        Assert.Equal(0, result.Enqueued);
        var row = (await _repository.GetByKeyAsync(candidate.Key))!;
        Assert.Equal(JobState.Completed, row.State);
        Assert.Equal(T2, row.MtimeUtc);
        Assert.Equal(_clock.GetUtcNow().UtcDateTime, row.UpdatedAt);
    }

    [Fact]
    public async Task Completed_with_changed_bytes_is_Pending_again_with_zero_attempts()
    {
        var candidate = Local();
        var completed = await CompleteAsync(candidate);

        var result = await _repository.EnqueueAsync(new[]
        {
            candidate with { Size = 200, MtimeUtc = T2, ContentHash = "hash-b" },
        });

        Assert.Equal(1, result.Enqueued);
        var row = (await _repository.GetByKeyAsync(candidate.Key))!;
        Assert.Equal(JobState.Pending, row.State);
        Assert.Equal(0, row.Attempts);
        Assert.Equal("hash-b", row.ContentHash);
        Assert.Equal(completed.EnqueuedAt, row.EnqueuedAt);
    }

    [Fact]
    public async Task Failed_with_the_same_fingerprint_is_never_retried_and_a_change_brings_it_back()
    {
        var candidate = Local();
        await _repository.EnqueueAsync(new[] { candidate });
        var claimed = await _repository.ClaimBatchAsync(1);
        Assert.True(await _repository.MarkFailedAsync(claimed[0].Id, "cannot decode image"));

        var unchanged = await _repository.EnqueueAsync(new[] { candidate });
        Assert.Equal(0, unchanged.Enqueued);
        Assert.Equal(JobState.Failed, (await _repository.GetByKeyAsync(candidate.Key))!.State);

        var changed = await _repository.EnqueueAsync(new[] { candidate with { Size = 101, ContentHash = "hash-c" } });
        Assert.Equal(1, changed.Enqueued);
        Assert.Equal(JobState.Pending, (await _repository.GetByKeyAsync(candidate.Key))!.State);
    }

    [Fact]
    public async Task Transient_failure_is_not_re_enqueued_until_the_file_changes()
    {
        var candidate = Local();
        await _repository.EnqueueAsync(new[] { candidate });
        var claimed = await _repository.ClaimBatchAsync(1);
        Assert.True(await _repository.MarkTransientAsync(
            claimed[0].Id, "file is locked", _clock.GetUtcNow().UtcDateTime.AddSeconds(60)));

        Assert.Equal(0, (await _repository.EnqueueAsync(new[] { candidate })).Enqueued);
        Assert.Equal(1, (await _repository.EnqueueAsync(new[]
        {
            candidate with { Size = 300, ContentHash = "hash-d" },
        })).Enqueued);
    }

    // -------------------------------------------------------------- claims

    [Fact]
    public async Task Claiming_takes_the_oldest_due_rows_once()
    {
        await _repository.EnqueueAsync(new[]
        {
            Local("a.jpg"), Local("b.jpg"), Local("c.jpg"),
        });

        var first = await _repository.ClaimBatchAsync(2);
        var second = await _repository.ClaimBatchAsync(2);

        Assert.Equal(new[] { "a.jpg", "b.jpg" }, first.Select(j => j.FileName));
        Assert.Equal(new[] { "c.jpg" }, second.Select(j => j.FileName));
        Assert.All(first, job => Assert.Equal(JobState.InFlight, job.State));
        Assert.All(first, job => Assert.Equal(1, job.Attempts));
        Assert.Empty(await _repository.ClaimBatchAsync(2));
    }

    [Fact]
    public async Task A_row_with_a_future_retry_time_is_not_claimed_yet()
    {
        await _repository.EnqueueAsync(new[] { Local() });
        var claimed = await _repository.ClaimBatchAsync(1);
        var retryAt = _clock.GetUtcNow().UtcDateTime.AddSeconds(60);
        Assert.True(await _repository.MarkTransientAsync(claimed[0].Id, "locked", retryAt));

        Assert.Empty(await _repository.ClaimBatchAsync(1));

        _clock.Advance(TimeSpan.FromSeconds(61));
        Assert.Single(await _repository.ClaimBatchAsync(1));
    }

    [Fact]
    public async Task Completing_a_row_records_the_transition()
    {
        var candidate = Local();
        var row = await CompleteAsync(candidate);

        Assert.Equal(JobState.Completed, row.State);
        using var connection = IndexerQueueReader.TryOpenReadOnly(_databasePath)!;
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT to_state, detail FROM job_events WHERE job_id = $id ORDER BY id;";
        command.Parameters.AddWithValue("$id", row.Id);
        using var reader = command.ExecuteReader();
        var transitions = new List<string>();
        while (reader.Read()) transitions.Add($"{reader.GetString(0)}:{reader.GetString(1)}");

        Assert.Equal(new[] { "Pending:detected", "InFlight:claimed", "Completed:verified" }, transitions);
    }

    [Fact]
    public async Task Recovery_returns_every_InFlight_row_to_Pending_and_says_why()
    {
        await _repository.EnqueueAsync(new[] { Local("a.jpg"), Local("b.jpg"), Local("c.jpg") });
        await _repository.ClaimBatchAsync(2);

        var recovered = await _repository.RecoverInFlightAsync();

        Assert.Equal(2, recovered);
        var summary = await _repository.GetSummaryAsync();
        Assert.Equal(3, summary.Pending);
        Assert.Equal(0, summary.InFlight);
        Assert.Equal(0, await _repository.RecoverInFlightAsync());

        using var connection = IndexerQueueReader.TryOpenReadOnly(_databasePath)!;
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT COUNT(*) FROM job_events WHERE from_state = 'InFlight' AND to_state = 'Pending' " +
            "AND detail = 'service restart';";
        Assert.Equal(2L, (long)command.ExecuteScalar()!);
    }

    // ---------------------------------------------------------- persistence

    [Fact]
    public async Task A_second_repository_over_the_same_file_sees_the_same_rows()
    {
        await _repository.EnqueueAsync(new[] { Local("a.jpg"), Local("b.jpg") });
        await _repository.ClaimBatchAsync(1);

        var reopened = new IndexingQueueRepository(_databasePath, _clock);
        await reopened.InitializeAsync();
        var summary = await reopened.GetSummaryAsync();

        Assert.Equal(1, summary.Pending);
        Assert.Equal(1, summary.InFlight);

        var recovered = await reopened.RecoverInFlightAsync();
        Assert.Equal(1, recovered);
        Assert.Equal(2, (await reopened.GetSummaryAsync()).Pending);
        reopened.ClearPool();
    }

    [Fact]
    public async Task Initialize_is_idempotent_and_keeps_rows()
    {
        await _repository.EnqueueAsync(new[] { Local() });
        await _repository.InitializeAsync();
        await _repository.InitializeAsync();

        Assert.Equal(1, (await _repository.GetSummaryAsync()).Pending);
    }

    [Fact]
    public async Task Activity_is_true_only_while_open_work_exists()
    {
        Assert.False((await _repository.GetActivityAsync()).HasPendingWork);

        var candidate = Local();
        await EnqueueAndReadAsync(candidate);
        var activity = await _repository.GetActivityAsync();
        Assert.True(activity.HasPendingWork);
        Assert.Equal(_clock.GetUtcNow().UtcDateTime, activity.LastWorkAt);

        _clock.Advance(TimeSpan.FromMinutes(3));
        await CompleteAsync(candidate);
        var idle = await _repository.GetActivityAsync();
        Assert.False(idle.HasPendingWork);
        Assert.Null(idle.LastWorkAt);
    }

    [Fact]
    public async Task Summary_counts_each_state_and_lists_at_most_five_failures()
    {
        for (var i = 0; i < 7; i++)
        {
            await _repository.EnqueueAsync(new[] { Local($"f{i}.jpg", size: 10 + i, hash: $"hash-{i}") });
            var claimed = await _repository.ClaimBatchAsync(1);
            Assert.True(await _repository.MarkFailedAsync(claimed[0].Id, $"error {i}"));
            _clock.Advance(TimeSpan.FromSeconds(1));
        }

        await _repository.EnqueueAsync(new[] { Local("pending.jpg", size: 999, hash: "hash-p") });

        var summary = await _repository.GetSummaryAsync();
        Assert.Equal(7, summary.Failed);
        Assert.Equal(1, summary.Pending);
        Assert.Equal(8, summary.Total);
        Assert.Equal(5, summary.RecentFailures.Count);
        Assert.Equal("error 6", summary.RecentFailures[0].LastError);
    }

    // ---------------------------------------------------------- concurrency

    [Fact]
    public async Task Parallel_enqueues_of_one_key_produce_one_row()
    {
        var candidate = Local();
        var results = await Task.WhenAll(Enumerable.Range(0, 8)
            .Select(_ => Task.Run(() => _repository.EnqueueAsync(new[] { candidate }))));

        Assert.Equal(1, results.Sum(r => r.Enqueued));
        Assert.Equal(1, (await _repository.GetSummaryAsync()).Total);
    }

    [Fact]
    public void Read_only_open_of_a_missing_database_returns_null()
    {
        Assert.Null(IndexerQueueReader.TryOpenReadOnly(Path.Combine(_directory, "nope.db")));
    }

    private async Task<long> CountEventsAsync(long jobId)
    {
        using var connection = IndexerQueueReader.TryOpenReadOnly(_databasePath)!;
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM job_events WHERE job_id = $id;";
        command.Parameters.AddWithValue("$id", jobId);
        return (long)(await command.ExecuteScalarAsync())!;
    }
}
