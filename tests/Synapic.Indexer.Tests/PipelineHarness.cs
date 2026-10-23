using Synapic.Core.Services;
using Synapic.Core.Services.Processing;
using Synapic.Indexer.Configuration;
using Synapic.Indexer.Detection;
using Synapic.Indexer.Hosting;
using Synapic.Indexer.Pipeline;
using Synapic.Indexer.State;
using Synapic.Shared.Contracts;

namespace Synapic.Indexer.Tests;

/// <summary>A deterministic sidecar: canned tags, optional per-call behaviour, and observable overlap.</summary>
internal sealed class FakeSidecar : IInferenceSidecar
{
    private int _active;
    private int _tagCalls;

    public SidecarStatus CurrentStatus => SidecarStatus.Ready;
    public int SidecarPort => 0;
    public bool IsRunning { get; set; } = true;

#pragma warning disable CS0067 // the pipeline never subscribes
    public event EventHandler<SidecarStatusChangedEventArgs>? StatusChanged;
    public event Action<string>? LogReceived;
#pragma warning restore CS0067

    /// <summary>Per-request behaviour; null means "return the canned tags".</summary>
    public Func<TagRequest, CancellationToken, Task<TagResponse>>? OnTag { get; set; }

    public int TagCalls => _tagCalls;
    public int MaxConcurrent { get; private set; }

    public static TagResponse CannedTags => new()
    {
        Category = "Nature",
        Keywords = new[] { "forest", "tree" },
        Description = "A forest at dawn",
    };

    public Task StartAsync(CancellationToken ct = default)
    {
        IsRunning = true;
        return Task.CompletedTask;
    }

    public Task StopAsync(TimeSpan? gracefulTimeout = null)
    {
        IsRunning = false;
        return Task.CompletedTask;
    }

    public async Task<TagResponse> TagAsync(TagRequest request, CancellationToken ct = default)
    {
        Interlocked.Increment(ref _tagCalls);
        var active = Interlocked.Increment(ref _active);
        if (active > MaxConcurrent) MaxConcurrent = active;
        try
        {
            if (OnTag is null) return CannedTags;
            return await OnTag(request, ct).ConfigureAwait(false);
        }
        finally
        {
            Interlocked.Decrement(ref _active);
        }
    }

    public Task<ModelInfo[]> ListModelsAsync(CancellationToken ct = default) =>
        Task.FromResult(Array.Empty<ModelInfo>());

    public Task DownloadModelAsync(string modelId, CancellationToken ct = default) => Task.CompletedTask;
    public Task<HealthResponse> GetHealthAsync(CancellationToken ct = default) => Task.FromResult(new HealthResponse());
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

internal sealed class FakeSupervisor : ISidecarSupervisor
{
    public bool IsReady { get; set; } = true;
    public int EnsureReadyCalls { get; private set; }
    public Queue<Exception> Failures { get; } = new();

    public Task EnsureReadyAsync(CancellationToken ct)
    {
        EnsureReadyCalls++;
        if (Failures.Count > 0) throw Failures.Dequeue();
        IsReady = true;
        return Task.CompletedTask;
    }

    public Task EnsureStoppedAsync(CancellationToken ct)
    {
        IsReady = false;
        return Task.CompletedTask;
    }
}

internal sealed class FakeRecovery : ISidecarRecovery
{
    public bool Succeeds { get; set; } = true;
    public int Calls { get; private set; }

    /// <summary>Stands in for "the restarted process is serving again".</summary>
    public Action? OnRecovered { get; set; }

    public Task<bool> TryRecoverAsync(CancellationToken ct)
    {
        Calls++;
        if (Succeeds) OnRecovered?.Invoke();
        return Task.FromResult(Succeeds);
    }
}

/// <summary>A temp queue, real images and a real metadata writer behind the real executor.</summary>
internal sealed class PipelineHarness : IDisposable
{
    private PipelineHarness(string root)
    {
        Root = root;
    }

    public string Root { get; }
    public TestClock Clock { get; } = new();
    public FakeSidecar Sidecar { get; } = new();
    public FakeSupervisor Supervisor { get; } = new();
    public FakeRecovery Recovery { get; } = new();
    public Sha256FileHasher Hasher { get; } = new();
    public required IndexingQueueRepository Repository { get; init; }
    public required IndexerConfig Config { get; init; }
    public required IMetadataWriter Writer { get; init; }
    public required PreserveExistingTagsPolicy Policy { get; init; }

    /// <summary>Set after construction because it depends on the harness's own clock and fakes.</summary>
    public BatchExecutor Executor { get; set; } = null!;
    public string SampleSource { get; private set; } = "";

    public static string FindSample()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 8 && dir is not null; i++, dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "tests", "Synapic.Core.Tests", "TestData", "sample.jpg");
            if (File.Exists(candidate)) return candidate;
        }

        throw new FileNotFoundException("tests/Synapic.Core.Tests/TestData/sample.jpg was not found.");
    }

    public static async Task<PipelineHarness> CreateAsync(
        int concurrency = 1,
        bool overwrite = true,
        IMetadataWriter? writer = null,
        bool catalog = false,
        Func<DatasourceSelection>? catalogSelection = null,
        ISidecarRecovery? recovery = null,
        Action<IndexerConfig>? configure = null)
    {
        var root = Path.Combine(Path.GetTempPath(), "synapic-indexer-pipeline-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        var repository = new IndexingQueueRepository(Path.Combine(root, "indexer.db"));
        await repository.InitializeAsync();

        var config = IndexerConfig.CreateDefault();
        config.Service.MaxConcurrentItems = concurrency;
        config.Engine.ModelId = "test/model";
        config.Tagging = new TaggingSettings
        {
            WriteCategory = true,
            WriteKeywords = true,
            WriteDescription = true,
            OverwriteExistingTags = overwrite,
        };
        configure?.Invoke(config);

        var actualWriter = writer ?? new MetadataWriterService();
        var fields = TagRequestFactory.Fields(config.Tagging);
        var harness = new PipelineHarness(root)
        {
            Repository = repository,
            Config = config,
            Writer = actualWriter,
            Policy = new PreserveExistingTagsPolicy(actualWriter, fields, overwrite),
        };

        var sample = FindSample();
        var copy = Path.Combine(root, "sample.jpg");
        File.Copy(sample, copy);
        harness.SampleSource = copy;

        harness.Executor = new BatchExecutor(
            harness.Sidecar,
            harness.Supervisor,
            repository,
            config,
            actualWriter,
            harness.Hasher,
            harness.Policy,
            recovery ?? harness.Recovery,
            harness.Clock,
            catalog ? catalogSelection ?? (() => new DatasourceSelection { IsDaminion = true }) : null);

        return harness;
    }

    /// <summary>Copies the sample JPEG to a new name and returns its path.</summary>
    public string AddImage(string name)
    {
        var path = Path.Combine(Root, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.Copy(SampleSource, path, overwrite: true);
        return path;
    }

    public JobCandidate CandidateFor(string path) => new(
        PathKey.ForLocal(path), "local", path, null, Path.GetFileName(path),
        new FileInfo(path).Length, File.GetLastWriteTimeUtc(path),
        Hasher.TryHashAsync(path, CancellationToken.None).GetAwaiter().GetResult());

    public async Task<JobRecord> EnqueueAsync(string path)
    {
        await Repository.EnqueueAsync(new[] { CandidateFor(path) });
        return (await Repository.GetByKeyAsync(PathKey.ForLocal(path)))!;
    }

    public async Task<JobRecord> EnqueueCatalogAsync(int id, string name, long hash = 0, long size = 0)
    {
        await Repository.EnqueueAsync(new[]
        {
            new JobCandidate(PathKey.ForDaminion(id), "daminion", null, id, name, size > 0 ? size : null, null,
                hash != 0 ? ContentFingerprint.ServerHash(hash) : null),
        });
        return (await Repository.GetByKeyAsync(PathKey.ForDaminion(id)))!;
    }

    public void Dispose()
    {
        try { Repository.ClearPool(); } catch (Exception) { }
        try { Directory.Delete(Root, recursive: true); } catch (IOException) { }
    }
}
