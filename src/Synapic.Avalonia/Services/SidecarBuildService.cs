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

    /// <summary>
    /// Runs the pipeline for one RID (fetch Python, install deps, PyInstaller).
    /// Streams lines to <paramref name="log"/> and stage/percentage updates to
    /// <paramref name="progress"/>. The RID suffix <c>-cuda</c> selects the
    /// CUDA torch wheel index (see install-python-deps).
    /// </summary>
    Task BuildAsync(string rid, Action<string> log, IProgress<SidecarBuildProgress> progress, CancellationToken ct);

    /// <summary>Cancels an active build (used on app shutdown).</summary>
    void Cancel();
}

public sealed class SidecarBuildService : ISidecarBuildService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);

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

    /// <summary>Rejects RIDs that could be interpreted by the shell we launch.</summary>
    private static void ValidateRid(string rid)
    {
        if (string.IsNullOrWhiteSpace(rid) || rid.Length > 64 ||
            rid.Any(c => !char.IsLetterOrDigit(c) && c != '-' && c != '_'))
            throw new ArgumentException($"Invalid RID: '{rid}'", nameof(rid));
    }

    public async Task BuildAsync(string rid, Action<string> log, IProgress<SidecarBuildProgress> progress, CancellationToken ct)
    {
        ValidateRid(rid);
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

        var tracker = new SidecarBuildProgressTracker(root, rid);
        progress.Report(SidecarBuildProgress.Starting);

        try
        {
            log($"[build] Building the inference server for {rid} ({InferenceSidecarService.VariantDisplayName(rid)}); the first build downloads Python + packages...");

            var psi = OperatingSystem.IsWindows()
                ? new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"{script}\" -Rid \"{rid}\"",
                }
                : new ProcessStartInfo
                {
                    FileName = "bash",
                    Arguments = $"\"{script}\" \"{rid}\"",
                };
            psi.WorkingDirectory = root;
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;

            using var process = new Process { StartInfo = psi };

            // Both streams feed the log and the stage tracker. Reports are
            // de-duplicated and serialized because the output callbacks arrive
            // on thread-pool threads while the packaging poll reports its own.
            var reportGate = new object();
            var lastPercent = -1.0;
            var lastStage = string.Empty;

            void Report()
            {
                lock (reportGate)
                {
                    if (Math.Abs(tracker.Percent - lastPercent) < 0.01 && tracker.Stage == lastStage) return;
                    lastPercent = tracker.Percent;
                    lastStage = tracker.Stage;
                    progress.Report(new SidecarBuildProgress(tracker.Percent, tracker.Stage));
                }
            }

            void Handle(string? line)
            {
                if (line is null) return;
                log(line);
                tracker.Observe(line);
                Report();
            }

            process.OutputDataReceived += (_, e) => Handle(e.Data);
            process.ErrorDataReceived += (_, e) => Handle(e.Data);

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

            using var pollCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
            var poll = PollPackagingAsync(tracker, Report, pollCts.Token);
            try
            {
                await process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
            }
            finally
            {
                pollCts.Cancel();
                try { await poll.ConfigureAwait(false); } catch (OperationCanceledException) { }
            }

            if (process.ExitCode != 0)
                throw new InvalidOperationException(
                    $"Server build failed with exit code {process.ExitCode} - see the log for details.");

            progress.Report(new SidecarBuildProgress(100, "Sidecar built"));
            log("[build] Server build complete.");
        }
        finally
        {
            lock (_gate) _activeCts = null;
        }
    }

    /// <summary>
    /// Drives the packaging band from the bytes landing on disk while
    /// PyInstaller runs, which is otherwise silent for many minutes at a time.
    /// </summary>
    private static async Task PollPackagingAsync(SidecarBuildProgressTracker tracker, Action report, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            tracker.PollBytes();
            report();
            try
            {
                await Task.Delay(PollInterval, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }
}
