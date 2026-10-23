using System.Diagnostics;
using System.Globalization;
using Synapic.Core.Services;
using Synapic.Indexer.Configuration;

namespace Synapic.Indexer.Hosting;

/// <summary>
/// Owns the sidecar's lifetime inside the service (HOST-04): start it, wait for
/// <c>/health</c> to say ready, stop it when the configured idle timeout expires,
/// clean up a leftover from a crashed run, and stop it on shutdown with a 5 s
/// budget inside the host's 10 s one.
///
/// The process itself is Core's <see cref="IInferenceSidecar"/> — this class adds
/// the policy a background service needs, not a second launcher.
/// </summary>
public sealed class SidecarSupervisor : ISidecarSupervisor
{
    private static readonly TimeSpan StopBudget = TimeSpan.FromSeconds(5);

    private readonly IInferenceSidecar _sidecar;
    private readonly IndexerConfig _config;
    private readonly IndexerPaths _paths;
    private readonly ISidecarActivity _activity;
    private readonly SidecarExecutable _executable;
    private readonly TimeSpan _readinessTimeout;
    private readonly TimeSpan _readinessPoll;
    private readonly TimeSpan _idleCheckInterval;
    private readonly Func<DateTimeOffset> _clock;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private CancellationTokenSource? _idleCts;
    private DateTimeOffset _readyAt;
    private bool _orphanChecked;

    public SidecarSupervisor(
        IInferenceSidecar sidecar,
        IndexerConfig config,
        IndexerPaths paths,
        ISidecarActivity activity,
        SidecarExecutable executable,
        TimeSpan? readinessTimeout = null,
        TimeSpan? readinessPollInterval = null,
        TimeSpan? idleCheckInterval = null,
        Func<DateTimeOffset>? clock = null)
    {
        _sidecar = sidecar;
        _config = config;
        _paths = paths;
        _activity = activity;
        _executable = executable;
        _readinessTimeout = readinessTimeout ?? TimeSpan.FromSeconds(120);
        _readinessPoll = readinessPollInterval ?? TimeSpan.FromSeconds(2);
        _idleCheckInterval = idleCheckInterval ?? TimeSpan.FromSeconds(30);
        _clock = clock ?? (() => DateTimeOffset.Now);
    }

    public bool IsReady { get; private set; }

    public async Task EnsureReadyAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (IsReady && _sidecar.IsRunning) return;

            if (_executable.Path is null)
                throw new SidecarUnavailableException(SidecarExecutable.MissingMessage(_executable.Searched));

            if (!_orphanChecked)
            {
                CleanupOrphan();
                _orphanChecked = true;
            }

            SynapicLog.Info(nameof(SidecarSupervisor),
                $"Starting sidecar '{_executable.Path}' for device {_config.Engine.Device}.");
            await _sidecar.StartAsync(ct).ConfigureAwait(false);
            await WaitForReadyAsync(ct).ConfigureAwait(false);

            WritePidRecord();
            _readyAt = _clock();
            IsReady = true;
            StartIdleLoop();

            SynapicLog.Info(nameof(SidecarSupervisor),
                $"Sidecar ready on port {_sidecar.SidecarPort} (idle timeout {_config.Service.IdleSidecarTimeoutMinutes} min).");
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task EnsureStoppedAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await StopCoreAsync().ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task StopCoreAsync()
    {
        CancelIdleLoop();

        if (_sidecar.IsRunning)
        {
            try
            {
                SynapicLog.Info(nameof(SidecarSupervisor), "Stopping the sidecar.");
                await _sidecar.StopAsync(StopBudget).ConfigureAwait(false);
            }
            catch (Exception e)
            {
                SynapicLog.Warning(nameof(SidecarSupervisor), $"Sidecar stop reported {e.GetType().Name}: {e.Message}");
            }
        }

        IsReady = false;
        DeletePidRecord();
    }

    private async Task WaitForReadyAsync(CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + _readinessTimeout;
        string? lastStatus = null;
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var health = await _sidecar.GetHealthAsync(ct).ConfigureAwait(false);
                lastStatus = health.Status;
                if (string.Equals(health.Status, "ready", StringComparison.OrdinalIgnoreCase)) return;
            }
            catch (HttpRequestException e)
            {
                // The port is open but the app is not serving yet.
                lastStatus = e.Message;
            }

            var remaining = deadline - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero) break;
            await Task.Delay(remaining < _readinessPoll ? remaining : _readinessPoll, ct).ConfigureAwait(false);
        }

        throw new TimeoutException(
            $"The sidecar did not report ready within {_readinessTimeout.TotalSeconds:F0}s " +
            $"(last status: {lastStatus ?? "no response"}). See the service log for the sidecar's output.");
    }

    private void StartIdleLoop()
    {
        CancelIdleLoop();
        _idleCts = new CancellationTokenSource();
        var token = _idleCts.Token;
        _ = Task.Run(() => IdleLoopAsync(token), CancellationToken.None);
    }

    private void CancelIdleLoop()
    {
        try { _idleCts?.Cancel(); } catch (ObjectDisposedException) { }
        _idleCts?.Dispose();
        _idleCts = null;
    }

    private async Task IdleLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(_idleCheckInterval, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            var minutes = _config.Service.IdleSidecarTimeoutMinutes;
            var lastActivity = _activity.LastWorkAt is { } at
                ? new DateTimeOffset(DateTime.SpecifyKind(at, DateTimeKind.Local))
                : _readyAt;

            if (!ShouldStopForIdle(_clock(), lastActivity, TimeSpan.FromMinutes(minutes),
                    _activity.HasPendingWork, enabled: minutes > 0))
            {
                continue;
            }

            SynapicLog.Info(nameof(SidecarSupervisor),
                $"No work for {minutes} min; stopping the sidecar (it restarts on the next batch).");
            try
            {
                await EnsureStoppedAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception e)
            {
                SynapicLog.Warning(nameof(SidecarSupervisor), $"Idle stop failed: {e.GetType().Name}: {e.Message}");
            }
            return;
        }
    }

    /// <summary>
    /// The idle rule, as a pure fact: a positive timeout, no pending work and no
    /// activity for at least that long. Zero means "never stop" (§5.2).
    /// </summary>
    public static bool ShouldStopForIdle(
        DateTimeOffset now,
        DateTimeOffset lastActivity,
        TimeSpan timeout,
        bool hasPendingWork,
        bool enabled)
        => enabled && timeout > TimeSpan.Zero && !hasPendingWork && now - lastActivity >= timeout;

    /// <summary>
    /// Whether a pid record names a leftover sidecar: the image name must match
    /// (never kill an unrelated process because a file said so) and it must not
    /// be this run's child.
    /// </summary>
    public static bool IsOrphanTarget(string? processName, int recordedPid, int? currentChildPid)
    {
        if (recordedPid == currentChildPid.GetValueOrDefault(-1)) return false;
        var expected = Path.GetFileNameWithoutExtension(InferenceSidecarService.ExeName);
        return string.Equals(processName, expected, StringComparison.OrdinalIgnoreCase);
    }

    private void CleanupOrphan()
    {
        if (!File.Exists(_paths.SidecarPidFile)) return;

        try
        {
            var text = File.ReadAllText(_paths.SidecarPidFile).Trim();
            if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var pid))
            {
                SynapicLog.Warning(nameof(SidecarSupervisor),
                    $"The sidecar pid record in {_paths.SidecarPidFile} is not a pid; discarded.");
                return;
            }

            using var process = Process.GetProcessById(pid);
            if (!IsOrphanTarget(process.ProcessName, pid, _sidecar.ProcessId))
            {
                SynapicLog.Warning(nameof(SidecarSupervisor),
                    $"Pid {pid} from the previous run is '{process.ProcessName}', not a sidecar; left alone.");
                return;
            }

            process.Kill(entireProcessTree: true);
            process.WaitForExit(milliseconds: (int)StopBudget.TotalMilliseconds);
            SynapicLog.Warning(nameof(SidecarSupervisor),
                $"Terminated the sidecar left behind by a previous run (pid {pid}).");
        }
        catch (ArgumentException)
        {
            // The recorded pid is no longer running: nothing to clean up.
        }
        catch (Exception e) when (e is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            SynapicLog.Warning(nameof(SidecarSupervisor),
                $"Could not clean up the previous sidecar: {e.GetType().Name}: {e.Message}");
        }
        finally
        {
            DeletePidRecord();
        }
    }

    private void WritePidRecord()
    {
        var pid = _sidecar.ProcessId;
        if (pid is null) return;
        Directory.CreateDirectory(_paths.StateDirectory);
        File.WriteAllText(_paths.SidecarPidFile, pid.Value.ToString(CultureInfo.InvariantCulture));
    }

    private void DeletePidRecord()
    {
        try
        {
            if (File.Exists(_paths.SidecarPidFile)) File.Delete(_paths.SidecarPidFile);
        }
        catch (IOException e)
        {
            SynapicLog.Warning(nameof(SidecarSupervisor), $"Could not delete the sidecar pid record: {e.Message}");
        }
    }
}
