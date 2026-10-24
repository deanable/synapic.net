using System.Diagnostics;
using Synapic.Core.Services;
using Synapic.Indexer.Hosting;

namespace Synapic.Indexer.Install;

/// <summary>
/// Registers, starts and removes the service through <c>sc.exe</c> (spec §7.1,
/// HOST-01, D-11-30). Phase 8's installer calls this same code rather than
/// duplicating the command sequence; two implementations of "install the
/// service" would drift.
///
/// Install is the spec's create → failure → start sequence, and it is
/// idempotent: an existing service has its recovery actions refreshed and is
/// started if it is not already running.
/// </summary>
public sealed class ServiceInstaller
{
    private const int ServiceAlreadyRunning = 1056;
    private const int ServiceNotInstalled = 1060;
    private const int ServiceNotStarted = 1062;

    private readonly IScRunner _runner;
    private readonly Action<IndexerPaths, Action<string>> _ensureProgramData;

    public ServiceInstaller() : this(new ScRunner()) { }

    /// <param name="runner">The SCM seam; tests substitute a recorder.</param>
    /// <param name="ensureProgramData">
    /// Creates the ProgramData tree and its ACLs. Injected in tests so they never
    /// touch <c>%ProgramData%</c> or <c>icacls</c>.
    /// </param>
    public ServiceInstaller(
        IScRunner runner,
        Action<IndexerPaths, Action<string>>? ensureProgramData = null)
    {
        _runner = runner;
        _ensureProgramData = ensureProgramData
            ?? ((paths, log) => ProgramDataSetup.Ensure(paths, log: log));
    }

    public async Task<int> InstallAsync(string executablePath, IndexerPaths paths, CancellationToken ct = default)
    {
        var query = await _runner.RunAsync(ServiceCommandLine.BuildQueryArguments(), ct).ConfigureAwait(false);
        var exists = query.ExitCode == 0;

        if (exists)
        {
            // Idempotent: refreshing the recovery actions is the useful part on a
            // re-run; creating again would just fail with "already exists".
            SynapicLog.Info(nameof(ServiceInstaller),
                $"{ServiceCommandLine.ServiceName} is already installed; refreshing its recovery settings.");
        }
        else
        {
            var create = await _runner.RunAsync(
                ServiceCommandLine.BuildCreateArguments(executablePath), ct).ConfigureAwait(false);
            if (create.ExitCode != 0)
            {
                SynapicLog.Error(nameof(ServiceInstaller),
                    $"sc create failed with exit code {create.ExitCode}: {create.Output.Trim()}");
                return create.ExitCode;
            }
            SynapicLog.Info(nameof(ServiceInstaller),
                $"Created service {ServiceCommandLine.ServiceName} (auto start) for '{executablePath}'.");
        }

        var failure = await _runner.RunAsync(ServiceCommandLine.BuildFailureArguments(), ct).ConfigureAwait(false);
        if (failure.ExitCode != 0)
        {
            SynapicLog.Error(nameof(ServiceInstaller),
                $"sc failure failed with exit code {failure.ExitCode}: {failure.Output.Trim()}");
            return failure.ExitCode;
        }
        SynapicLog.Info(nameof(ServiceInstaller),
            "Recovery actions set: restart 3 times at 60 s intervals, failure count reset daily.");

        EnsureEventLogSource();

        // ACLs come with the directory tree; the installer (phase 8) runs this
        // same step, so a manual --install and a package install agree.
        try
        {
            _ensureProgramData(paths, message => SynapicLog.Info(nameof(ProgramDataSetup), message));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            // icacls needs the same elevation sc.exe does; report it as a failed
            // install instead of an unhandled exception in a service binary.
            SynapicLog.Error(nameof(ServiceInstaller),
                $"Could not secure {paths.Root}: {e.Message}. Run --install from an elevated prompt.");
            Console.Error.WriteLine($"Could not secure {paths.Root}: {e.Message}");
            return 1;
        }

        // D-11-30: the installer makes one call, so the start belongs here. The
        // spec's sequence is create → failure → start; a machine where the SCM
        // already has it running is a success, not an error (re-run of --install).
        var start = await _runner.RunAsync(ServiceCommandLine.BuildStartArguments(), ct).ConfigureAwait(false);
        if (start.ExitCode is not (0 or ServiceAlreadyRunning or ServiceNotStarted))
        {
            SynapicLog.Error(nameof(ServiceInstaller),
                $"sc start failed with exit code {start.ExitCode}: {start.Output.Trim()}");
            Console.Error.WriteLine(
                $"The {ServiceCommandLine.DisplayName} service was installed but could not be started " +
                $"(sc start exit code {start.ExitCode}). Start it with: sc.exe start {ServiceCommandLine.ServiceName}");
            return start.ExitCode;
        }

        SynapicLog.Info(nameof(ServiceInstaller),
            start.ExitCode == 0
                ? $"Started service {ServiceCommandLine.ServiceName}."
                : $"Service {ServiceCommandLine.ServiceName} was already running (sc start {start.ExitCode}).");
        return 0;
    }

    public async Task<int> UninstallAsync(IndexerPaths paths, CancellationToken ct = default)
    {
        var query = await _runner.RunAsync(ServiceCommandLine.BuildQueryArguments(), ct).ConfigureAwait(false);
        if (query.ExitCode == ServiceNotInstalled)
        {
            SynapicLog.Info(nameof(ServiceInstaller), $"{ServiceCommandLine.ServiceName} is not installed; nothing to remove.");
            return 0;
        }

        var stop = await _runner.RunAsync(ServiceCommandLine.BuildStopArguments(), ct).ConfigureAwait(false);
        if (stop.ExitCode != 0 && stop.ExitCode != ServiceNotStarted && stop.ExitCode != ServiceNotInstalled)
        {
            SynapicLog.Warning(nameof(ServiceInstaller),
                $"sc stop returned {stop.ExitCode}: {stop.Output.Trim()} (continuing to delete).");
        }

        // Give the SCM its ten seconds to reach STOPPED before the delete; a
        // service still shutting down answers 1072 to sc delete.
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            var state = await _runner.RunAsync(ServiceCommandLine.BuildQueryArguments(), ct).ConfigureAwait(false);
            if (state.ExitCode == ServiceNotInstalled || state.Output.Contains("STOPPED", StringComparison.OrdinalIgnoreCase))
                break;
            await Task.Delay(TimeSpan.FromMilliseconds(250), ct).ConfigureAwait(false);
        }

        var delete = await _runner.RunAsync(ServiceCommandLine.BuildDeleteArguments(), ct).ConfigureAwait(false);
        if (delete.ExitCode != 0)
        {
            SynapicLog.Error(nameof(ServiceInstaller),
                $"sc delete failed with exit code {delete.ExitCode}: {delete.Output.Trim()}");
            return delete.ExitCode;
        }

        SynapicLog.Info(nameof(ServiceInstaller),
            $"Removed service {ServiceCommandLine.ServiceName}. Logs and data under {paths.Root} were kept.");
        return 0;
    }

    /// <summary>
    /// Stops and starts the service when it is running, so a configuration
    /// applied by <c>--apply-config</c> takes effect immediately; a stopped or
    /// absent service is left alone and reported as not restarted.
    /// </summary>
    public async Task<bool> RestartIfRunningAsync(CancellationToken ct = default)
    {
        var query = await _runner.RunAsync(ServiceCommandLine.BuildQueryArguments(), ct).ConfigureAwait(false);
        if (query.ExitCode != 0 || !query.Output.Contains("RUNNING", StringComparison.OrdinalIgnoreCase))
        {
            SynapicLog.Info(nameof(ServiceInstaller),
                $"{ServiceCommandLine.ServiceName} is not running; no restart is needed.");
            return false;
        }

        var stop = await _runner.RunAsync(ServiceCommandLine.BuildStopArguments(), ct).ConfigureAwait(false);
        if (stop.ExitCode != 0)
        {
            SynapicLog.Warning(nameof(ServiceInstaller),
                $"sc stop returned {stop.ExitCode}: {stop.Output.Trim()} (continuing; the service will read the change at its next start).");
            return false;
        }

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            var state = await _runner.RunAsync(ServiceCommandLine.BuildQueryArguments(), ct).ConfigureAwait(false);
            if (state.ExitCode != 0 || state.Output.Contains("STOPPED", StringComparison.OrdinalIgnoreCase))
                break;
            await Task.Delay(TimeSpan.FromMilliseconds(250), ct).ConfigureAwait(false);
        }

        var start = await _runner.RunAsync(ServiceCommandLine.BuildStartArguments(), ct).ConfigureAwait(false);
        if (start.ExitCode != 0)
        {
            SynapicLog.Error(nameof(ServiceInstaller),
                $"sc start returned {start.ExitCode}: {start.Output.Trim()}");
            return false;
        }

        SynapicLog.Info(nameof(ServiceInstaller),
            $"{ServiceCommandLine.ServiceName} restarted to pick up a configuration change.");
        return true;
    }

    private static void EnsureEventLogSource()
    {
        if (IndexerLogging.EventLogSourceExists())
        {
            SynapicLog.Info(nameof(ServiceInstaller),
                $"Event Log source '{IndexerLogging.EventLogSource}' already exists.");
            return;
        }

        try
        {
            // Needs the elevated prompt --install runs under (D-11-08).
            EventLog.CreateEventSource(IndexerLogging.EventLogSource, "Application");
            SynapicLog.Info(nameof(ServiceInstaller),
                $"Created Event Log source '{IndexerLogging.EventLogSource}' in the Application log.");
        }
        catch (Exception e) when (e is System.Security.SecurityException or InvalidOperationException or UnauthorizedAccessException)
        {
            SynapicLog.Warning(nameof(ServiceInstaller),
                $"Could not create the Event Log source ({e.GetType().Name}: {e.Message}); " +
                "the service will log to its file only.");
        }
    }
}
