# 06-04 SUMMARY — Daminion source, scheduler, `--status` and the phase gate

**Status:** Complete
**Requirements:** SCAN-04, SCAN-05, SCAN-06, SCAN-07

## What was built

| File | Role |
|------|------|
| [DaminionWorkSource.cs](../../../src/Synapic.Indexer/Detection/DaminionWorkSource.cs) | Builds the desktop's `DatasourceSelection` and calls `WorkflowRunner.FetchItemsAsync` |
| [ScanScheduler.cs](../../../src/Synapic.Indexer/Detection/ScanScheduler.cs) | Immediate run, then one per `intervalMinutes`; one failing source never ends the loop |
| [QueueSidecarActivity.cs](../../../src/Synapic.Indexer/Detection/QueueSidecarActivity.cs) | `ISidecarActivity` from the queue, cached for 1 s |
| [StatusCommand.cs](../../../src/Synapic.Indexer/Commands/StatusCommand.cs) | `--status`: counts per state + five newest failures |
| [ServiceCommandLine.cs](../../../src/Synapic.Indexer/Install/ServiceCommandLine.cs) | `ServiceMode.Status` and its usage line |
| [Program.cs](../../../src/Synapic.Indexer/Program.cs) | Queue init + recovery, source construction, DI, hosted-service order |
| [IndexerConfigurationException.cs](../../../src/Synapic.Indexer/Configuration/IndexerConfigurationException.cs) | Typed pre-host configuration failure |

`--status` is handled **before** `paths.EnsureCreated()`, so it creates no directories and no log.
Hosted services register as worker → scheduler → watcher, so shutdown stops detection before the sidecar.

## Deviations from the plan, each forced by a real failure or a spec contradiction

1. **Scope is limited to `all`/`catalog`.** The config has no saved-search/collection id fields, and the
   client treats an id-less `collection`/`saved_search` scope as "whole catalog". Accepting those words would
   silently query everything, so `DaminionWorkSource` refuses them and the validator now rejects them at
   load time. Supporting them needs config fields (already deferred in 06-CONTEXT).
2. **Daminion + `Watch` is rejected.** A catalog cannot be watched; a watch-only Daminion config would never
   detect anything.
3. **Read-only status and WAL.** With no `-wal` file the database is opened as an SQLite `immutable=1` URI,
   which reads it without creating companion files. When a `-wal` exists (the service is running, or a forced
   kill left one) SQLite needs its `-shm`; `--status` then falls back to a `PRAGMA query_only=1` connection
   rather than failing. The queue's rows are never modified either way.
4. **A `--status` on a missing database prints `no queue yet`** with exit 0, per D-11-20.

## Phase gate — ROADMAP Phase 6 success criteria

### 1. Fixture scan enqueues supported extensions once; second scan nothing; touching re-enqueues ✓
```
dotnet test tests/Synapic.Indexer.Tests -c Release --nologo --filter "FullyQualifiedName~LocalFolderScanSourceTests"
Passed! - Failed: 0, Passed: 10, Skipped: 0, Total: 10
```
Facts: `First_scan_enqueues_the_five_supported_non_empty_files`,
`A_second_scan_of_unchanged_files_enqueues_nothing_and_reads_no_content`,
`Touching_only_the_time_costs_one_hash_and_enqueues_nothing`,
`One_changed_byte_puts_the_file_back_in_the_queue`.
Service-level: [acceptance transcript](acceptance-transcript.txt) §1–§2 —
`found=3 enqueued=3` then `found=4 enqueued=0 skipped=4`.

### 2. A killed batch returns every InFlight job to Pending, with no duplicate rows ✓ (partly, stated honestly)
```
dotnet test tests/Synapic.Indexer.Tests -c Release --nologo --filter "FullyQualifiedName~IndexingQueueRepositoryTests"
Passed! - Failed: 0, Passed: 18, Skipped: 0, Total: 18
```
`Recovery_returns_every_InFlight_row_to_Pending_and_says_why` moves two claimed rows back with a
`service restart` event; `A_second_repository_over_the_same_file_sees_the_same_rows` proves the state is on disk.
Service-level: acceptance transcript §4 — a seeded `InFlight` row became `Pending` at startup
(`Recovered 1 InFlight job(s)…`), `--status` showed `Pending 4 / InFlight 0`, and the immediate scan enqueued 0.

**Not proven here:** killing a live batch. There is no executor until phase 7, so the mid-batch state was
seeded directly in the database. The real kill-while-processing test is 07-03/08-03.

### 3. A watched file is enqueued once after the debounce, never while a writer holds it ✓
```
dotnet test tests/Synapic.Indexer.Tests -c Release --nologo --filter "FullyQualifiedName~FolderWatcherTests"
Passed! - Failed: 0, Passed: 7, Skipped: 0, Total: 7   (run twice)
```
`A_file_still_held_by_a_writer_waits_for_the_probe` enqueues nothing while the handle is open and one row after
it closes; `A_file_moved_into_the_folder_is_enqueued_once_after_the_debounce` proves the single enqueue.
Service-level: acceptance transcript §2 (`Enqueued 1 of 1`).

### 4. Daminion untagged query produces the expected rows; an unreachable server leaves the queue intact ✓
```
dotnet test tests/Synapic.Indexer.Tests -c Release --nologo --filter "FullyQualifiedName~DaminionWorkSourceTests"
Passed! - Failed: 0, Passed: 15, Skipped: 0, Total: 15
```
`The_selection_reaches_the_shared_fetch_with_the_configured_scope` asserts `keywords`/`categories` and scope
reach `FetchItemsAsync`; `Items_map_to_daminion_keys_with_server_hash_fingerprints`; a `DaminionNetworkException`
yields `Unavailable` with an empty queue; a missing/undecryptable password and an unsupported scope are fatal
configuration errors naming the setting. No test contacts a server.
**Not proven here:** pausing/resuming a run against a live catalog — that is phase 7 behaviour.

### 5. Queue state is inspectable and every transition is unit-tested at the repository seam ✓
```
dotnet test tests/Synapic.Indexer.Tests -c Release --nologo --filter "FullyQualifiedName~JobStateMachineTests"
Passed! - Failed: 0, Passed: 27, Skipped: 0, Total: 27
dotnet test tests/Synapic.Indexer.Tests -c Release --nologo --filter "FullyQualifiedName~StatusCommandTests"
Passed! - Failed: 0, Passed: 4, Skipped: 0, Total: 4
```
`Every_other_pair_throws` enumerates all 25 state pairs (9 permitted, 16 forbidden); `Completing_a_row_records_the_transition` checks the
`job_events` trail. `--status` was run while the service was running and after shutdown (transcript §3, §5).

## Full suite

```
dotnet build Synapic.Net.sln --nologo -v minimal            -> 0 warnings, 0 errors
Synapic.Indexer.Tests  : 175 passed, 0 failed
Synapic.Core.Tests     : 144 passed, 0 failed
Synapic.Shared.Tests   :   6 passed, 0 failed
Synapic.Main.Tests     : 598 passed, 3 skipped, 0 failed
```

## Residue

The console run left a queue database under `%ProgramData%\Synapic\state`; it and the stale `sidecar.pid` were
deleted after the transcript, and the pre-existing dev config was restored. Logs from the run remain in
`%ProgramData%\Synapic\Logs\indexer-20261022.log`.
