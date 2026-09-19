using System.Diagnostics;
using System.IO;

namespace Synapic.Avalonia.Services;

/// <summary>
/// Builds the Python inference sidecar (synapic-inference.exe) from source by
/// running the repo's build pipeline. Only available in development checkouts
/// that ship the build scripts; installed apps bundle a prebuilt sidecar.
/// </summary>
public interface ISidecarBuildService
{
    /// <summary>True when the build scripts were found (dev checkout).</summary>
    bool CanBuild { get; }

    bool IsBuilding { get; }

    /// <summary>Runs the pipeline: fetch Python, install deps, PyInstaller. Streams lines to <paramref name="log"/>.</summary>
    Task BuildAsync(Action<string> log, CancellationToken ct);

    /// <summary>Cancels an active build (used on app shutdown).</summary>
    void Cancel();
}

public sealed class SidecarBuildService : ISidecarBuildService
{
    private readonly object _gate = new();
    private CancellationTokenSource? _activeCts;

    private static string BuildScriptName => OperatingSystem.IsWindows() ? "build-server.ps1" : "build-server.sh";

    public bool CanBuild
    {
        get
        {
            var root = InferenceSidecarService.FindRepoRoot();
            return root is not null && File.Exists(Path.Combine(root, "build", BuildScriptName));
        }
    }

    public bool IsBuilding
    {
        get { lock (_gate) return _activeCts is not null; }
    }

    public void Cancel()
    {
        CancellationTokenSource? cts;
        lock (_gate) cts = _activeCts;
        cts?.Cancel();
    }

    public async Task BuildAsync(Action<string> log, CancellationToken ct)
    {
        var root = InferenceSidecarService.FindRepoRoot()
            ?? throw new InvalidOperationException(
                "Source checkout not found; the server cannot be built from an installed app.");
        var script = Path.Combine(root, "build", BuildScriptName);
        if (!File.Exists(script))
            throw new InvalidOperationException($"Build script not found: {script}");

        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        lock (_gate)
        {
            if (_activeCts is not null)
                throw new InvalidOperationException("A build is already running.");
            _activeCts = cts;
        }

        try
        {
            log("[build] Building the inference server (first build downloads Python + packages)...");

            var psi = OperatingSystem.IsWindows()
                ? new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"{script}\"",
                }
                : new ProcessStartInfo
                {
                    FileName = "bash",
                    Arguments = $"\"{script}\"",
                };
            psi.WorkingDirectory = root;
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;

            using var process = new Process { StartInfo = psi };
            process.OutputDataReceived += (_, e) => { if (e.Data is not null) log(e.Data); };
            process.ErrorDataReceived += (_, e) => { if (e.Data is not null) log(e.Data); };

            if (!process.Start())
                throw new InvalidOperationException("Failed to launch the build script");

            // Kill the script (and its children) when the build is cancelled.
            using var killReg = cts.Token.Register(() =>
            {
                try
                {
                    if (!process.HasExited)
                        process.Kill(entireProcessTree: true);
                }
                catch
                {
                    // Already gone.
                }
            });

            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            await process.WaitForExitAsync(cts.Token).ConfigureAwait(false);

            if (process.ExitCode != 0)
                throw new InvalidOperationException(
                    $"Server build failed with exit code {process.ExitCode} - see the log for details.");

            log("[build] Server build complete.");
        }
        finally
        {
            lock (_gate) _activeCts = null;
        }
    }
}
