using System.Text.Json.Nodes;
using Synapic.Indexer.Contract;
using Synapic.Indexer.Contract.Configuration;

namespace Synapic.ServiceConsole.Services;

/// <summary>What the SCM answers about the background service, already parsed.</summary>
/// <param name="Exists">The service is registered at all.</param>
/// <param name="StateText">RUNNING, STOPPED, START_PENDING, … as sc.exe reports it.</param>
/// <param name="StartTypeText">AUTO_START, DEMAND_START, … from sc qc.</param>
/// <param name="IsRunning">The state the Start/Stop buttons care about.</param>
public sealed record IndexerServiceStatus(
    bool Exists, string StateText, string StartTypeText, bool IsRunning);

/// <summary>
/// The queue half of the console: counts per state, the newest failures, and
/// the audit trail. Null summary means "no queue yet" - the service has never
/// run on this machine.
/// </summary>
public sealed record IndexerQueueSnapshot(
    QueueSummary? Summary, IReadOnlyList<QueueEvent> Events, string? Note);

/// <summary>
/// Everything the console reads without elevating: the SCM's view of the
/// service, the durable queue, and the schedule the service is configured with.
/// Returns null (or a noted snapshot) when this is not a Windows machine or the
/// pieces do not exist yet - reading is never allowed to throw the window away.
/// </summary>
public interface IIndexerServiceMonitor
{
    IndexerServiceStatus? QueryStatus();
    IndexerQueueSnapshot ReadQueue();
    ScheduleSettings? ReadSchedule();
}

/// <summary>
/// What <c>--apply-config</c> reported in its result file - the verified half
/// of the save. Null from the launcher means the elevated process never
/// returned a result (could not start, or was killed).
/// </summary>
public sealed record ApplyConfigOutcome(int ExitCode, bool Restarted, IReadOnlyList<string> Messages)
{
    public bool Applied => ExitCode == 0;

    /// <summary>The messages joined into one sentence, for the note the user reads.</summary>
    public string Summary => Messages.Count == 0
        ? "(the command reported nothing)"
        : string.Join("; ", Messages);
}

/// <summary>
/// The console's elevated half. The indexer executable is launched with
/// <c>UseShellExecute</c> so its control verbs (<c>--install</c>,
/// <c>--uninstall</c>; start/stop go through sc.exe) raise UAC, and the one
/// write path - <c>--apply-config</c> - is awaited and its result file read,
/// so a save either visibly happened or visibly did not.
/// </summary>
public interface IIndexerProcessLauncher
{
    /// <summary>True when the indexer executable was found beside the app.</summary>
    bool IndexerExecutablePresent { get; }

    /// <summary>Writes the partial JSON config to a temp file; null when it could not.</summary>
    string? WriteConfigPatch(JsonObject patch);

    /// <summary>
    /// Runs <c>--apply-config</c> elevated, waits for it to finish (the UAC
    /// prompt may wait as long as the human needs), and returns what its result
    /// file reported. Null when the elevated process could not start at all.
    /// </summary>
    Task<ApplyConfigOutcome?> ApplyConfigAsync(string patchPath, CancellationToken ct = default);

    /// <summary>Launches the indexer executable with these arguments, elevated and unawaited. False when it could not start.</summary>
    bool LaunchElevated(params string[] arguments);

    /// <summary>Starts or stops the service through sc.exe, elevated and unawaited. False when it could not start.</summary>
    bool LaunchServiceControl(string scVerb);
}
