# 07-04 SUMMARY — Parity, failure visibility and the phase gate

**Status:** Complete
**Requirements:** TAG-01..TAG-08 (gate record), TAG-04 (parity)

## What was built

| File | Role |
|------|------|
| [ParityTests.cs](../../../tests/Synapic.Indexer.Tests/ParityTests.cs) | The same JPEG through the desktop's `RunAsync` and the indexer's `BatchExecutor`, compared by reading the written XMP back |
| [StatusCommand.cs](../../../src/Synapic.Indexer/Commands/StatusCommand.cs) | `--status --failed`: up to 50 rows with id, key, attempts and a 160-character one-line error |
| [ServiceCommandLine.cs](../../../src/Synapic.Indexer/Install/ServiceCommandLine.cs) | `--failed` parsed as a view of `--status`; rejected on its own (exit 64), never a silent fallthrough |
| [IndexingQueueRepository.cs](../../../src/Synapic.Indexer/State/IndexingQueueRepository.cs) | `failureLimit` on `ReadSummaryAsync`, `attempts` on `QueueFailure` |
| [BatchExecutor.cs](../../../src/Synapic.Indexer/Pipeline/BatchExecutor.cs) | Stable per-process batch id on the summary line |

## Evidence — parity

```
dotnet test tests/Synapic.Indexer.Tests -c Release --nologo --filter "FullyQualifiedName~ParityTests"
Passed!  - Failed: 0, Passed: 2, Skipped: 0, Total: 2
```

- Same template, same field selection, overwrite on, fresh copies → identical category, description and keyword
  set on both files; the desktop result is `ProcessStatus.Success` and the indexer row is `Completed`.
- `writeDescription=false` → neither file gains a description, and the rest still matches.
- The sidecar is deterministic, so a failure means the pipeline differs, not the model.

## Evidence — failure visibility and the CLI

```
dotnet test tests/Synapic.Indexer.Tests -c Release --nologo --filter "FullyQualifiedName~StatusCommandTests|FullyQualifiedName~ServiceCommandLineTests"
Passed!  - Failed: 0, Passed: 24, Skipped: 0, Total: 24

dotnet run --project src/Synapic.Indexer -c Release -- --status --failed     -> no queue yet, exit 0
dotnet run --project src/Synapic.Indexer -c Release -- --failed             -> exit 64, "--failed can only be used with --status."
```

The failure table itself is asserted in `StatusCommandTests`: 11 seeded failures each print
`id  key  attempts=N  error`, the view returns more than the summary's five-row limit, `Failures: none` when
nothing failed, and the database mtime and directory contents are unchanged (still read-only).

## Phase gate — ROADMAP Phase 7 success criteria

### 1. A batch of local files is tagged and only the configured fields are written ✓
```
BatchExecutorTests                      6 passed
WriteBackPolicyTests                    5 passed   (A_disabled_field_is_never_written…)
ParityTests                             2 passed   (Field_selection_applies_identically…)
```
Tags are read back from the files, not from the in-memory results.

### 2. `overwriteExistingTags:false` preserves; `true` writes ✓
```
WriteBackPolicyTests  5 passed
```
Fully tagged → 0 sidecar calls, `Completed`, `job_events` detail `existing tags preserved`. Partially tagged →
only the empty selected fields written (D-11-23). Overwrite on → every selected field written; keywords still
merge (`old` survives), documented and pinned as a limitation, not passed off as replacement.

### 3. Killing `synapic-inference` mid-batch restarts it and the batch finishes; locked file retried after 60 s; unreadable image `Failed` with the rest completing ✓
```
ResilienceTests  5 passed
RealSidecarKillTests  1 passed (real bundle + model; opt-in)
killed pid=45304; final: pending=0 completed=6 transient=0 failed=0 running=True processId=44804 status=Ready
```
The kill lands during the first in-flight `/tag`, the supervisor restarts a new process, the affected rows are
retried to `Completed`, and every file carries tags.

### 4. A Daminion outage pauses the run and resumes when the server returns, with nothing dropped ✓ (fake-network, stated)
```
ResilienceTests.A_catalog_outage_pauses_the_run_and_a_batch_resumes_after_the_window
```
A real `DaminionApiClient` on a closed port → every claimed row back to `Pending`, `IsPaused`, no claim inside
the five-minute window, a batch again after it. **Not proven here:** a live server returning mid-window; that is
the phase 8 acceptance run.

### 5. The same image and parameters produce identical tags on both paths ✓
```
ParityTests  2 passed
```
**Deferred, not ticked:** repeating parity with the **live model** (rather than the deterministic fake) is
08-03's job — non-deterministic sampling makes it an acceptance observation, not a CI assertion.

## Requirement coverage

| Requirement | Evidence |
|---|---|
| TAG-01 batch execution at configured concurrency | `BatchExecutorTests` (measured overlap = `maxConcurrentItems`) |
| TAG-02 field switches + overwrite policy | `WriteBackPolicyTests` |
| TAG-03 fingerprint refresh (no re-tag loop) | `WriteBackPolicyTests.The_write_refreshes_the_fingerprint…` |
| TAG-04 parity by reuse | `ParityTests` |
| TAG-05 sidecar crash/restart | `ResilienceTests` (a), `RealSidecarKillTests` |
| TAG-06 locked file | `ResilienceTests` (c) |
| TAG-07 corrupt image | `ResilienceTests` (d), `JobOutcomeClassifierTests` |
| TAG-08 Daminion outage pause | `ResilienceTests` (e), classifier theory |

## Full suite at the gate

```
dotnet build Synapic.Net.sln --nologo -v minimal        -> 0 warnings, 0 errors
Synapic.Shared.Tests       6 passed
Synapic.Core.Tests       148 passed
Synapic.Integration.Tests  5 passed
Synapic.Indexer.Tests    213 passed, 1 skipped   (the opt-in real-kill test)
Synapic.Main.Tests       597 passed, 1 skipped, 1 environment failure when run alone
```

**Stated limitation:** the solution-wide parallel run is not green on this machine. `Main.Tests` produced 137
failures with one root cause — `MainWindowViewModel.OnUiLogEmitted` inserting into an `ObservableCollection`
from inside a `CollectionChanged` handler under load — and the same suite run alone gives 597 passed and one
failure (`Step4ResultsBehaviorTests…`, `Unable to locate 'Avalonia.Platform.IFontManagerImpl'`), which passes in
isolation. Both are in `Synapic.Main`, untouched by this phase, and are recorded rather than papered over. The
phase's own suites (Core, Indexer, Shared, Integration) are green.
