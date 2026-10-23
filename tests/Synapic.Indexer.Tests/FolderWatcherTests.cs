using Synapic.Indexer.Configuration;
using Synapic.Indexer.Detection;
using Synapic.Indexer.State;
using Xunit;
using Xunit.Sdk;

namespace Synapic.Indexer.Tests;

public sealed class FolderWatcherTests
{
    private static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(250);

    private sealed class Harness : IAsyncDisposable
    {
        public required string Root { get; init; }
        public required IndexingQueueRepository Repository { get; init; }
        public required FolderWatcher Watcher { get; init; }
        public required TestClock Clock { get; init; }

        public static async Task<Harness> StartAsync(
            bool recursive = true,
            Func<CancellationToken, Task>? onOverflow = null)
        {
            var root = Path.Combine(Path.GetTempPath(), "synapic-indexer-watch-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var stateDirectory = Path.Combine(root, "_state");
            Directory.CreateDirectory(stateDirectory);

            var clock = new TestClock();
            var repository = new IndexingQueueRepository(Path.Combine(stateDirectory, "indexer.db"), clock);
            await repository.InitializeAsync();

            var config = IndexerConfig.CreateDefault();
            config.Schedule.Mode = "IntervalAndWatch";
            config.Schedule.WatchPaths = new List<string> { root };
            config.Schedule.WatchRecursive = recursive;

            var watcher = new FolderWatcher(
                config,
                new WorkIntake(repository),
                new FileStabilityProbe(),
                new Sha256FileHasher(),
                repository,
                clock,
                Debounce,
                onOverflow);

            await watcher.StartAsync(CancellationToken.None);

            // FileSystemWatcher opens its directory handle on its own thread, so
            // a file created in the same millisecond as EnableRaisingEvents can
            // be missed. Give the handle a moment (well under the 500 ms flush
            // interval) before the test starts writing.
            await Task.Delay(150);

            return new Harness { Root = root, Repository = repository, Watcher = watcher, Clock = clock };
        }

        public async ValueTask DisposeAsync()
        {
            await Watcher.StopAsync(CancellationToken.None);
            Repository.ClearPool();
            try { Directory.Delete(Root, recursive: true); } catch (IOException) { }
        }
    }

    private static async Task WaitForBufferAsync(FolderWatcher watcher, int atLeast = 1)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            if (watcher.BufferedPaths >= atLeast) return;
            await Task.Delay(25);
        }

        throw new XunitException(
            $"The watcher buffer stayed at {watcher.BufferedPaths} entries; expected at least {atLeast}.");
    }

    [Fact]
    public async Task A_file_moved_into_the_folder_is_enqueued_once_after_the_debounce()
    {
        await using var harness = await Harness.StartAsync();

        var incoming = Path.Combine(harness.Root, "incoming.tmp");
        File.WriteAllBytes(incoming, new byte[] { 1, 2, 3, 4 });
        File.Move(incoming, Path.Combine(harness.Root, "photo.jpg"));

        await WaitForBufferAsync(harness.Watcher);
        harness.Clock.Advance(Debounce + TimeSpan.FromMilliseconds(50));
        await harness.Watcher.FlushAsync(CancellationToken.None);

        var summary = await harness.Repository.GetSummaryAsync();
        Assert.Equal(1, summary.Pending);
        var row = (await harness.Repository.GetByKeyAsync(
            PathKey.ForLocal(Path.Combine(harness.Root, "photo.jpg"))))!;
        Assert.Equal("photo.jpg", row.FileName);
        Assert.Equal("local", row.Source);
    }

    [Fact]
    public async Task A_file_still_held_by_a_writer_waits_for_the_probe()
    {
        await using var harness = await Harness.StartAsync();
        var path = Path.Combine(harness.Root, "slow.jpg");

        using (var writer = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read))
        {
            writer.Write(new byte[] { 1, 2, 3 });
            writer.Flush();

            await WaitForBufferAsync(harness.Watcher);
            harness.Clock.Advance(Debounce + TimeSpan.FromMilliseconds(50));
            await harness.Watcher.FlushAsync(CancellationToken.None);

            Assert.Equal(0, (await harness.Repository.GetSummaryAsync()).Total);
            Assert.Equal(1, harness.Watcher.BufferedPaths);
        }

        harness.Clock.Advance(TimeSpan.FromSeconds(3));
        await harness.Watcher.FlushAsync(CancellationToken.None);

        Assert.Equal(1, (await harness.Repository.GetSummaryAsync()).Pending);
    }

    [Fact]
    public async Task Only_supported_extensions_reach_the_queue()
    {
        await using var harness = await Harness.StartAsync();

        File.WriteAllText(Path.Combine(harness.Root, "notes.txt"), "not an image");
        File.WriteAllBytes(Path.Combine(harness.Root, "photo.PNG"), new byte[] { 5, 6, 7 });

        await WaitForBufferAsync(harness.Watcher);
        harness.Clock.Advance(Debounce + TimeSpan.FromMilliseconds(50));
        await harness.Watcher.FlushAsync(CancellationToken.None);

        var summary = await harness.Repository.GetSummaryAsync();
        Assert.Equal(1, summary.Total);
        Assert.NotNull(await harness.Repository.GetByKeyAsync(
            PathKey.ForLocal(Path.Combine(harness.Root, "photo.PNG"))));
    }

    [Fact]
    public async Task A_recursive_watcher_sees_subfolders()
    {
        await using var harness = await Harness.StartAsync(recursive: true);
        var sub = Path.Combine(harness.Root, "sub");
        Directory.CreateDirectory(sub);

        File.WriteAllBytes(Path.Combine(sub, "deep.jpg"), new byte[] { 9, 9, 9 });

        await WaitForBufferAsync(harness.Watcher);
        harness.Clock.Advance(Debounce + TimeSpan.FromMilliseconds(50));
        await harness.Watcher.FlushAsync(CancellationToken.None);

        var row = (await harness.Repository.GetByKeyAsync(
            PathKey.ForLocal(Path.Combine(sub, "deep.jpg"))))!;
        Assert.Equal(string.Join('\\', new[] { "sub", "deep.jpg" }),
            string.Join('\\', row.Path!.Split('\\')[^2..]));
    }

    [Fact]
    public async Task A_non_recursive_watcher_ignores_subfolders_but_still_sees_the_root()
    {
        await using var harness = await Harness.StartAsync(recursive: false);
        var sub = Path.Combine(harness.Root, "sub");
        Directory.CreateDirectory(sub);

        File.WriteAllBytes(Path.Combine(sub, "deep.jpg"), new byte[] { 1, 1, 1 });
        await Task.Delay(750);
        Assert.Equal(0, harness.Watcher.BufferedPaths);

        File.WriteAllBytes(Path.Combine(harness.Root, "top.jpg"), new byte[] { 2, 2, 2 });
        await WaitForBufferAsync(harness.Watcher);
        harness.Clock.Advance(Debounce + TimeSpan.FromMilliseconds(50));
        await harness.Watcher.FlushAsync(CancellationToken.None);

        var summary = await harness.Repository.GetSummaryAsync();
        Assert.Equal(1, summary.Total);
        Assert.Null(await harness.Repository.GetByKeyAsync(
            PathKey.ForLocal(Path.Combine(sub, "deep.jpg"))));
    }

    [Fact]
    public async Task A_buffer_overflow_requests_a_full_scan()
    {
        var requested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var harness = await Harness.StartAsync(onOverflow: _ =>
        {
            requested.TrySetResult();
            return Task.CompletedTask;
        });

        harness.Watcher.NotifyBufferOverflow(new IOException("event buffer overflow"));

        await requested.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Stopping_the_watcher_releases_every_root()
    {
        var harness = await Harness.StartAsync();
        Assert.Single(harness.Watcher.Roots);

        await harness.Watcher.StopAsync(CancellationToken.None);

        Assert.Empty(harness.Watcher.Roots);
        await harness.DisposeAsync();
    }
}
