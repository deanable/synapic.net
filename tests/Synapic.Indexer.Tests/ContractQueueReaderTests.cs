using Synapic.Indexer.Contract;
using Synapic.Indexer.State;
using Xunit;

namespace Synapic.Indexer.Tests;

/// <summary>
/// The shared queue reader (Synapic.Indexer.Contract, what the desktop app's
/// Service tab reads) against a database the service's own schema and
/// repository wrote. If the writer's schema ever drifts from the contract's
/// queries, this is where it becomes a test failure rather than a GUI that
/// quietly reads nothing.
/// </summary>
public sealed class ContractQueueReaderTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), "synapic-contract-reader-" + Guid.NewGuid().ToString("N"));
    private string DatabasePath => Path.Combine(_directory, "indexer.db");

    public ContractQueueReaderTests() => Directory.CreateDirectory(_directory);
    public void Dispose()
    {
        var repository = new IndexingQueueRepository(DatabasePath);
        repository.ClearPool();
        try { Directory.Delete(_directory, recursive: true); } catch (IOException) { }
    }

    private async Task<IndexingQueueRepository> SeededAsync()
    {
        var repository = new IndexingQueueRepository(DatabasePath);
        await repository.InitializeAsync();
        await repository.EnqueueAsync(new[]
        {
            new JobCandidate("local|C:\\P\\one.jpg", "local", @"C:\P\one.jpg", null, "one.jpg", 10, null, null),
            new JobCandidate("local|C:\\P\\two.jpg", "local", @"C:\P\two.jpg", null, "two.jpg", 20, null, null),
        });
        var claimed = await repository.ClaimBatchAsync(max: 1);
        await repository.MarkCompletedAsync(claimed[0].Id);
        return repository;
    }

    [Fact]
    public async Task The_contract_reads_the_counts_and_failures_the_service_wrote()
    {
        await SeededAsync();

        using var connection = IndexerQueueReader.TryOpenReadOnly(DatabasePath);
        Assert.NotNull(connection);
        Assert.True(IndexerQueueReader.IsInitialized(connection!));

        var summary = await IndexerQueueReader.ReadSummaryAsync(connection!);
        Assert.Equal(1, summary.Pending);
        Assert.Equal(0, summary.InFlight);
        Assert.Equal(1, summary.Completed);

        await Assert.ThrowsAsync<Microsoft.Data.Sqlite.SqliteException>(
            () => WriteThroughTheReaderAsync(connection!));
    }

    [Fact]
    public async Task The_contract_reads_the_audit_events_the_service_wrote()
    {
        await SeededAsync();

        using var connection = IndexerQueueReader.TryOpenReadOnly(DatabasePath);
        var events = await IndexerQueueReader.ReadRecentEventsAsync(connection!, limit: 20);

        Assert.True(events.Count >= 3, $"expected detected/claimed/completed events, got {events.Count}");
        // Newest first: the completion is the newest row.
        Assert.Contains("Completed", events[0].ToState);
        Assert.Contains("one.jpg", events.Select(e => e.FileName));
        Assert.Contains("detected", events.Select(e => e.Detail));
    }

    [Fact]
    public void A_database_the_service_never_created_surfaces_its_error_on_query()
    {
        File.WriteAllText(DatabasePath, "not a database");
        // SQLite defers the header check to the first query, so opening can
        // succeed: the refusal must come from reading, and the caller's
        // documented branch (the monitor's "could not be read" note) catches it.
        var connection = IndexerQueueReader.TryOpenReadOnly(DatabasePath);
        if (connection is null) return; // refused at open is equally correct
        using (connection)
        {
            Assert.Throws<Microsoft.Data.Sqlite.SqliteException>(
                () => IndexerQueueReader.IsInitialized(connection));
        }
    }

    /// <summary>The read-only connection must refuse a write through itself.</summary>
    private static async Task WriteThroughTheReaderAsync(Microsoft.Data.Sqlite.SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO jobs (key, source, file_name, state, enqueued_at, updated_at) " +
                              "VALUES ('x', 'local', 'x', 'Pending', 'now', 'now');";
        await command.ExecuteNonQueryAsync();
    }
}
