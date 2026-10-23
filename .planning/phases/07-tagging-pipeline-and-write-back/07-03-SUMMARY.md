# 07-03 SUMMARY — One failure table, and a real mid-batch kill

**Status:** Complete
**Requirements:** TAG-05, TAG-06, TAG-07, TAG-08

## What was built

| File | Role |
|------|------|
| [JobOutcomeClassifier.cs](../../../src/Synapic.Indexer/Pipeline/JobOutcomeClassifier.cs) | Spec §8 as one table: `Transient(60 s)`, `PauseRun(5 min)`, `Failed(permanent)`, with the attempt cap |
| [ISidecarRecovery.cs](../../../src/Synapic.Indexer/Pipeline/ISidecarRecovery.cs) | Restart with 1 s / 2 s / 4 s backoff; the delay is injected so tests do not sleep |
| [BatchExecutor.cs](../../../src/Synapic.Indexer/Pipeline/BatchExecutor.cs) | Uses only the classifier; pauses the run, requeues after recovery, reschedules at 5 min when restart fails |
| [JobOutcomeClassifierTests.cs](../../../tests/Synapic.Indexer.Tests/JobOutcomeClassifierTests.cs) | Every row of the table, method by method |
| [ResilienceTests.cs](../../../tests/Synapic.Indexer.Tests/ResilienceTests.cs) | The five §8 scenarios through the executor |
| [RealSidecarKillTests.cs](../../../tests/Synapic.Indexer.Tests/RealSidecarKillTests.cs) | Opt-in: the production sidecar process killed during a real `/tag` |

## Two corrections the tests forced

1. **The sidecar's process state outranks the exception shape.** The first classifier ordered
   `HttpRequestException`/`TaskCanceledException` into `PauseRun`, so a killed local sidecar would have paused
   the run for five minutes instead of restarting it. `!sidecarRunning` is now checked first, and timeout-shaped
   failures only pause when the item is a catalog item (`DaminionId` set or a Daminion type).
2. **A catalog download failure is an outage, not a lock.** `DaminionApiClient` returns `null` for a transport
   failure and the shared handler throws `IOException("Could not download image…")`. For a catalog item that is
   the spec's "server unreachable" row, so the classifier maps it to `PauseRun`; the generic IO branch remains
   transient for local files.

## Evidence — five scenarios

```
dotnet test tests/Synapic.Indexer.Tests -c Release --nologo --filter "FullyQualifiedName~ResilienceTests|FullyQualifiedName~JobOutcomeClassifierTests"
Passed!  - Failed: 0, Passed: 20, Skipped: 0, Total: 20
```

- (a) crash between items, recovery succeeds → both `Completed`, the affected row `attempts == 2`, one recovery call.
- (b) recovery fails → both `TransientFailure` with a 5-minute due time; `IsPaused` false (a stuck sidecar is not a catalog pause).
- (c) file held `FileShare.None` → `TransientFailure` due in 60 s while the other file `Completed`; release + clock +61 s → completed, attempts 2.
- (d) corrupt JPEG → `InferenceApiException(422, "Failed to decode image…")` → `Failed` on the first attempt, error stored, the other file `Completed`.
- (e) real `DaminionApiClient` on a closed port → all rows `Pending`, `IsPaused`, no claim inside the window; after 5 minutes a batch runs again (attempts 1 → 2) and pauses again. Nothing is dropped.

## Evidence — the real mid-batch kill

`RealSidecarKillTests` launches the built `artifacts/win-x64/synapic-inference.exe` through the production
`InferenceSidecarService` + `SidecarSupervisor`, queues six fixture JPEGs, and kills the process **during the
first in-flight `/tag`** (a pass-through `IInferenceSidecar` reports when the request starts, so the crash
cannot land between items). It is opt-in because it loads the model twice.

```
SYNAPIC_TEST_REAL_KILL=1 dotnet test tests/Synapic.Indexer.Tests -c Release --nologo \
  --filter "FullyQualifiedName~RealSidecarKillTests"
Passed Synapic.Indexer.Tests.RealSidecarKillTests.Killing_the_sidecar_mid_batch_restarts_it_and_the_batch_finishes [2 m 21 s]
killed pid=45304; final: pending=0 completed=6 transient=0 failed=0 running=True processId=44804 status=Ready
Test Run Successful. Total tests: 1, Passed: 1
```

Six rows completed, at least one retried (`attempts >= 2`), the new pid differs from the killed one, and every
file has a category and keywords read back through `MetadataWriterService`. Without
`SYNAPIC_TEST_REAL_KILL=1` (or without the bundle/model) the test reports **skipped with its reason**, not a pass.

**Scope note:** the CI-default resilience evidence uses a canned sidecar; only this opt-in test exercises the
real process, and it needs a built bundle plus cached `LFM2.5-VL-1.6B` weights.
