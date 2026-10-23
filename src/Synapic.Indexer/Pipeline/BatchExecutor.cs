using System.Diagnostics;
using Synapic.Core.Services;
using Synapic.Core.Services.Processing;
using Synapic.Indexer.Configuration;
using Synapic.Indexer.Detection;
using Synapic.Indexer.Hosting;
using Synapic.Indexer.State;

namespace Synapic.Indexer.Pipeline;

/// <summary>
/// Turns claimed queue rows into one tagging batch through the shared kernel
/// (D-11-02, D-11-21). The executor owns queue semantics — claiming, mapping
/// outcomes, retries, pause — and nothing else: it never calls <c>/tag</c> or
/// writes metadata itself, so the indexer and the desktop cannot diverge.
/// </summary>
public sealed class BatchExecutor
{
    /// <summary>Attempts a row gets before an unknown failure becomes permanent.</summary>
    private const int MaxClaimBatch = 64;

    private readonly IInferenceSidecar _sidecar;
    private readonly ISidecarSupervisor _supervisor;
    private readonly IndexingQueueRepository _repository;
    private readonly IndexerConfig _config;
    private readonly IMetadataWriter _writer;
    private readonly IFileHasher _hasher;
    private readonly PreserveExistingTagsPolicy _policy;
    private readonly ISidecarRecovery _recovery;
    private readonly TimeProvider _clock;
    private readonly Func<DatasourceSelection>? _daminionSelection;

    private DateTimeOffset _pauseUntil = DateTimeOffset.MinValue;
    private DateTimeOffset? _pausedAt;
    private long _batchId;

    public BatchExecutor(
        IInferenceSidecar sidecar,
        ISidecarSupervisor supervisor,
        IndexingQueueRepository repository,
        IndexerConfig config,
        IMetadataWriter writer,
        IFileHasher hasher,
        PreserveExistingTagsPolicy policy,
        ISidecarRecovery? recovery = null,
        TimeProvider? clock = null,
        Func<DatasourceSelection>? daminionSelection = null)
    {
        _sidecar = sidecar;
        _supervisor = supervisor;
        _repository = repository;
        _config = config;
        _writer = writer;
        _hasher = hasher;
        _policy = policy;
        _recovery = recovery ?? new SidecarRecovery(supervisor);
        _clock = clock ?? TimeProvider.System;
        _daminionSelection = daminionSelection;
    }

    /// <summary>D-11-21: enough rows that the kernel is never starved, few enough that a crash loses little.</summary>
    public int BatchSize => Math.Min(MaxClaimBatch, Math.Max(1, _config.Service.MaxConcurrentItems) * 8);

    public bool IsPaused => _clock.GetUtcNow() < _pauseUntil;

    /// <summary>How many times this run has paused for an unreachable catalog.</summary>
    public int PauseCount { get; private set; }

    /// <summary>
    /// How many times a paused run has resumed. A resume is observed by the next
    /// batch that runs after the pause window, not by the clock alone: the run
    /// is only "back" when it is working again.
    /// </summary>
    public int ResumeCount { get; private set; }

    /// <summary>
    /// Claim one batch and process it. Returns the number of rows the batch
    /// finished with (0 when there was nothing to do or the run is paused).
    /// </summary>
    public async Task<int> ExecuteOnceAsync(CancellationToken ct) => await ExecuteOnceAsync(ct, retried: false);

    private async Task<int> ExecuteOnceAsync(CancellationToken ct, bool retried)
    {
        if (IsPaused) return 0;

        if (_pausedAt is { } pausedAt)
        {
            var paused = _clock.GetUtcNow() - pausedAt;
            _pausedAt = null;
            ResumeCount++;
            SynapicLog.Info(nameof(BatchExecutor),
                $"The catalog is available again; the run resumes after being paused " +
                $"{paused.TotalMinutes:0.0} minutes.");
        }

        var claimed = await _repository.ClaimBatchAsync(BatchSize, ct).ConfigureAwait(false);
        if (claimed.Count == 0) return 0;

        // The idle timer may have stopped the sidecar since the last batch; make
        // sure it is up before claiming work is "running". A failure here is a
        // reason to wait, not a reason to lose the rows.
        try
        {
            await _supervisor.EnsureReadyAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            foreach (var job in claimed)
                await _repository.ReturnToPendingAsync(job.Id, "batch cancelled", CancellationToken.None)
                    .ConfigureAwait(false);
            throw;
        }
        catch (Exception e)
        {
            foreach (var job in claimed)
            {
                await _repository.MarkTransientAsync(job.Id, $"the inference sidecar is unavailable: {e.Message}",
                    _clock.GetUtcNow().UtcDateTime.Add(JobOutcomeClassifier.CatalogPauseDelay), ct)
                    .ConfigureAwait(false);
            }

            SynapicLog.Error(nameof(BatchExecutor),
                $"The inference sidecar could not be started; {claimed.Count} job(s) retry in " +
                $"{JobOutcomeClassifier.CatalogPauseDelay.TotalMinutes:0} minutes.", e);
            return 0;
        }

        var batchId = Interlocked.Increment(ref _batchId);
        BatchOutcome outcome;
        try
        {
            outcome = await RunBatchAsync(batchId, claimed, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Cancellation is a stop, not a failure: hand the rows back so the
            // next run (or the recovery pass) sees them as Pending, never InFlight.
            foreach (var job in claimed)
                await _repository.ReturnToPendingAsync(job.Id, "batch cancelled", CancellationToken.None)
                    .ConfigureAwait(false);
            throw;
        }

        if (outcome.PauseIds.Count > 0)
        {
            _pauseUntil = _clock.GetUtcNow() + JobOutcomeClassifier.CatalogPauseDelay;
            _pausedAt = _clock.GetUtcNow();
            PauseCount++;
            foreach (var id in outcome.PauseIds)
                await _repository.ReturnToPendingAsync(id, "catalog unavailable — run paused", ct)
                    .ConfigureAwait(false);
            SynapicLog.Warning(nameof(BatchExecutor),
                $"Catalog unavailable; the run is paused until {_pauseUntil:u}.");
        }

        if (retried || outcome.TransientIds.Count == 0 || _sidecar.IsRunning) return outcome.Finished;

        // D-11-25: the sidecar died mid-batch. Restart it, put the affected rows
        // back, and give the batch one more chance; otherwise wait longer.
        if (await _recovery.TryRecoverAsync(ct).ConfigureAwait(false))
        {
            foreach (var id in outcome.TransientIds)
                await _repository.ReturnToPendingAsync(id, "sidecar recovered; retrying", ct).ConfigureAwait(false);
            return outcome.Finished + await ExecuteOnceAsync(ct, retried: true).ConfigureAwait(false);
        }

        foreach (var id in outcome.TransientIds)
            await _repository.RescheduleRetryAsync(
                id, _clock.GetUtcNow().UtcDateTime.Add(JobOutcomeClassifier.CatalogPauseDelay),
                "the inference sidecar could not be restarted", ct).ConfigureAwait(false);
        SynapicLog.Error(nameof(BatchExecutor),
            $"The inference sidecar did not restart; {outcome.TransientIds.Count} item(s) retry in " +
            $"{JobOutcomeClassifier.CatalogPauseDelay.TotalMinutes:0} minutes.");
        return outcome.Finished;
    }

    private async Task<BatchOutcome> RunBatchAsync(
        long batchId, IReadOnlyList<JobRecord> claimed, CancellationToken ct)
    {
        var outcome = new BatchOutcome();
        var isDaminion = claimed[0].DaminionId is not null;
        if (claimed.Any(job => (job.DaminionId is not null) != isDaminion))
        {
            // Should be impossible: keys are prefixed by source. Fail loudly
            // rather than send a local path to a catalog batch.
            foreach (var job in claimed)
                await _repository.MarkFailedAsync(job.Id, "batch mixed local and catalog items", ct)
                    .ConfigureAwait(false);
            throw new InvalidOperationException("A batch mixed local and Daminion jobs.");
        }

        _policy.ClearCache();
        var toRun = new List<JobRecord>(claimed.Count);

        foreach (var job in claimed)
        {
            ct.ThrowIfCancellationRequested();

            if (!isDaminion && !_config.Tagging.OverwriteExistingTags && job.Path is { } path)
            {
                var existing = await _policy.InspectAsync(path, ct).ConfigureAwait(false);
                if (existing.FullyPopulated)
                {
                    await _repository.MarkCompletedAsync(job.Id, "existing tags preserved", ct).ConfigureAwait(false);
                    outcome.Preserved++;
                    continue;
                }
            }

            toRun.Add(job);
        }

        if (toRun.Count > 0)
        {
            // Claimed rows are InFlight before this call, so the idle timer sees
            // work for the whole batch (phase 6's QueueSidecarActivity).
            var results = new List<ProcessItemResult>();
            var errors = new List<WorkflowItemError>();
            var started = Stopwatch.StartNew();
            var datasource = isDaminion
                ? _daminionSelection?.Invoke()
                  ?? throw new InvalidOperationException("A Daminion batch was claimed but no catalog is configured.")
                : new DatasourceSelection { IsDaminion = false, Scope = "all" };

            var orchestrator = new ProcessingOrchestrator(
                _sidecar, _writer, _config.Service.MaxConcurrentItems, _policy);

            await orchestrator.RunItemsAsync(
                datasource,
                TagRequestFactory.Create(_config.Engine),
                toRun.Select(ToWorkItem).ToList(),
                new Progress<ProcessProgress>(_ => { }),
                LogKernelAsync,
                ct,
                results,
                pause: null,
                TagRequestFactory.Fields(_config.Tagging),
                errors).ConfigureAwait(false);

            foreach (var job in toRun)
            {
                ct.ThrowIfCancellationRequested();
                await MapAsync(job, results, errors, outcome, ct).ConfigureAwait(false);
            }

            started.Stop();
            SynapicLog.Info(nameof(BatchExecutor),
                $"batch {batchId}: claimed={claimed.Count} completed={outcome.Completed} " +
                $"transient={outcome.Transient} failed={outcome.Failed} preserved={outcome.Preserved} " +
                $"elapsed={started.Elapsed.TotalSeconds:0.0}s");
        }
        else
        {
            SynapicLog.Info(nameof(BatchExecutor),
                $"batch {batchId}: claimed={claimed.Count} completed=0 transient=0 failed=0 " +
                $"preserved={outcome.Preserved} (all tags already present)");
        }

        return outcome;
    }

    private async Task MapAsync(
        JobRecord job,
        IReadOnlyList<ProcessItemResult> results,
        IReadOnlyList<WorkflowItemError> errors,
        BatchOutcome outcome,
        CancellationToken ct)
    {
        var result = FindResult(job, results);
        var error = FindError(job, errors);

        if (result is not null && ProcessStatus.IsSuccess(result.Status))
        {
            await CompleteAsync(job, ct).ConfigureAwait(false);
            outcome.Completed++;
            return;
        }

        if (result is not null && ProcessStatus.NeedsRetry(result.Status))
        {
            await TransientAsync(job, "the metadata write did not land", JobOutcomeClassifier.LockRetryDelay,
                outcome, ct).ConfigureAwait(false);
            return;
        }

        var disposition = JobOutcomeClassifier.Classify(error, job.Attempts, _sidecar.IsRunning);
        switch (disposition.Kind)
        {
            case JobDispositionKind.Completed:
                await CompleteAsync(job, ct).ConfigureAwait(false);
                outcome.Completed++;
                break;

            case JobDispositionKind.Transient:
                await TransientAsync(job, disposition.Reason, disposition.Delay, outcome, ct).ConfigureAwait(false);
                break;

            case JobDispositionKind.PauseRun:
                outcome.PauseIds.Add(job.Id);
                SynapicLog.Warning(nameof(BatchExecutor),
                    $"job {job.Id} ({job.Key}) paused: {disposition.Reason}");
                break;

            case JobDispositionKind.Failed:
                await _repository.MarkFailedAsync(job.Id, disposition.Reason, ct).ConfigureAwait(false);
                outcome.Failed++;
                SynapicLog.Error(nameof(BatchExecutor),
                    $"job {job.Id} ({job.Key}) failed permanently after {job.Attempts} attempt(s): " +
                    disposition.Reason);
                break;
        }
    }

    private async Task TransientAsync(
        JobRecord job, string reason, TimeSpan delay, BatchOutcome outcome, CancellationToken ct)
    {
        await _repository.MarkTransientAsync(
            job.Id, reason, _clock.GetUtcNow().UtcDateTime.Add(delay), ct).ConfigureAwait(false);
        outcome.Transient++;
        outcome.TransientIds.Add(job.Id);
        SynapicLog.Warning(nameof(BatchExecutor),
            $"job {job.Id} ({job.Key}) retries in {delay.TotalSeconds:0}s after attempt {job.Attempts}: {reason}");
    }

    private async Task CompleteAsync(JobRecord job, CancellationToken ct)
    {
        await _repository.MarkCompletedAsync(job.Id, null, ct).ConfigureAwait(false);

        if (job.Path is not { } path) return;

        try
        {
            var info = new FileInfo(path);
            if (!info.Exists) return;

            // The write changed the file, so the fingerprint the scan compares
            // against must be the new one or the next scan re-enqueues it forever.
            var hash = await _hasher.TryHashAsync(path, ct).ConfigureAwait(false);
            if (hash is not null)
            {
                await _repository.UpdateFingerprintAsync(job.Id, info.Length, info.LastWriteTimeUtc, hash, ct)
                    .ConfigureAwait(false);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            SynapicLog.Warning(nameof(BatchExecutor),
                $"job {job.Id}: could not refresh the fingerprint of {path}: {e.Message}");
        }
    }

    private static ProcessItemResult? FindResult(JobRecord job, IReadOnlyList<ProcessItemResult> results)
    {
        if (job.DaminionId is { } id) return results.FirstOrDefault(r => r.DaminionId == id);
        return results.FirstOrDefault(r =>
            r.SourcePath is not null && string.Equals(r.SourcePath, job.Path, StringComparison.OrdinalIgnoreCase));
    }

    private static WorkflowItemError? FindError(JobRecord job, IReadOnlyList<WorkflowItemError> errors)
    {
        if (job.DaminionId is { } id) return errors.FirstOrDefault(e => e.DaminionId == id);
        return errors.FirstOrDefault(e =>
            e.LocalPath is not null && string.Equals(e.LocalPath, job.Path, StringComparison.OrdinalIgnoreCase));
    }

    private static ProcessWorkItem ToWorkItem(JobRecord job) => job.DaminionId is { } id
        ? new ProcessWorkItem { DaminionId = id, FileName = job.FileName }
        : new ProcessWorkItem { LocalPath = job.Path, FileName = job.FileName };

    private static Task LogKernelAsync(string message)
    {
        SynapicLog.Debug(nameof(BatchExecutor), message);
        return Task.CompletedTask;
    }

    private sealed class BatchOutcome
    {
        public int Completed { get; set; }
        public int Transient { get; set; }
        public int Failed { get; set; }
        public int Preserved { get; set; }
        public int Finished => Completed + Transient + Failed + Preserved;
        public List<long> TransientIds { get; } = new();
        public List<long> PauseIds { get; } = new();
    }
}
