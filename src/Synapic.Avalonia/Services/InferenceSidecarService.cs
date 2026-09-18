using System.Diagnostics;
using System.IO;
using System.Net.Http;
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
/// manual launch by default, opt-in auto-launch, orphan-free shutdown.
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

    private readonly HttpClient _httpClient;
    private readonly InferenceApiClient _api;
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

    /// <summary>Path of the bundled sidecar next to the app executable.</summary>
    public static string ResolveSidecarPath()
    {
        var exeName = OperatingSystem.IsWindows() ? "synapic-inference.exe" : "synapic-inference";
        return Path.Combine(AppContext.BaseDirectory, exeName);
    }

    public async Task StartAsync(CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (_status is SidecarStatus.Starting or SidecarStatus.Ready) return;
            if (_process is { HasExited: false })
            {
                SetStatus(SidecarStatus.Ready);
                return;
            }
        }

        var sidecarPath = ResolveSidecarPath();
        if (!File.Exists(sidecarPath))
        {
            SetStatus(SidecarStatus.Error, $"Sidecar executable not found: {sidecarPath}");
            throw new FileNotFoundException("Sidecar executable not found", sidecarPath);
        }

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
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) LogReceived?.Invoke(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) LogReceived?.Invoke(e.Data); };

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

        SynapicLog.Info(nameof(InferenceSidecarService), $"Sidecar launched (pid {process.Id}), port file: {portFile}");

        try
        {
            _port = await ReadPortFileAsync(portFile, ct).ConfigureAwait(false);
            _httpClient.BaseAddress = new Uri($"http://127.0.0.1:{_port}/");
            await WaitUntilReadyAsync(ct).ConfigureAwait(false);
            SetStatus(SidecarStatus.Ready);
        }
        catch (Exception e)
        {
            SetStatus(SidecarStatus.Error, e.Message);
            await StopAsync().ConfigureAwait(false);
            throw;
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
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
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
        var deadline = DateTime.UtcNow + HealthTimeout;
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var health = await _api.GetHealthAsync(ct).ConfigureAwait(false);
                if (string.Equals(health.Status, "ready", StringComparison.OrdinalIgnoreCase))
                    return;
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

        if (process is null) return;

        livenessCts?.Cancel();

        try
        {
            if (!process.HasExited)
            {
                using var shutdownCts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                await _api.ShutdownAsync(shutdownCts.Token).ConfigureAwait(false);
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

        SynapicLog.Info(nameof(InferenceSidecarService), "Sidecar stopped");
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
