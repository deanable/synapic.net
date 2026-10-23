using Synapic.Indexer.Commands;
using Synapic.Indexer.Hosting;
using Synapic.Indexer.State;
using Xunit;

namespace Synapic.Indexer.Tests;

public sealed class StatusCommandTests : IDisposable
{
    private readonly string _root;
    private readonly IndexerPaths _paths;
    private readonly TestClock _clock = new();

    public StatusCommandTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "synapic-indexer-status-" + Guid.NewGuid().ToString("N"));
        _paths = new IndexerPaths(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private static JobCandidate Candidate(string name) => new(
        "local|c:\\photos\\" + name, "local", "c:\\photos\\" + name, null, name, 10,
        new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), "hash-" + name);

    private async Task<IndexingQueueRepository> SeedAsync()
    {
        Directory.CreateDirectory(_paths.StateDirectory);
        var repository = new IndexingQueueRepository(_paths.QueueFilePath, _clock);
        await repository.InitializeAsync();

        await repository.EnqueueAsync(new[]
        {
            Candidate("a.jpg"), Candidate("b.jpg"), Candidate("c.jpg"),
            Candidate("d.jpg"), Candidate("e.jpg"),
        });

        var claimed = await repository.ClaimBatchAsync(4);
        await repository.MarkCompletedAsync(claimed[0].Id);
        await repository.MarkFailedAsync(claimed[1].Id, "decode failed");
        await repository.MarkTransientAsync(
            claimed[2].Id, "file locked", _clock.GetUtcNow().UtcDateTime.AddSeconds(60));
        // claimed[3] stays InFlight; e.jpg stays Pending.
        repository.ClearPool();
        return repository;
    }

    [Fact]
    public void A_missing_database_prints_no_queue_yet_and_creates_nothing()
    {
        var output = new StringWriter();
        var error = new StringWriter();

        var exit = StatusCommand.Run(_paths, output, error);

        Assert.Equal(0, exit);
        Assert.Equal("no queue yet", output.ToString().Trim());
        Assert.Equal("", error.ToString());
        Assert.False(Directory.Exists(_root));
    }

    [Fact]
    public async Task Every_state_and_the_newest_failures_are_printed()
    {
        var repository = await SeedAsync();
        var before = File.GetLastWriteTimeUtc(_paths.QueueFilePath);
        var directoryEntries = Directory.GetFileSystemEntries(_paths.StateDirectory).OrderBy(p => p).ToArray();

        var output = new StringWriter();
        var exit = StatusCommand.Run(_paths, output, new StringWriter());
        var text = output.ToString();

        Assert.Equal(0, exit);
        Assert.Contains($"queue: {_paths.QueueFilePath}", text);
        Assert.Contains("Pending 1", text);
        Assert.Contains("InFlight 1", text);
        Assert.Contains("Completed 1", text);
        Assert.Contains("TransientFailure 1", text);
        Assert.Contains("Failed 1", text);
        Assert.Contains("Recent failures:", text);
        Assert.Contains("decode failed", text);
        Assert.Contains("file locked", text);
        Assert.Contains("local|c:\\photos\\b.jpg", text);
        Assert.Contains("local|c:\\photos\\c.jpg", text);

        Assert.Equal(before, File.GetLastWriteTimeUtc(_paths.QueueFilePath));
        Assert.Equal(directoryEntries, Directory.GetFileSystemEntries(_paths.StateDirectory).OrderBy(p => p).ToArray());
        repository.ClearPool();
    }

    [Fact]
    public async Task An_empty_queue_prints_zeroes_and_none()
    {
        Directory.CreateDirectory(_paths.StateDirectory);
        var repository = new IndexingQueueRepository(_paths.QueueFilePath);
        await repository.InitializeAsync();
        repository.ClearPool();

        var output = new StringWriter();
        var exit = StatusCommand.Run(_paths, output, new StringWriter());
        var text = output.ToString();

        Assert.Equal(0, exit);
        Assert.Contains("Pending 0", text);
        Assert.Contains("Completed 0", text);
        Assert.Contains("none", text);
    }

    [Fact]
    public async Task The_failed_view_lists_more_than_the_summary_limit_with_attempts()
    {
        var repository = await SeedAsync();
        var candidates = Enumerable.Range(0, 8)
            .Select(i => Candidate($"extra-{i}.jpg"))
            .ToArray();
        await repository.EnqueueAsync(candidates);
        var claimed = await repository.ClaimBatchAsync(20);
        foreach (var job in claimed)
            Assert.True(await repository.MarkFailedAsync(job.Id, "decode failed"));
        repository.ClearPool();

        var output = new StringWriter();
        var exit = StatusCommand.Run(_paths, output, new StringWriter(), failedOnly: true);
        var text = output.ToString();

        Assert.Equal(0, exit);
        Assert.Contains($"queue: {_paths.QueueFilePath}", text);
        // The summary view stops at five; the failure view is where an operator
        // gets the whole (bounded) list, with the attempt count that says whether
        // a retry is still plausible.
        Assert.Contains("Failures (newest first", text);
        Assert.Equal(11, text.Split('\n').Count(line => line.Contains("attempts=")));
        Assert.True(text.Contains("extra-7.jpg"), text);
        Assert.DoesNotContain("Pending 1", text);
    }

    [Fact]
    public async Task The_failed_view_says_none_when_nothing_failed()
    {
        Directory.CreateDirectory(_paths.StateDirectory);
        var repository = new IndexingQueueRepository(_paths.QueueFilePath);
        await repository.InitializeAsync();
        repository.ClearPool();

        var output = new StringWriter();
        StatusCommand.Run(_paths, output, new StringWriter(), failedOnly: true);

        Assert.Contains("Failures: none", output.ToString());
    }

    [Fact]
    public async Task Long_multiline_errors_are_previewed_on_one_line()
    {
        var repository = await SeedAsync();
        await repository.EnqueueAsync(new[] { Candidate("f.jpg") });
        var claimed = await repository.ClaimBatchAsync(1);
        var longError = "line one\nline two " + new string('x', 300);
        Assert.True(await repository.MarkFailedAsync(claimed[0].Id, longError));
        repository.ClearPool();

        var output = new StringWriter();
        StatusCommand.Run(_paths, output, new StringWriter());
        var lines = output.ToString().Split('\n').Select(line => line.TrimEnd('\r')).ToList();
        var failureLines = lines.SkipWhile(line => line != "Recent failures:").Skip(1).ToList();

        Assert.Contains(failureLines, line => line.Contains("line one line two") && line.EndsWith('…'));
    }
}
