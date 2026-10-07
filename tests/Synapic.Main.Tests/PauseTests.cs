using Synapic.Avalonia.Models;
using Synapic.Avalonia.Services;
using Synapic.Avalonia.Services.Processing;
using Synapic.Shared.Contracts;
using Xunit;

namespace Synapic.Avalonia.Tests;

/// <summary>Sidecar fake whose TagAsync blocks until the test releases it — lets pause tests observe in-flight vs queued items deterministically.</summary>
internal sealed class BlockingSidecar : IInferenceSidecar
{
    private readonly object _lock = new();
    private TaskCompletionSource? _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _started;

    public int Started => Volatile.Read(ref _started);
    public int Completed => Volatile.Read(ref _completed);
    private int _completed;

    public SidecarStatus CurrentStatus => SidecarStatus.Ready;
    public int SidecarPort => 0;
    public bool IsRunning => true;

#pragma warning disable CS0067 // events unused by this fake
    public event EventHandler<SidecarStatusChangedEventArgs>? StatusChanged;
    public event Action<string>? LogReceived;
#pragma warning restore CS0067

    public Task StartAsync(CancellationToken ct = default) => Task.CompletedTask;
    public Task StopAsync(TimeSpan? gracefulTimeout = null) => Task.CompletedTask;

    public async Task<TagResponse> TagAsync(TagRequest request, CancellationToken ct = default)
    {
        Interlocked.Increment(ref _started);
        TaskCompletionSource? gate;
        lock (_lock)
        {
            gate = _gate; // null after ReleaseAll → items complete immediately
        }
        if (gate is not null) await gate.Task.WaitAsync(ct);
        Interlocked.Increment(ref _completed);
        return new TagResponse { Category = "cat", Keywords = ["kw"], Description = "desc" };
    }

    /// <summary>Complete the current in-flight item(s); subsequent TagAsync calls return immediately.</summary>
    public void ReleaseAll()
    {
        lock (_lock)
        {
            _gate?.TrySetResult();
            _gate = null;
        }
    }

    public Task<ModelInfo[]> ListModelsAsync(CancellationToken ct = default) => Task.FromResult(Array.Empty<ModelInfo>());
    public Task DownloadModelAsync(string modelId, CancellationToken ct = default) => Task.CompletedTask;
    public Task<HealthResponse> GetHealthAsync(CancellationToken ct = default) => Task.FromResult(new HealthResponse());
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

/// <summary>Metadata-writer stub: claims success without touching files.</summary>
internal sealed class StubMetadataWriter : IMetadataWriter
{
    public Task<bool> WriteAsync(string filePath, TagResult tags, CancellationToken ct) => Task.FromResult(true);
    public Task<TagResult?> ReadAsync(string filePath, CancellationToken ct) => Task.FromResult<TagResult?>(null);
}

public class PauseTests
{
    private static DatasourceSelection LocalSelection(string dir) => new()
    {
        IsDaminion = false,
        LocalPath = dir,
        LocalRecursive = false,
    };

    private static string[] MakeImageFiles(string dir, int count)
    {
        Directory.CreateDirectory(dir);
        var paths = new string[count];
        for (var i = 0; i < count; i++)
        {
            paths[i] = Path.Combine(dir, $"img{i}.jpg");
            File.WriteAllBytes(paths[i], new byte[] { 1 }); // content irrelevant — sidecar is faked
        }
        return paths;
    }

    [Fact]
    public async Task Pause_blocks_queued_items_and_resume_completes_them()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"synapic-pause-{Guid.NewGuid():N}");
        var paths = MakeImageFiles(dir, 3);
        try
        {
            var sidecar = new BlockingSidecar();
            var orchestrator = new ProcessingOrchestrator(sidecar, new StubMetadataWriter(), maxDegreeOfParallelism: 1);
            var results = new List<ProcessItemResult>();
            var pauseSource = new PauseTokenSource();
            var template = new TagRequest { ImagePath = "", ModelId = "m", Task = "image-text-to-text" };

            var run = orchestrator.RunAsync(
                LocalSelection(dir), template, new Progress<ProcessProgress>(), _ => Task.CompletedTask,
                CancellationToken.None, results, pauseSource.Token);

            // Wait until item 1 is in flight inside TagAsync.
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (sidecar.Started < 1 && DateTime.UtcNow < deadline)
                await Task.Delay(25);
            Assert.Equal(1, sidecar.Started);

            pauseSource.Pause();

            // Let the in-flight item finish; queued items must stay blocked while paused.
            sidecar.ReleaseAll();
            await Task.Delay(400);
            Assert.True(sidecar.Started <= 2, $"pause did not block: {sidecar.Started} items started");
            Assert.Equal(1, sidecar.Completed);

            pauseSource.Resume();
            await run.WaitAsync(TimeSpan.FromSeconds(10));

            Assert.Equal(3, results.Count);
            Assert.All(results, r => Assert.Equal("Success", r.Status));
            Assert.Equal(3, sidecar.Completed);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task Abort_wins_over_pause()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"synapic-pause-abort-{Guid.NewGuid():N}");
        MakeImageFiles(dir, 3);
        try
        {
            var sidecar = new BlockingSidecar();
            var orchestrator = new ProcessingOrchestrator(sidecar, new StubMetadataWriter(), maxDegreeOfParallelism: 1);
            var pauseSource = new PauseTokenSource();
            var cts = new CancellationTokenSource();
            var template = new TagRequest { ImagePath = "", ModelId = "m", Task = "image-text-to-text" };

            var run = orchestrator.RunAsync(
                LocalSelection(dir), template, new Progress<ProcessProgress>(), _ => Task.CompletedTask,
                cts.Token, null, pauseSource.Token);

            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (sidecar.Started < 1 && DateTime.UtcNow < deadline)
                await Task.Delay(25);

            pauseSource.Pause();
            sidecar.ReleaseAll(); // in-flight item completes
            await Task.Delay(200);
            cts.Cancel();         // abort while paused — waiters must throw, not hang

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run.WaitAsync(TimeSpan.FromSeconds(10)));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
