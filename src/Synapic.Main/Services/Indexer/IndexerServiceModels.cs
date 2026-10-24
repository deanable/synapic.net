using System.Text.Json.Nodes;
using Synapic.Indexer.Contract;
using Synapic.Indexer.Contract.Configuration;

namespace Synapic.Main.Services.Indexer;

/// <summary>What the SCM answers about the background service, already parsed.</summary>
/// <param name="Exists">The service is registered at all.</param>
/// <param name="StateText">RUNNING, STOPPED, START_PENDING, … as sc.exe reports it.</param>
/// <param name="StartTypeText">AUTO_START, DEMAND_START, … from sc qc.</param>
/// <param name="IsRunning">The state the Start/Stop buttons care about.</param>
public sealed record IndexerServiceStatus(
    bool Exists, string StateText, string StartTypeText, bool IsRunning);

/// <summary>
/// The queue half of the Service tab: counts per state, the newest failures, and
/// the audit trail. Null summary means "no queue yet" — the service has never
/// run on this machine.
/// </summary>
public sealed record IndexerQueueSnapshot(
    QueueSummary? Summary, IReadOnlyList<QueueEvent> Events, string? Note);

/// <summary>
/// Everything the Service tab reads without elevating: the SCM's view of the
/// service, the durable queue, and the schedule the service is configured with.
/// Returns null (or a noted snapshot) when this is not a Windows machine or the
/// pieces do not exist yet — reading is never allowed to throw the tab away.
/// </summary>
public interface IIndexerServiceMonitor
{
    IndexerServiceStatus? QueryStatus();
    IndexerQueueSnapshot ReadQueue();
    ScheduleSettings? ReadSchedule();
}

/// <summary>
/// The Service tab's elevated half. The indexer executable is launched with
/// <c>UseShellExecute</c> so its control verbs (<c>--install</c>,
/// <c>--uninstall</c>, <c>start</c>/<c>stop</c> are sc.exe — see the launcher's
/// implementation) raise UAC, and the one write path is
/// <c>--apply-config &lt;file&gt;</c>: the patch is written to the user's temp
/// folder here and applied by the elevated command, which validates before it
/// saves and restarts the service if it was running.
/// </summary>
public interface IIndexerProcessLauncher
{
    /// <summary>True when the indexer executable was found beside the app.</summary>
    bool IndexerExecutablePresent { get; }

    /// <summary>Writes the partial JSON config to a temp file; null when it could not.</summary>
    string? WriteConfigPatch(JsonObject patch);

    /// <summary>Launches the indexer executable with these arguments, elevated. False when it could not start.</summary>
    bool LaunchElevated(params string[] arguments);

    /// <summary>Starts or stops the service through sc.exe, elevated. False when it could not start.</summary>
    bool LaunchServiceControl(string scVerb);
}
