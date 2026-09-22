using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using Synapic.Shared;
using Synapic.Shared.Contracts;

namespace Synapic.Avalonia.Services;

/// <summary>Sidecar lifecycle state shown in the UI server indicator (spec §2).</summary>
public enum SidecarStatus
{
    Stopped,
    Starting,
    Ready,
    Error,
}

public sealed class SidecarStatusChangedEventArgs : EventArgs
{
    public SidecarStatus Status { get; }
    public string? Message { get; }

    public SidecarStatusChangedEventArgs(SidecarStatus status, string? message = null)
    {
        Status = status;
        Message = message;
    }
}

/// <summary>
/// Launches and manages the PyInstaller sidecar process (spec §5.3):
/// auto-launched with the app by default (`ui.autoLaunchSidecar=false` opts
/// out to manual Start/Stop), orphan-free shutdown either way.
/// </summary>
public interface IInferenceSidecar : IAsyncDisposable
{
    SidecarStatus CurrentStatus { get; }
    int SidecarPort { get; }
    bool IsRunning { get; }

    event EventHandler<SidecarStatusChangedEventArgs>? StatusChanged;
    event Action<string>? LogReceived;

    Task StartAsync(CancellationToken ct = default);
    Task StopAsync(TimeSpan? gracefulTimeout = null);
    Task<TagResponse> TagAsync(TagRequest request, CancellationToken ct = default);
    Task<ModelInfo[]> ListModelsAsync(CancellationToken ct = default);
    Task DownloadModelAsync(string modelId, CancellationToken ct = default);
    Task<HealthResponse> GetHealthAsync(CancellationToken ct = default);
}

public sealed class InferenceSidecarService : IInferenceSidecar
{
    private const string PortFileEnvVar = "SYNAPIC_PORT_FILE";
    private static readonly TimeSpan HealthTimeout = TimeSpan.FromSeconds(120);
    private static readonly TimeSpan TagTimeout = TimeSpan.FromMinutes(5);

    private HttpClient _httpClient;
    private InferenceApiClient _api;
    private readonly object _gate = new();

    private Process? _process;
    private SidecarStatus _status = SidecarStatus.Stopped;
    private int _port;
    private CancellationTokenSource? _livenessCts;

    public event EventHandler<SidecarStatusChangedEventArgs>? StatusChanged;
    public event Action<string>? LogReceived;

    public InferenceSidecarService(HttpClient? httpClient = null)
    {
        _httpClient = httpClient ?? new HttpClient();
        _api = new InferenceApiClient(_httpClient);
    }

    public SidecarStatus CurrentStatus
    {
        get { lock (_gate) return _status; }
    }

    public int SidecarPort
    {
        get { lock (_gate) return _port; }
    }

    public bool IsRunning
    {
        get
        {
            lock (_gate)
            {
                var p = _process;
                return p is { HasExited: false };
            }
        }
    }

    private void SetStatus(SidecarStatus status, string? message = null)
    {
        bool changed;
        lock (_gate)
        {
            changed = _status != status;
            _status = status;
        }
        SynapicLog.Info(nameof(InferenceSidecarService), $"Sidecar status: {status}{(message is null ? "" : $" — {message}")}");
        if (changed) StatusChanged?.Invoke(this, new SidecarStatusChangedEventArgs(status, message));
    }

    /// <summary>
    /// Locates the sidecar executable: bundled next to the app executable, or
    /// in a dev checkout's artifacts output (found by walking up to the repo
    /// root). Returns null when no executable exists yet.
    /// </summary>
    /// <summary>Sidecar executable file name for this platform.</summary>
    public static string ExeName => OperatingSystem.IsWindows() ? "synapic-inference.exe" : "synapic-inference";

    /// <summary>The RID whose output this machine runs by default.</summary>
    public static string PreferredRid()
    {
        var arm64 = RuntimeInformation.ProcessArchitecture == Architecture.Arm64;
        return OperatingSystem.IsWindows() ? "win-x64"
            : OperatingSystem.IsMacOS() ? (arm64 ? "osx-arm64" : "osx-x64")
            : (arm64 ? "linux-arm64" : "linux-x64");
    }

    /// <summary>
    /// RIDs this machine can build a sidecar for, most recommended first.
    /// CUDA is a Windows-only packaging variant - it shares the win-x64
    /// interpreter and differs only in which torch wheels are installed and
    /// bundled (see build/install-python-deps.ps1).
    /// </summary>
    public static IReadOnlyList<string> BuildableRids()
    {
        if (OperatingSystem.IsWindows())
            return new[] { "win-x64", "win-x64-cuda" };
        return new[] { PreferredRid() };
    }

    /// <summary>Friendly platform name for a RID, e.g. "CPU" or "CUDA".</summary>
    public static string VariantDisplayName(string rid) =>
        rid.EndsWith("-cuda", StringComparison.OrdinalIgnoreCase) ? "CUDA (NVIDIA GPU)" : "CPU";

    /// <summary>
    /// Locates the built sidecar for one specific RID. A packaged install
    /// bundles a single executable next to the app, which satisfies whichever
    /// RID matches this machine; dev checkouts look in artifacts/&lt;rid&gt;.
    /// </summary>
    public static string? FindExecutableForRid(string rid)
    {
        if (string.Equals(rid, PreferredRid(), StringComparison.OrdinalIgnoreCase))
        {
            var bundled = Path.Combine(AppContext.BaseDirectory, ExeName);
            if (File.Exists(bundled)) return bundled;
        }

        var repoRoot = FindRepoRoot();
        if (repoRoot is null) return null;

        var path = Path.Combine(repoRoot, "artifacts", rid, ExeName);
        return File.Exists(path) ? path : null;
    }

    public static string? FindExecutable()
    {
        var bundled = Path.Combine(AppContext.BaseDirectory, ExeName);
        if (File.Exists(bundled)) return bundled;

        var repoRoot = FindRepoRoot();
        if (repoRoot is null) return null;

        var artifactsDir = Path.Combine(repoRoot, "artifacts");
        if (!Directory.Exists(artifactsDir)) return null;

        // Prefer the output matching this machine's RID, then any other.
        var preferred = Path.Combine(artifactsDir, PreferredRid(), ExeName);
        if (File.Exists(preferred)) return preferred;

        foreach (var dir in Directory.EnumerateDirectories(artifactsDir))
        {
            var candidate = Path.Combine(dir, ExeName);
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    /// <summary>
    /// Walks up from the app directory to the repository root (dev checkouts
    /// only; installed apps carry no .sln).
    /// </summary>
    public static string? FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 12 && dir is not null; i++, dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Synapic.Net.sln")))
                return dir.FullName;
        }
        return null;
    }

    public async Task StartAsync(CancellationToken ct = default)
    {
        SynapicLog.Info(nameof(InferenceSidecarService), $"StartAsync requested (current status: {CurrentStatus})");
        lock (_gate)
        {
            if (_status is SidecarStatus.Starting or SidecarStatus.Ready)
            {
                SynapicLog.Info(nameof(InferenceSidecarService), "StartAsync ignored - sidecar already starting/ready");
                return;
            }
            if (_process is { HasExited: false })
            {
                SynapicLog.Info(nameof(InferenceSidecarService), "StartAsync ignored - sidecar process alive");
                SetStatus(SidecarStatus.Ready);
                return;
            }
        }

        var sidecarPath = FindExecutable();
        if (sidecarPath is null)
        {
            var exeName = OperatingSystem.IsWindows() ? "synapic-inference.exe" : "synapic-inference";
            SynapicLog.Error(nameof(InferenceSidecarService),
                $"Sidecar executable not found. Searched bundled: {Path.Combine(AppContext.BaseDirectory, exeName)} and artifacts/ under repo root: {FindRepoRoot() ?? "<repo root not found>"}");
            SetStatus(SidecarStatus.Error, "Sidecar executable not found - build it first");
            throw new FileNotFoundException("Sidecar executable not found. Use Build Server first.");
        }

        SweepStalePortFiles();
        SetStatus(SidecarStatus.Starting);

        var portFile = Path.Combine(Path.GetTempPath(), $"synapic_port_{Environment.ProcessId}.txt");
        File.Delete(portFile);

        var startInfo = new ProcessStartInfo
        {
            FileName = sidecarPath,
            Arguments = "--port=0",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        startInfo.EnvironmentVariables[PortFileEnvVar] = portFile;
        // Model cache location (spec §6.2): the sidecar's HF_HOME points at
        // the Synapic-managed models directory.
        startInfo.EnvironmentVariables["HF_HOME"] = ModelsRoot();

        var process = new Process { StartInfo = startInfo };
        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                SynapicLog.Debug(nameof(InferenceSidecarService), $"[sidecar] {e.Data}");
                LogReceived?.Invoke(e.Data);
            }
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                SynapicLog.Debug(nameof(InferenceSidecarService), $"[sidecar:err] {e.Data}");
                LogReceived?.Invoke(e.Data);
            }
        };

        try
        {
            if (!process.Start())
                throw new InvalidOperationException("Failed to start sidecar process");
        }
        catch (Exception e)
        {
            process.Dispose();
            SetStatus(SidecarStatus.Error, e.Message);
            throw;
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        lock (_gate)
        {
            _process = process;
            _livenessCts = new CancellationTokenSource();
        }

        _ = Task.Run(() => WatchProcessExit(process, _livenessCts!.Token));

        SynapicLog.Info(nameof(InferenceSidecarService),
            $"Sidecar launched: pid {process.Id}, exe {sidecarPath}, args '{startInfo.Arguments}', port file {portFile}, HF_HOME {startInfo.EnvironmentVariables["HF_HOME"]}");

        // Tie the sidecar's lifetime to this app's (spec §2 orphan-free
        // shutdown): when the app exits - even by crash or kill - the OS
        // terminates the sidecar. This is the root fix for the app finding a
        // leftover server "already running" on the next launch.
        ProcessJob.AssignChild(process);

        try
        {
            var port = await ReadPortFileAsync(portFile, ct).ConfigureAwait(false);
            ConfigurePort(port);
            SynapicLog.Info(nameof(InferenceSidecarService), $"Port file read: port {port}");
            await WaitUntilReadyAsync(ct).ConfigureAwait(false);
            SetStatus(SidecarStatus.Ready);
        }
        catch (Exception e)
        {
            SynapicLog.Error(nameof(InferenceSidecarService), "Sidecar startup failed", e);
            SetStatus(SidecarStatus.Error, e.Message);
            await StopAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// Deletes leftover port files whose sidecar pid is dead (from crashed
    /// sessions). Pure hygiene: stale files can no longer be adopted, but
    /// removing them keeps the temp directory and logs meaningful.
    /// </summary>
    private static void SweepStalePortFiles()
    {
        try
        {
            foreach (var file in Directory.GetFiles(Path.GetTempPath(), "synapic_port_*.txt"))
            {
                try
                {
                    var lines = File.ReadAllLines(file).Where(l => !string.IsNullOrWhiteSpace(l)).ToArray();
                    if (lines.Length >= 2 && int.TryParse(lines[1].Trim(), out var pid) && IsPidAlive(pid))
                        continue; // still-running sidecar - leave it alone
                    File.Delete(file);
                    SynapicLog.Debug(nameof(InferenceSidecarService), $"Removed stale port file: {file}");
                }
                catch (Exception)
                {
                    // Best effort.
                }
            }
        }
        catch (Exception e)
        {
            SynapicLog.Debug(nameof(InferenceSidecarService), $"Port file sweep skipped: {e.Message}");
        }
    }

    private static bool IsPidAlive(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch
        {
            return false;
        }
    }

    public static string ModelsRoot()
    {
        if (OperatingSystem.IsWindows())
        {
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return Path.Combine(localAppData, "Synapic", "models");
        }
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(home, ".cache", "synapic", "models");
    }

    private static async Task<int> ReadPortFileAsync(string portFile, CancellationToken ct)
    {
        SynapicLog.Debug(nameof(InferenceSidecarService), $"Waiting for port file: {portFile}");
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        var waitedMs = 0;
        while (DateTime.UtcNow < deadline)
        {
            if (waitedMs > 0 && waitedMs % 2000 == 0)
                SynapicLog.Debug(nameof(InferenceSidecarService), $"Still waiting for port file ({waitedMs / 1000.0:F0}s)... (sidecar booting)");
            waitedMs += 200;
            ct.ThrowIfCancellationRequested();
            try
            {
                if (File.Exists(portFile))
                {
                    var content = await File.ReadAllTextAsync(portFile, ct).ConfigureAwait(false);
                    var lines = content.Split('\n', StringSplitOptions.RemoveEmptyEntries);
                    if (lines.Length >= 1 && int.TryParse(lines[0].Trim(), out var port) && port > 0)
                    {
                        // Validate PID liveness when present (port file protocol).
                        if (lines.Length >= 2 && int.TryParse(lines[1].Trim(), out var pid))
                        {
                            try
                            {
                                using var proc = Process.GetProcessById(pid);
                            }
                            catch (ArgumentException e)
                            {
                                throw new InvalidOperationException($"Sidecar pid {pid} is not alive", e);
                            }
                        }
                        return port;
                    }
                }
            }
            catch (InvalidOperationException)
            {
                throw;
            }
            catch (Exception)
            {
                // File may be mid-write; retry.
            }
            await Task.Delay(200, ct).ConfigureAwait(false);
        }
        throw new TimeoutException("Timed out waiting for the sidecar port file");
    }

    private async Task WaitUntilReadyAsync(CancellationToken ct)
    {
        SynapicLog.Debug(nameof(InferenceSidecarService), $"Polling /health until ready (max {HealthTimeout.TotalSeconds:F0}s)");
        var deadline = DateTime.UtcNow + HealthTimeout;
        var attempts = 0;
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var health = await _api.GetHealthAsync(ct).ConfigureAwait(false);
                attempts++;
                if (string.Equals(health.Status, "ready", StringComparison.OrdinalIgnoreCase))
                {
                    SynapicLog.Info(nameof(InferenceSidecarService), $"/health ready after {attempts} poll(s)");
                    return;
                }
                // Log progress periodically so slow boots are visible in the log.
                if (attempts % 20 == 1)
                    SynapicLog.Debug(nameof(InferenceSidecarService),
                        $"/health attempt {attempts}: status={health.Status} model={health.Model}");
                if (string.Equals(health.Status, "error", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException(health.Error ?? "Sidecar reported error state");
            }
            catch (InvalidOperationException)
            {
                throw;
            }
            catch (Exception)
            {
                // Connection refused while uvicorn boots — keep polling.
                if (++attempts % 20 == 1)
                    SynapicLog.Debug(nameof(InferenceSidecarService), $"/health attempt {attempts}: connection refused (server booting)");
            }
            await Task.Delay(500, ct).ConfigureAwait(false);
        }
        throw new TimeoutException($"Sidecar did not become ready within {HealthTimeout.TotalSeconds}s");
    }

    private void WatchProcessExit(Process process, CancellationToken ct)
    {
        try
        {
            process.WaitForExitAsync(ct).GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
            return; // deliberate stop
        }
        catch (Exception)
        {
            // WaitForExitAsync can throw when the process handle detaches.
        }

        lock (_gate)
        {
            if (!ReferenceEquals(_process, process)) return; // replaced by a newer launch
            _process = null;
        }

        try
        {
            SynapicLog.Info(nameof(InferenceSidecarService), $"Sidecar process exited (pid {process.Id}, exit code {process.ExitCode})");
        }
        catch
        {
            SynapicLog.Info(nameof(InferenceSidecarService), "Sidecar process exited (exit code unavailable)");
        }

        if (CurrentStatus is SidecarStatus.Ready or SidecarStatus.Starting)
        {
            SynapicLog.Warning(nameof(InferenceSidecarService), "Sidecar process exited unexpectedly");
            SetStatus(SidecarStatus.Error, "Server stopped unexpectedly");
        }
    }

    public async Task StopAsync(TimeSpan? gracefulTimeout = null)
    {
        var timeout = gracefulTimeout ?? TimeSpan.FromSeconds(5);
        Process? process;
        CancellationTokenSource? livenessCts;
        lock (_gate)
        {
            process = _process;
            livenessCts = _livenessCts;
            _process = null;
            _livenessCts = null;
        }

        SetStatus(SidecarStatus.Stopped);

        if (process is null)
        {
            SynapicLog.Info(nameof(InferenceSidecarService), "StopAsync: no sidecar process to stop");
            return;
        }

        livenessCts?.Cancel();

        try
        {
            if (!process.HasExited)
            {
                if (SidecarPort > 0)
                {
                    using var shutdownCts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                    await _api.ShutdownAsync(shutdownCts.Token).ConfigureAwait(false);
                }
                else
                {
                    SynapicLog.Debug(nameof(InferenceSidecarService), "Port never became known - skipping graceful shutdown POST, killing directly");
                }
                await process.WaitForExitAsync(new CancellationTokenSource(timeout).Token).ConfigureAwait(false);
            }
        }
        catch (Exception e)
        {
            SynapicLog.Warning(nameof(InferenceSidecarService), $"Graceful shutdown failed ({e.Message}); killing process tree");
        }
        finally
        {
            try
            {
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);
            }
            catch
            {
                // Already exited.
            }
            process.Dispose();
        }

        try
        {
            var ownPortFile = Path.Combine(Path.GetTempPath(), $"synapic_port_{Environment.ProcessId}.txt");
            if (File.Exists(ownPortFile)) File.Delete(ownPortFile);
        }
        catch
        {
            // Best effort.
        }

        SynapicLog.Info(nameof(InferenceSidecarService), "Sidecar stopped");
    }

    private void ConfigurePort(int port)
    {
        lock (_gate) _port = port;

        // HttpClient forbids changing BaseAddress after its first request -
        // and the UI health poll during "Starting" marks the original
        // instance as started. Recreate the client (and API wrapper) bound
        // to the port instead of mutating the existing one.
        var newClient = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}/") };
        _api = new InferenceApiClient(newClient);
        _httpClient = newClient;
        SynapicLog.Debug(nameof(InferenceSidecarService), $"API client bound to http://127.0.0.1:{port}/");
    }

    public async Task<HealthResponse> GetHealthAsync(CancellationToken ct = default) => await _api.GetHealthAsync(ct).ConfigureAwait(false);

    public async Task<TagResponse> TagAsync(TagRequest request, CancellationToken ct = default)
    {
        return await _api.TagAsync(request, ct).ConfigureAwait(false);
    }

    public async Task<ModelInfo[]> ListModelsAsync(CancellationToken ct = default)
    {
        return await _api.ListModelsAsync(ct).ConfigureAwait(false);
    }

    public async Task DownloadModelAsync(string modelId, CancellationToken ct = default)
    {
        await _api.DownloadModelAsync(modelId, "main", ct).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _httpClient.Dispose();
        GC.SuppressFinalize(this);
    }
}
