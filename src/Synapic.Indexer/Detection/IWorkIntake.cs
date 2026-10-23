using Synapic.Core.Services;
using Synapic.Indexer.Pipeline;
using Synapic.Indexer.State;

namespace Synapic.Indexer.Detection;

/// <summary>
/// The single enqueue path. The interval scan, the watcher and the Daminion
/// query all hand their candidates to this, so the idempotency rules have exactly
/// one implementation (D-11-19).
/// </summary>
public interface IWorkIntake
{
    Task<IntakeResult> EnqueueAsync(IReadOnlyList<JobCandidate> candidates, CancellationToken ct = default);
}

/// <summary>What one detection pass found and did.</summary>
public sealed record SourceScanResult
{
    /// <summary>Supported, non-empty files or catalog items this pass looked at.</summary>
    public int Found { get; init; }

    public int Enqueued { get; init; }

    /// <summary>Found minus enqueued: already queued, unchanged, or deferred.</summary>
    public int Skipped { get; init; }

    /// <summary>Files or subtrees that could not be read. Never fatal.</summary>
    public int Errors { get; init; }

    /// <summary>The source itself could not be reached (server down); nothing was enqueued.</summary>
    public bool Unavailable { get; init; }

    public static SourceScanResult UnavailableResult(int errors = 0) =>
        new() { Unavailable = true, Errors = errors };
}

/// <summary>A detector the scheduler can run on its interval (local scan, catalog query).</summary>
public interface IWorkSource
{
    /// <summary>Short, stable name used in logs and <c>--status</c>: <c>local</c> or <c>daminion</c>.</summary>
    string Name { get; }

    Task<SourceScanResult> ScanAsync(CancellationToken ct);
}

/// <summary>The real intake: one repository call, one log line, one wake-up.</summary>
public sealed class WorkIntake : IWorkIntake
{
    private readonly IndexingQueueRepository _repository;
    private readonly IWorkSignal? _signal;

    public WorkIntake(IndexingQueueRepository repository, IWorkSignal? signal = null)
    {
        _repository = repository;
        _signal = signal;
    }

    public async Task<IntakeResult> EnqueueAsync(IReadOnlyList<JobCandidate> candidates, CancellationToken ct = default)
    {
        var result = await _repository.EnqueueAsync(candidates, ct).ConfigureAwait(false);

        if (result.Total == 0)
            SynapicLog.Debug(nameof(WorkIntake), "No work detected; nothing to enqueue.");
        else
            SynapicLog.Info(nameof(WorkIntake),
                $"Enqueued {result.Enqueued} of {result.Total} candidate(s); {result.Skipped} already queued or unchanged.");

        if (result.Enqueued > 0) _signal?.Set();
        return result;
    }
}
