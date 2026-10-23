# 06-01 SUMMARY — SQLite job store

**Status:** Complete
**Requirements:** SCAN-05, SCAN-06, SCAN-07

## What was built

| File | Role |
|------|------|
| [JobState.cs](../../../src/Synapic.Indexer/State/JobState.cs) | The five states and their storage strings |
| [JobStateMachine.cs](../../../src/Synapic.Indexer/State/JobStateMachine.cs) | The permitted-transition table and `InvalidJobTransitionException` |
| [JobRecord.cs](../../../src/Synapic.Indexer/State/JobRecord.cs) | Immutable row plus the fingerprint rules (`MatchesSizeAndTime`, `MatchesFingerprint`) |
| [JobCandidate.cs](../../../src/Synapic.Indexer/State/JobCandidate.cs) | The detector→queue shape, `IntakeResult`, `QueueActivity` |
| [QueueSchema.cs](../../../src/Synapic.Indexer/State/QueueSchema.cs) | Schema v1 DDL, WAL, idempotent creation, read-only probe |
| [QueueSummary.cs](../../../src/Synapic.Indexer/State/QueueSummary.cs) | Per-state counts and the five newest failures |
| [IndexingQueueRepository.cs](../../../src/Synapic.Indexer/State/IndexingQueueRepository.cs) | The only SQL that reads or writes jobs |
| [IndexerPaths.cs](../../../src/Synapic.Indexer/Hosting/IndexerPaths.cs) | `QueueFilePath` = `state\indexer.db` |

One new package: `Microsoft.Data.Sqlite` 10.0.12 (pinned). WAL, `busy_timeout=5000`, one pooled
connection per operation, writes behind a `SemaphoreSlim`, claims inside `BEGIN IMMEDIATE`.

## Two deviations from the plan, both forced by tests

1. **Completed→Pending was missing from the transition table.** The CONTEXT's enqueue matrix requires
   "Completed changed → Pending", but its state list did not include the move, so the first re-enqueue of a
   changed file threw `InvalidJobTransitionException`. The transition is now listed, and `EnqueueAsync` is its
   only caller (it checks the fingerprint first).
2. **A due transient retry was unreachable.** `ClaimBatchAsync` selected only `Pending`, and a
   `TransientFailure` row has no transition to `InFlight`. Claiming now promotes transient rows whose
   `next_attempt_at` is due (event `"retry due"`) inside the same immediate transaction before selecting.
   Phase 7 therefore only ever sees Pending rows, as its contract says.

## Evidence

```
dotnet test tests/Synapic.Indexer.Tests -c Release --nologo \
  --filter "FullyQualifiedName~JobStateMachineTests|FullyQualifiedName~IndexingQueueRepositoryTests"
Passed! - Failed: 0, Passed: 45, Skipped: 0, Total: 45, Duration: 516 ms
```

Covered at this seam: every permitted transition and every forbidden pair (including
Completed→Completed); insert + event; no-op for Pending/InFlight; case-insensitive key collision;
Completed unchanged with no hash; Completed moved-mtime/same-hash refresh; Completed changed → Pending with
attempts reset; Failed unchanged never retried and Failed changed requeued; transient deferral until due;
oldest-first claiming; `job_events` sequence `Pending:detected → InFlight:claimed → Completed:verified`;
recovery of two InFlight rows with `service restart`; persistence across a second repository instance;
idempotent `InitializeAsync`; activity true only with open work; summary counts and a five-row failure cap;
eight parallel enqueues of one key producing one row; read-only open of a missing file returning null.

## Known limits

- `--status`'s read-only path cannot read a database whose `-wal` file exists while no process holds it
  (SQLite needs the `-shm`). Phase 6-04 reports that as a read error rather than pretending the queue is empty.
- No retention/pruning: `job_events` grows until a later milestone adds it (already deferred in 06-CONTEXT).
