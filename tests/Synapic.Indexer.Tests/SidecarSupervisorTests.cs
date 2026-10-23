using Synapic.Core.Services;
using Synapic.Indexer.Configuration;
using Synapic.Indexer.Hosting;
using Synapic.Shared.Contracts;
using Xunit;

namespace Synapic.Indexer.Tests;

public sealed class SidecarSupervisorTests : IDisposable
{
    private readonly string _root;
    private readonly IndexerPaths _paths;
    private readonly IndexerConfig _config = IndexerConfig.CreateDefault();

    public SidecarSupervisorTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "synapic-indexer-sup-" + Guid.NewGuid().ToString("N"));
        _paths = new IndexerPaths(_root);
        _paths.EnsureCreated();
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private SidecarSupervisor Supervisor(
        FakeSidecar sidecar,
        SidecarExecutable? executable = null,
        ISidecarActivity? activity = null,
        TimeSpan? readinessTimeout = null,
        TimeSpan? idleCheckInterval = null,
        Func<DateTimeOffset>? clock = null)
        => new(
            sidecar,
            _config,
            _paths,
            activity ?? new NoWorkActivity(),
            executable ?? new SidecarExecutable("C:\\fake\\synapic-inference.exe", new[] { "C:\\fake\\synapic-inference.exe" }),
            readinessTimeout: readinessTimeout ?? TimeSpan.FromSeconds(5),
            readinessPollInterval: TimeSpan.FromMilliseconds(10),
            idleCheckInterval: idleCheckInterval,
            clock: clock);

    [Fact]
    public async Task Ready_means_health_said_ready_not_just_that_the_process_started()
    {
        var sidecar = new FakeSidecar { ReadyAfterHealthCalls = 3 };
        var supervisor = Supervisor(sidecar);

        await supervisor.EnsureReadyAsync(CancellationToken.None);

        Assert.True(supervisor.IsReady);
        Assert.Equal(1, sidecar.StartCount);
        Assert.True(sidecar.HealthCount >= 3);

        await supervisor.EnsureStoppedAsync(CancellationToken.None);
    }

    [Fact]
    public async Task A_sidecar_that_never_reports_ready_fails_with_a_timeout()
    {
        var sidecar = new FakeSidecar { NeverReady = true };
        var supervisor = Supervisor(sidecar, readinessTimeout: TimeSpan.FromMilliseconds(150));

        var error = await Assert.ThrowsAsync<TimeoutException>(
            () => supervisor.EnsureReadyAsync(CancellationToken.None));

        Assert.Contains("did not report ready", error.Message);
        Assert.False(supervisor.IsReady);
    }

    [Fact]
    public async Task A_missing_executable_is_fatal_and_names_every_searched_path()
    {
        var sidecar = new FakeSidecar();
        var searched = new[] { "C:\\Program Files\\Synapic\\synapic-inference.exe", "C:\\Users\\me\\sidecars" };
        var supervisor = Supervisor(sidecar, new SidecarExecutable(null, searched));

        var error = await Assert.ThrowsAsync<SidecarUnavailableException>(
            () => supervisor.EnsureReadyAsync(CancellationToken.None));

        Assert.Contains(searched[0], error.Message);
        Assert.Contains(searched[1], error.Message);
        Assert.Equal(0, sidecar.StartCount);
    }

    [Fact]
    public async Task Stopping_calls_StopAsync_once_and_clears_the_pid_record()
    {
        var sidecar = new FakeSidecar { ProcessId = 4242 };
        File.WriteAllText(_paths.SidecarPidFile, "4242");
        var supervisor = Supervisor(sidecar);
        await supervisor.EnsureReadyAsync(CancellationToken.None);

        Assert.True(File.Exists(_paths.SidecarPidFile));
        Assert.Contains("4242", File.ReadAllText(_paths.SidecarPidFile));

        await supervisor.EnsureStoppedAsync(CancellationToken.None);

        Assert.Equal(1, sidecar.StopCount);
        Assert.False(supervisor.IsReady);
        Assert.False(File.Exists(_paths.SidecarPidFile));
    }

    [Fact]
    public async Task A_stopped_supervisor_starts_the_sidecar_again()
    {
        var sidecar = new FakeSidecar();
        var supervisor = Supervisor(sidecar);

        await supervisor.EnsureReadyAsync(CancellationToken.None);
        await supervisor.EnsureStoppedAsync(CancellationToken.None);
        await supervisor.EnsureReadyAsync(CancellationToken.None);

        Assert.True(supervisor.IsReady);
        Assert.Equal(2, sidecar.StartCount);

        await supervisor.EnsureStoppedAsync(CancellationToken.None);
    }

    [Fact]
    public void The_idle_rule_needs_a_timeout_no_work_and_enough_elapsed_time()
    {
        var now = DateTimeOffset.Now;
        var timeout = TimeSpan.FromMinutes(30);

        Assert.True(SidecarSupervisor.ShouldStopForIdle(now, now - timeout, timeout, false, enabled: true));
        // 0 means never stop.
        Assert.False(SidecarSupervisor.ShouldStopForIdle(now, now - TimeSpan.FromDays(1), TimeSpan.Zero, false, enabled: false));
        // Pending work keeps the sidecar alive regardless of the clock.
        Assert.False(SidecarSupervisor.ShouldStopForIdle(now, now - TimeSpan.FromDays(1), timeout, true, enabled: true));
        // Not yet idle for long enough.
        Assert.False(SidecarSupervisor.ShouldStopForIdle(now, now - TimeSpan.FromMinutes(5), timeout, false, enabled: true));
    }

    [Fact]
    public async Task The_idle_timer_stops_the_sidecar_after_the_configured_timeout()
    {
        var now = DateTimeOffset.Now;
        var sidecar = new FakeSidecar();
        var supervisor = Supervisor(sidecar, idleCheckInterval: TimeSpan.FromMilliseconds(10),
            clock: () => now);

        await supervisor.EnsureReadyAsync(CancellationToken.None);
        now = now.AddMinutes(_config.Service.IdleSidecarTimeoutMinutes + 1);

        await WaitUntilAsync(() => sidecar.StopCount > 0);

        Assert.False(supervisor.IsReady);
    }

    [Fact]
    public async Task Pending_work_keeps_the_sidecar_running_past_the_timeout()
    {
        var now = DateTimeOffset.Now;
        var sidecar = new FakeSidecar();
        var supervisor = Supervisor(sidecar, activity: new AlwaysBusyActivity(), idleCheckInterval: TimeSpan.FromMilliseconds(10),
            clock: () => now);

        await supervisor.EnsureReadyAsync(CancellationToken.None);
        now = now.AddHours(2);
        await Task.Delay(200);

        Assert.Equal(0, sidecar.StopCount);
        Assert.True(supervisor.IsReady);

        await supervisor.EnsureStoppedAsync(CancellationToken.None);
    }

    [Fact]
    public void An_orphan_is_only_a_matching_image_with_a_different_pid()
    {
        Assert.True(SidecarSupervisor.IsOrphanTarget("synapic-inference", 100, currentChildPid: 200));
        Assert.True(SidecarSupervisor.IsOrphanTarget("Synapic-Inference", 100, currentChildPid: null));
        Assert.False(SidecarSupervisor.IsOrphanTarget("synapic-inference", 100, currentChildPid: 100));
        Assert.False(SidecarSupervisor.IsOrphanTarget("notepad", 100, currentChildPid: 200));
        Assert.False(SidecarSupervisor.IsOrphanTarget(null, 100, currentChildPid: 200));
    }

    private static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 5000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return;
            await Task.Delay(10);
        }
        Assert.True(condition(), "condition was not met within the timeout");
    }

    private sealed class AlwaysBusyActivity : ISidecarActivity
    {
        public DateTime? LastWorkAt => null;
        public bool HasPendingWork => true;
    }

    private sealed class FakeSidecar : IInferenceSidecar
    {
        public SidecarStatus CurrentStatus { get; set; } = SidecarStatus.Stopped;
        public int SidecarPort => 12345;
        public bool IsRunning { get; private set; }
        public int? ProcessId { get; set; } = 4242;
        public int StartCount { get; private set; }
        public int StopCount { get; private set; }
        public int HealthCount { get; private set; }
        public int ReadyAfterHealthCalls { get; set; } = 1;
        public bool NeverReady { get; set; }

#pragma warning disable CS0067 // the supervisor does not subscribe to these
        public event EventHandler<SidecarStatusChangedEventArgs>? StatusChanged;
        public event Action<string>? LogReceived;
#pragma warning restore CS0067

        public Task StartAsync(CancellationToken ct = default)
        {
            StartCount++;
            IsRunning = true;
            CurrentStatus = SidecarStatus.Starting;
            return Task.CompletedTask;
        }

        public Task StopAsync(TimeSpan? gracefulTimeout = null)
        {
            StopCount++;
            IsRunning = false;
            CurrentStatus = SidecarStatus.Stopped;
            return Task.CompletedTask;
        }

        public Task<HealthResponse> GetHealthAsync(CancellationToken ct = default)
        {
            HealthCount++;
            var status = !NeverReady && HealthCount >= ReadyAfterHealthCalls ? "ready" : "loading";
            return Task.FromResult(new HealthResponse { Status = status });
        }

        public Task<TagResponse> TagAsync(TagRequest request, CancellationToken ct = default) =>
            Task.FromException<TagResponse>(new NotSupportedException());

        public Task<ModelInfo[]> ListModelsAsync(CancellationToken ct = default) =>
            Task.FromResult(Array.Empty<ModelInfo>());

        public Task DownloadModelAsync(string modelId, CancellationToken ct = default) =>
            Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
