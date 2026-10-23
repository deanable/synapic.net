using Synapic.Indexer.Configuration;
using Synapic.Indexer.Detection;
using Synapic.Indexer.State;
using Xunit;

namespace Synapic.Indexer.Tests;

public sealed class LocalFolderScanSourceTests : IDisposable
{
    private readonly string _root;
    private readonly string _sub;
    private readonly string _databasePath;
    private readonly TestClock _clock = new();
    private readonly IndexingQueueRepository _repository;
    private readonly CountingHasher _hasher = new();
    private readonly IndexerConfig _config = IndexerConfig.CreateDefault();

    public LocalFolderScanSourceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "synapic-indexer-scan-" + Guid.NewGuid().ToString("N"));
        _sub = Path.Combine(_root, "sub");
        var stateDir = Path.Combine(_root, "_state");
        Directory.CreateDirectory(_sub);
        Directory.CreateDirectory(stateDir);

        _databasePath = Path.Combine(stateDir, "indexer.db");
        _repository = new IndexingQueueRepository(_databasePath, _clock);
        _repository.InitializeAsync().GetAwaiter().GetResult();

        _config.Schedule.WatchPaths = new List<string> { _root };
        _config.Schedule.WatchRecursive = true;

        WriteImage("a.jpg", 10);
        WriteImage("B.JPEG", 11);
        WriteImage("c.png", 12);
        WriteImage("d.tif", 13);
        File.WriteAllText(Path.Combine(_root, "e.txt"), "not an image");
        WriteImage(Path.Combine("sub", "f.tiff"), 14);
        File.WriteAllBytes(Path.Combine(_root, "empty.jpg"), Array.Empty<byte>());
    }

    public void Dispose()
    {
        _repository.ClearPool();
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private void WriteImage(string relativePath, int bytes)
    {
        var path = Path.Combine(_root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, Enumerable.Range(0, bytes).Select(i => (byte)(i % 251)).ToArray());
    }

    private LocalFolderScanSource Source()
    {
        var intake = new WorkIntake(_repository);
        return new LocalFolderScanSource(_config, _repository, _hasher, intake);
    }

    private sealed class CountingHasher : IFileHasher
    {
        private readonly Sha256FileHasher _inner = new();
        public int Calls { get; private set; }

        public async Task<string?> TryHashAsync(string path, CancellationToken ct)
        {
            Calls++;
            return await _inner.TryHashAsync(path, ct);
        }
    }

    [Fact]
    public async Task First_scan_enqueues_the_five_supported_non_empty_files()
    {
        var result = await Source().ScanAsync(CancellationToken.None);

        Assert.Equal(5, result.Found);
        Assert.Equal(5, result.Enqueued);
        Assert.Equal(0, result.Skipped);
        Assert.Equal(0, result.Errors);
        Assert.Equal(5, _hasher.Calls);

        var names = (await SummaryKeysAsync()).OrderBy(k => k, StringComparer.OrdinalIgnoreCase).ToList();
        Assert.Equal(5, names.Count);
        Assert.Contains(names, k => k.EndsWith("a.jpg", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(names, k => k.EndsWith("f.tiff", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(names, k => k.EndsWith("e.txt", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(names, k => k.EndsWith("empty.jpg", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task A_second_scan_of_unchanged_files_enqueues_nothing_and_reads_no_content()
    {
        var source = Source();
        await source.ScanAsync(CancellationToken.None);
        var afterFirst = _hasher.Calls;

        var second = await source.ScanAsync(CancellationToken.None);

        Assert.Equal(0, second.Enqueued);
        Assert.Equal(0, second.Errors);
        Assert.Equal(afterFirst, _hasher.Calls);
    }

    [Fact]
    public async Task Touching_only_the_time_costs_one_hash_and_enqueues_nothing()
    {
        var source = Source();
        await source.ScanAsync(CancellationToken.None);
        await CompleteAllAsync();

        var path = Path.Combine(_root, "a.jpg");
        var before = _hasher.Calls;
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddHours(1));

        var result = await source.ScanAsync(CancellationToken.None);

        Assert.Equal(0, result.Enqueued);
        Assert.Equal(before + 1, _hasher.Calls);
        var row = (await _repository.GetByKeyAsync(PathKey.ForLocal(path)))!;
        Assert.Equal(JobState.Completed, row.State);
        Assert.Equal(File.GetLastWriteTimeUtc(path), row.MtimeUtc);
    }

    [Fact]
    public async Task One_changed_byte_puts_the_file_back_in_the_queue()
    {
        var source = Source();
        await source.ScanAsync(CancellationToken.None);
        await CompleteAllAsync();

        var path = Path.Combine(_root, "a.jpg");
        File.AppendAllText(path, "x");
        var result = await source.ScanAsync(CancellationToken.None);

        Assert.Equal(1, result.Enqueued);
        Assert.Equal(JobState.Pending, (await _repository.GetByKeyAsync(PathKey.ForLocal(path)))!.State);
    }

    [Fact]
    public async Task A_completed_file_that_only_moved_in_time_stays_completed_and_refreshes_its_time()
    {
        var source = Source();
        await source.ScanAsync(CancellationToken.None);
        await CompleteAllAsync();

        var path = Path.Combine(_root, "a.jpg");
        _clock.Advance(TimeSpan.FromMinutes(10));
        File.SetLastWriteTimeUtc(path, File.GetLastWriteTimeUtc(path).AddMinutes(5));

        var result = await source.ScanAsync(CancellationToken.None);

        Assert.Equal(0, result.Enqueued);
        var row = (await _repository.GetByKeyAsync(PathKey.ForLocal(path)))!;
        Assert.Equal(JobState.Completed, row.State);
        Assert.Equal(File.GetLastWriteTimeUtc(path), row.MtimeUtc);
    }

    [Fact]
    public async Task Nested_roots_produce_one_candidate_per_file()
    {
        _config.Schedule.WatchPaths = new List<string> { _root, _sub };

        var result = await Source().ScanAsync(CancellationToken.None);

        Assert.Equal(5, result.Enqueued);
        Assert.Equal(5, (await _repository.GetSummaryAsync()).Total);
    }

    [Fact]
    public async Task A_missing_root_is_counted_and_the_other_root_still_scans()
    {
        var other = Path.Combine(_root, "other");
        Directory.CreateDirectory(other);
        File.WriteAllBytes(Path.Combine(other, "g.jpg"), new byte[] { 1, 2, 3 });
        _config.Schedule.WatchPaths = new List<string>
        {
            Path.Combine(_root, "does-not-exist"),
            other,
        };

        var result = await Source().ScanAsync(CancellationToken.None);

        Assert.Equal(1, result.Enqueued);
        Assert.Equal(1, result.Errors);
        Assert.True((await _repository.GetSummaryAsync()).Pending >= 1);
    }

    [Fact]
    public async Task Cancellation_stops_the_scan_without_enqueueing_a_partial_batch()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => Source().ScanAsync(cts.Token));
        Assert.Equal(0, (await _repository.GetSummaryAsync()).Total);
    }

    [Fact]
    public async Task A_file_locked_by_a_writer_is_skipped_and_the_scan_finishes()
    {
        var path = Path.Combine(_root, "a.jpg");
        using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var result = await Source().ScanAsync(CancellationToken.None);

            Assert.Equal(4, result.Enqueued);
            Assert.Equal(1, result.Errors);
            Assert.Null(await _repository.GetByKeyAsync(PathKey.ForLocal(path)));
        }
    }

    [Fact]
    public async Task Non_recursive_scan_ignores_subfolders()
    {
        _config.Schedule.WatchRecursive = false;

        var result = await Source().ScanAsync(CancellationToken.None);

        Assert.Equal(4, result.Enqueued);
    }

    private async Task CompleteAllAsync()
    {
        while (true)
        {
            var claimed = await _repository.ClaimBatchAsync(10);
            if (claimed.Count == 0) break;
            foreach (var job in claimed)
                Assert.True(await _repository.MarkCompletedAsync(job.Id));
        }
    }

    private async Task<List<string>> SummaryKeysAsync()
    {
        var keys = new List<string>();
        using var connection = IndexingQueueRepository.TryOpenReadOnly(_databasePath)!;
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT key FROM jobs;";
        using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync()) keys.Add(reader.GetString(0));
        return keys;
    }
}
