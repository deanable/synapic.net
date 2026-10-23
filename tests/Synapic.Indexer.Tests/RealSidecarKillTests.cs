using System.Diagnostics;
using Synapic.Core.Services;
using Synapic.Core.Services.Processing;
using Synapic.Shared.Contracts;
using Synapic.Indexer.Configuration;
using Synapic.Indexer.Detection;
using Synapic.Indexer.Hosting;
using Synapic.Indexer.Pipeline;
using Synapic.Indexer.State;
using Xunit;
using Xunit.Abstractions;

namespace Synapic.Indexer.Tests;

/// <summary>
/// The real thing: a batch through the executor, the production sidecar process
/// launched by <see cref="InferenceSidecarService"/>, that process killed while a
/// /tag request is in flight, and the batch finishing after the supervisor
/// restarts it.
///
/// Opt-in (<c>SYNAPIC_TEST_REAL_KILL=1</c>) because it loads the model twice and
/// needs a built bundle plus cached weights; without them it skips with the
/// reason instead of pretending to pass.
/// </summary>
public sealed class RealSidecarKillTests
{
    private const string ModelId = "LiquidAI/LFM2.5-VL-1.6B";
    private const int Images = 6;

    private readonly ITestOutputHelper _output;

    public RealSidecarKillTests(ITestOutputHelper output) => _output = output;

    [SkippableFact]
    public async Task Killing_the_sidecar_mid_batch_restarts_it_and_the_batch_finishes()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "The real sidecar is a Windows bundle.");
        Skip.IfNot(Environment.GetEnvironmentVariable("SYNAPIC_TEST_REAL_KILL") == "1",
            "Set SYNAPIC_TEST_REAL_KILL=1 to run the real sidecar kill test.");

        var executable = SidecarExecutable.Locate();
        Skip.If(executable.Path is null,
            "No synapic-inference bundle was found. Searched: " + string.Join("; ", executable.Searched));

        var modelRoot = Path.Combine(InferenceSidecarService.ModelsRoot(), "hub", "models--LiquidAI--LFM2.5-VL-1.6B");
        Skip.IfNot(Directory.Exists(modelRoot),
            $"The tagging model is not cached at {modelRoot}; this test will not download it.");

        var root = Path.Combine(Path.GetTempPath(), "synapic-real-kill-" + Guid.NewGuid().ToString("N"));
        var imagesDir = Path.Combine(root, "images");
        Directory.CreateDirectory(imagesDir);

        var paths = new IndexerPaths(root);
        paths.EnsureCreated();

        var config = IndexerConfig.CreateDefault();
        config.Service.MaxConcurrentItems = 1;
        config.Service.IdleSidecarTimeoutMinutes = 0;
        config.Engine.Device = "cpu";
        config.Engine.ModelId = ModelId;
        config.Tagging = new TaggingSettings
        {
            WriteCategory = true,
            WriteKeywords = true,
            WriteDescription = true,
            OverwriteExistingTags = true,
        };
        config.Schedule.WatchPaths.Add(imagesDir);

        var repository = new IndexingQueueRepository(paths.QueueFilePath);
        await repository.InitializeAsync();

        var writer = new MetadataWriterService();
        var hasher = new Sha256FileHasher();
        var policy = new PreserveExistingTagsPolicy(writer, TagRequestFactory.Fields(config.Tagging), overwrite: true);

        await using var sidecar = new InferenceSidecarService(options: new SidecarLaunchOptions
        {
            DeviceProvider = new StaticSidecarDeviceProvider("cpu"),
            ExecutablePathOverride = executable.Path,
            EnforceSourceStaleness = false,
        });
        var supervisor = new SidecarSupervisor(
            sidecar,
            config,
            paths,
            new NoWorkActivity(),
            executable,
            readinessTimeout: TimeSpan.FromMinutes(10),
            readinessPollInterval: TimeSpan.FromMilliseconds(500),
            idleCheckInterval: TimeSpan.FromMinutes(30));

        // The wrapper is only an observation point: the first /tag that starts is
        // the signal to kill the process, so the crash cannot land between items.
        var observed = new FirstTagObservedSidecar(sidecar);
        var executor = new BatchExecutor(
            observed, supervisor, repository, config, writer, hasher, policy,
            new SidecarRecovery(supervisor), TimeProvider.System);

        try
        {
            var sample = PipelineHarness.FindSample();
            for (var i = 0; i < Images; i++)
                File.Copy(sample, Path.Combine(imagesDir, $"kill-{i:00}.jpg"));

            var scanner = new LocalFolderScanSource(config, repository, hasher, new WorkIntake(repository));
            var scan = await scanner.ScanAsync(CancellationToken.None);
            Assert.Equal(Images, scan.Enqueued);

            using var batch = new CancellationTokenSource(TimeSpan.FromMinutes(15));
            var run = executor.ExecuteOnceAsync(batch.Token);

            await observed.FirstTag.Task.WaitAsync(TimeSpan.FromMinutes(12), batch.Token);
            Assert.False(run.IsCompleted,
                "The batch finished before the first tag could be interrupted; the test proves nothing.");

            var killedPid = sidecar.ProcessId;
            Assert.NotNull(killedPid);
            using (var process = Process.GetProcessById(killedPid!.Value))
            {
                process.Kill(entireProcessTree: true);
                Assert.True(process.WaitForExit(10_000), "The sidecar process did not exit after being killed.");
            }

            await run;

            // The crash may leave more rows Pending than the retried batch claimed.
            for (var i = 0; i < 10; i++)
            {
                var summary = await repository.GetSummaryAsync();
                if (summary.Pending == 0 && summary.InFlight == 0 && summary.TransientFailure == 0) break;
                await executor.ExecuteOnceAsync(batch.Token);
            }

            var final = await repository.GetSummaryAsync();
            _output.WriteLine($"killed pid={killedPid}; final: pending={final.Pending} completed={final.Completed} " +
                              $"transient={final.TransientFailure} failed={final.Failed} " +
                              $"running={sidecar.IsRunning} processId={sidecar.ProcessId} status={sidecar.CurrentStatus}");

            Assert.Equal(Images, final.Completed);
            Assert.Equal(0, final.Pending);
            Assert.Equal(0, final.InFlight);
            Assert.Equal(0, final.Failed);

            // The restart is a new process, not a surviving one.
            Assert.NotNull(sidecar.ProcessId);
            Assert.NotEqual(killedPid, sidecar.ProcessId);

            var retried = 0;
            foreach (var file in Directory.GetFiles(imagesDir))
            {
                var row = (await repository.GetByKeyAsync(PathKey.ForLocal(file)))!;
                Assert.Equal(JobState.Completed, row.State);
                if (row.Attempts >= 2) retried++;

                var tags = await writer.ReadAsync(file);
                Assert.NotNull(tags);
                Assert.False(string.IsNullOrWhiteSpace(tags!.Category), $"{file} has no category");
                Assert.NotEmpty(tags.Keywords);
            }

            // The item whose /tag was in flight when the process died must have
            // been attempted again — a completion count alone could hide a crash
            // that happened after the batch was already done.
            Assert.True(retried >= 1, "No row was retried; the kill did not land mid-batch.");
        }
        finally
        {
            using var stopBudget = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            try { await supervisor.EnsureStoppedAsync(stopBudget.Token); }
            catch (Exception) { /* best effort: the finally must not mask a failure */ }

            repository.ClearPool();
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }

    /// <summary>Delegates everything to the real sidecar; reports when a /tag begins.</summary>
    private sealed class FirstTagObservedSidecar : IInferenceSidecar
    {
        private readonly IInferenceSidecar _inner;
        private int _tags;

        public FirstTagObservedSidecar(IInferenceSidecar inner) => _inner = inner;

        public TaskCompletionSource FirstTag { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public SidecarStatus CurrentStatus => _inner.CurrentStatus;
        public int SidecarPort => _inner.SidecarPort;
        public bool IsRunning => _inner.IsRunning;
        public int? ProcessId => _inner.ProcessId;

        public event EventHandler<SidecarStatusChangedEventArgs>? StatusChanged
        {
            add => _inner.StatusChanged += value;
            remove => _inner.StatusChanged -= value;
        }

        public event Action<string>? LogReceived
        {
            add => _inner.LogReceived += value;
            remove => _inner.LogReceived -= value;
        }

        public async Task<TagResponse> TagAsync(TagRequest request, CancellationToken ct = default)
        {
            if (Interlocked.Increment(ref _tags) == 1) FirstTag.TrySetResult();
            return await _inner.TagAsync(request, ct).ConfigureAwait(false);
        }

        public Task StartAsync(CancellationToken ct = default) => _inner.StartAsync(ct);
        public Task StopAsync(TimeSpan? gracefulTimeout = null) => _inner.StopAsync(gracefulTimeout);
        public Task<ModelInfo[]> ListModelsAsync(CancellationToken ct = default) => _inner.ListModelsAsync(ct);
        public Task DownloadModelAsync(string modelId, CancellationToken ct = default) =>
            _inner.DownloadModelAsync(modelId, ct);
        public Task<HealthResponse> GetHealthAsync(CancellationToken ct = default) => _inner.GetHealthAsync(ct);
        public Task<UpscaleResponse> UpscaleAsync(UpscaleRequest request, CancellationToken ct = default) =>
            _inner.UpscaleAsync(request, ct);
        public Task SetDeviceAsync(string? device, CancellationToken ct = default) => _inner.SetDeviceAsync(device, ct);
        public Task<PromptDefaultsDto> GetPromptDefaultsAsync(CancellationToken ct = default) =>
            _inner.GetPromptDefaultsAsync(ct);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
