# 07-01 SUMMARY — Batch execution through the shared kernel

**Status:** Complete
**Requirements:** TAG-01, TAG-04 (execution half)

## What was built

| File | Role |
|------|------|
| [WorkflowItemError.cs](../../../src/Synapic.Core/Services/Processing/WorkflowItemError.cs) | Per-item failure record: file name, local path, Daminion id, status, message, exception type |
| [WorkflowRunner.cs](../../../src/Synapic.Core/Services/Processing/WorkflowRunner.cs) | Optional `errors` collection filled in the per-item catch; thread-safe, counts unchanged |
| [ProcessingOrchestrator.cs](../../../src/Synapic.Core/Services/Processing/ProcessingOrchestrator.cs) | `errors` pass-through on `RunAsync`/`RunItemsAsync`; `ProcessItemResult.SourcePath` |
| [TagRequestFactory.cs](../../../src/Synapic.Indexer/Pipeline/TagRequestFactory.cs) | `EngineConfig` → the desktop's `TagRequest` shape; `TaggingSettings` → `TagFieldSelection` |
| [IWorkSignal.cs](../../../src/Synapic.Indexer/Pipeline/IWorkSignal.cs) | Signal + 30 s poll fallback so a missed enqueue cannot strand work |
| [BatchExecutor.cs](../../../src/Synapic.Indexer/Pipeline/BatchExecutor.cs) | Claim (`min(64, concurrency×8)`), one datasource + one template, kernel call, outcome → queue state, one summary line per batch |
| [IndexerWorkerService.cs](../../../src/Synapic.Indexer/Hosting/IndexerWorkerService.cs) | `wait for signal (30 s) → ExecuteOnceAsync → repeat`, failing batches logged and the loop kept alive |
| [WorkflowErrorSinkTests.cs](../../../tests/Synapic.Core.Tests/WorkflowErrorSinkTests.cs) | Error sink + `SourcePath` facts |
| [BatchExecutorTests.cs](../../../tests/Synapic.Indexer.Tests/BatchExecutorTests.cs) | Six executor facts over a real queue, real writer and a canned sidecar |

Every claimed row is written back before the batch returns, on cancellation too; `InFlight` is set by the claim
(before the kernel call), so the idle timer never sees an idle queue mid-batch.

## Evidence

```
dotnet test tests/Synapic.Core.Tests -c Release --nologo --filter "FullyQualifiedName~WorkflowErrorSinkTests"
Passed!  - Failed: 0, Passed: 4, Skipped: 0, Total: 4

dotnet test tests/Synapic.Indexer.Tests -c Release --nologo --filter "FullyQualifiedName~BatchExecutorTests"
Passed!  - Failed: 0, Passed: 6, Skipped: 0, Total: 6
```

- Two local images → `Completed`, attempts 1, tags read back from the files.
- A failing writer → `TransientFailure` due in 55–65 s.
- A `FileShare`-held file → transient, not corrupt.
- Four images at `maxConcurrentItems: 2` → observed overlap exactly 2 (measured in the fake sidecar), 4 tag calls.
- Empty queue → 0, no summary line.
- Cancellation mid-batch → both rows `Pending`, no `InFlight`, exception propagates.
