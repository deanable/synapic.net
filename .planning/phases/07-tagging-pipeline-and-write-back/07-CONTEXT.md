# Phase 7: Tagging pipeline and write-back - Context

**Gathered:** 2026-10-22
**Status:** Ready for planning
**Source:** `docs/windows-indexer.md` §5.2 (engine/tagging), §6 (execute batch), §8 (failure table)

<domain>
## Phase Boundary

Turn Pending rows into tagged images. Build the inference request from configuration, run batches through the
shared `ProcessingOrchestrator`, translate per-item outcomes into queue states, apply the overwrite policy, and
survive the five failure modes in the spec. Observability is limited to logs, queue states and `--status`; no UI.

</domain>

<decisions>
## Implementation Decisions

### Execution is the shared kernel
- **D-11-02 (locked):** `ProcessingOrchestrator.RunItemsAsync(ds, template, items, progress, log, ct, results,
  pause, tagFields)` is the only tagging implementation. The indexer constructs `DatasourceSelection` per batch
  (local: `IsDaminion=false`, one item path each; Daminion: `IsDaminion=true` + client) and one `TagRequest`
  template from `engine` (`ModelId`, `ConfidenceThreshold`, `ProbabilityMode`, `ProbabilityThreshold`,
  `UserPrompt`/`SystemPrompt` when configured) — the same shape the desktop builds.
- **D-11-21:** Batch size is `min(64, max(1, maxConcurrentItems) * 8)` claimed per cycle; the kernel's own
  `maxDegreeOfParallelism` stays `maxConcurrentItems`. A batch never mixes local and Daminion items (different
  `DatasourceSelection`), and queue order is FIFO by id.

### Outcome mapping (the gap this phase must close)
- `TaggingItemHandler` returns `WorkflowItemOutcome(true, status)` even when the metadata write failed
  (`ProcessStatus.WriteFailed`), and `WorkflowRunner` catches per-item exceptions and only logs them. The queue
  therefore cannot be updated from `WorkflowRunSummary` alone.
- **D-11-22:** `RunItemsAsync`/`RunAsync` gain an optional `ICollection<WorkflowItemError>? errors` parameter
  (`WorkflowItemError(string FileName, int? DaminionId, string Status, string Message, string ExceptionType)`),
  filled in the runner's catch block. Mapping: `ProcessStatus.IsSuccess(status)` → `Completed`;
  `ProcessStatus.NeedsRetry(status)` (i.e. WriteFailed) → `TransientFailure` with a 60 s due time; an item with
  an error and no result → classify by exception (see D-11-26); an item with no result and no error →
  `TransientFailure` and a Warning (the runner must never drop an item silently).

### Write-back policy (spec §5.2 `tagging`)
- **D-11-23:** Write-back policy is a seam in the kernel, not a fork of it: `ProcessingOrchestrator` accepts an
  optional `ITagWritePolicy` (default no-op, so the desktop is unchanged). Before writing a tagged item the
  handler calls `AdjustAsync(item, tags, ct)`.
  - The indexer's `PreserveExistingTagsPolicy` reads current tags (`IMetadataWriter.ReadAsync`, or the Daminion
    client's verification read when available) and, when `overwriteExistingTags == false`, returns a `TagResult`
    with already-populated fields **left out** (null category/description, empty keywords) — `MetadataPatch.FromTags`
    treats those as `Unchanged`, so only the empty selected fields are written.
  - The executor additionally pre-checks: when `overwriteExistingTags == false` and **all** selected fields are
    already populated, the job is marked `Completed` with detail `"existing tags preserved"` and no `/tag` call
    is made. Same read, so the decision cannot disagree with the write policy.
  - `overwriteExistingTags == true` calls `/tag` and writes every selected field.
- **Known limitation, stated not hidden:** local keyword writes are merge-only (`MetadataPatch.FromTags` maps
  keywords to `Merge`, and the writer rejects Merge for category/description), and `DaminionApiClient`
  `UpdateItemMetadataAsync` only adds values (`Remove = false`). `overwriteExistingTags: true` therefore means
  "write the model's values", not "delete values already on the image". Removing tags is out of scope for v1.1;
  a test pins the merge behavior so it cannot be mistaken for replacement later.
- Field selection comes from `tagging.writeCategory/writeKeywords/writeDescription` → `TagFieldSelection`.
  At least one is true (validator enforces it).

### Resilience (spec §8)
- **D-11-24:** Batch exceptions and per-item errors are classified once, in `JobOutcomeClassifier`:
  `IOException`/`UnauthorizedAccessException` naming a sharing/lock condition → `TransientFailure`, retry after
  60 s; `DaminionNetworkException`/timeout → pause the run, requeue claimed items `Pending`, retry the batch every
  5 minutes until the server answers; `DaminionAuthenticationException` → `Failed` for the batch + Fatal log
  (a wrong password is an operator problem, not a retry loop); decoder/unsupported/corrupt image
  (`ArgumentException`, `NotSupportedException`, image-decode errors) → `Failed` permanently; anything else →
  `TransientFailure` once, then `Failed` when `attempts >= 3`.
- **D-11-25:** Sidecar crash: a batch that ends with non-success items while `IInferenceSidecar.IsRunning` is
  false triggers `ISidecarSupervisor.EnsureReadyAsync` with backoff 1 s / 2 s / 4 s (max 3). If it recovers, those
  items go back to `Pending` (immediate) and the batch is retried once; if not, they become
  `TransientFailure` with a 5-minute due time, and the executor keeps the service alive (the next interval
  retries). `attempts` bounds the loop.
- **D-11-26:** After `MarkCompleted`, the file's stored fingerprint (size/mtime/hash) is refreshed from disk so the
  next scan does not re-enqueue it for having been written to.

### Observability
- **D-11-27:** Every batch logs one Information line: claimed/completed/transient/failed/preserved counts and
  elapsed time. Per item: Debug on success, Warning on transient, Error on permanent, with the job key and
  attempt number — never the image content and never the Daminion password.
- `--status` (phase 6) remains the only queue view; no new command unless the gate needs one.

</decisions>

<canonical_refs>
## Canonical References

**Downstream agents MUST read these before planning or implementing.**

### Specification
- `docs/windows-indexer.md` §5.2 — engine/tagging config keys and defaults
- `docs/windows-indexer.md` §6 — execute-batch flow (send /tag max 4 parallel, write metadata, mark completed)
- `docs/windows-indexer.md` §8 — the five failure modes and their required behavior
- `docs/windows-indexer.md` §9.3 — parity acceptance

### Code
- `src/Synapic.Core/Services/Processing/ProcessingOrchestrator.cs` — `RunAsync`/`RunItemsAsync`, `TaggingItemHandler`,
  `DatasourceSelection`, `ProcessWorkItem`, `ProcessItemResult`
- `src/Synapic.Core/Services/Processing/WorkflowRunner.cs` — L640–690 per-item catch, `WorkflowItemOutcome`,
  `WorkflowRunSummary`
- `src/Synapic.Core/Services/Processing/ProcessStatus.cs` — `IsSuccess`/`NeedsRetry` (the mapping authority)
- `src/Synapic.Core/Services/MetadataWriterService.cs` — `ReadAsync` + `WriteAsync(TagResult)` (merge semantics)
- `src/Synapic.Core/Services/InferenceSidecarService.cs` — `IsRunning`, `StatusChanged`, `TagAsync`
- `src/Synapic.Indexer/Hosting/ISidecarSupervisor.cs` (phase 5)
- `src/Synapic.Indexer/State/IndexingQueueRepository.cs` (phase 6)

</canonical_refs>

<code_context>
## Existing Code Insights

### Reusable Assets
- `ProcessStatus.IsSuccess`/`NeedsRetry` already encode what "done" means — do not re-derive from strings.
- `IMetadataWriter.ReadAsync` exists, so the overwrite policy needs no new metadata API.
- `ProcessingOrchestrator` already writes metadata per item (local or Daminion check-in); the executor does not
  write anything itself.
- `IProgress<ProcessProgress>` can feed the batch log line; a no-op or logging progress is enough (no UI).

### Established Patterns
- Metadata writes are staged → validated → committed; a failure leaves the prior artifact intact. Rely on that
  instead of adding rollback.
- The sidecar's status events are the codebase's way to know a process died; polling HTTP with a timeout would
  duplicate it.
- Tests use fake `IInferenceSidecar` implementations with canned `TagResponse`s — parity should use that, not a
  real model, except the phase 8 acceptance run.

### Integration Points
- `ScanScheduler`/`FolderWatcher` (phase 6) fill the queue; the executor waits on a work signal after intake and
  also polls at the interval, so a missed signal cannot strand work.
- `QueueSidecarActivity` (phase 6) must see executor work: mark `InFlight` before the kernel call so the idle
  timer never stops the sidecar mid-batch.

</code_context>

<deferred>
## Deferred Ideas

- Batch-level parallelism across multiple sidecars (single sidecar in v1.1)
- Tag verification after Daminion check-in (`VerifyItemMetadataAsync`) as a nightly pass — not required by the spec
- Retry/backoff configurability (the 60 s / 5 min / 3 attempts values are spec constants)
- A dead-letter export command; `--status` + logs are enough for v1.1
- Dedup/upscale background execution (BG-01/BG-02)

</deferred>

---

*Phase: 07-tagging-pipeline-and-write-back*
*Context gathered: 2026-10-22*
