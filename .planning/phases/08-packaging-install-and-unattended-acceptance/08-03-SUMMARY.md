# 08-03 SUMMARY — The unattended acceptance run and the milestone gate

**Status:** Complete (two rows stated as not exercised — see Stated limits)
**Requirements:** OPS-01..OPS-05

## Machine and inputs

| Item | Value |
|------|-------|
| OS | Microsoft Windows 11 Pro, build 26300 (10.0.26300), x64 |
| CPU / RAM | Intel Core i7-14700K / 31.8 GB |
| Runtime | .NET Desktop Runtime 10.0.12 (the one the installer bundles); 10.0.13 also present |
| Sidecar | `artifacts/win-x64/synapic-inference.exe`, 227 438 621 bytes, CPU variant |
| Model / device | `LiquidAI/LFM2.5-VL-450M` / `cpu`, confidence 0.3, probability `both`/0.5, no prompt |
| Fixtures | `beach.jpg` `1eb78e54…aeb6f`, `forest.jpg` `f0393624…4a35`, `skyline.jpg` `7a913a24…e7cb` (SHA-256, 6 511 / 8 994 / 21 642 bytes) |
| Watch folder | `C:\ProgramData\Synapic-Acceptance\watch` (LocalService + Users modify) |
| Model cache | Seeded into `C:\Windows\ServiceProfiles\LocalService\AppData\Local\Synapic\models` (4.1 GB) so the service did not re-download |

Preflight (recorded before install #1): `sc.exe query SynapicIndexer` → **1060** (not installed);
`%ProgramData%\Synapic` from earlier phases backed up to `artifacts/phase-a/programdata-backup-20261023-003455`,
then removed by the run. Installer built by `build/package-windows.ps1` (exit 0) with the payload listed in
[08-01-SUMMARY.md](08-01-SUMMARY.md).

## 1. Install — OPS-01 ✓

Install #1 (silent, `/VERYSILENT`) exited **0** and produced: service `SynapicIndexer`, AUTO_START, recovery
`RESTART 60000 ×3` reset 86400, Event Log source `SynapicIndexer`, `%ProgramData%\Synapic` with
`LOCAL SERVICE:(OI)(CI)(M)` and `SYSTEM:(OI)(CI)(M)`.

It also exposed two defects, both fixed and re-verified:

| Found | Fix | Re-verified |
|-------|-----|-------------|
| `SERVICE_START_NAME : LocalSystem` (sc.exe default; docs promise LocalService) | `obj=` token in `ServiceCommandLine.BuildCreateArguments` + test | install #2: `SERVICE_START_NAME : NT AUTHORITY\LocalService` |
| A failed start was invisible to the user | `--install` moved from `[Run]` to `Exec` in `CurStepChanged`, exit code on the finish page | build compiles; code logged as `started=1, exit code=N` |

On a **fresh** machine the service cannot start yet — no configuration exists — so it exits with
`schedule.watchPaths` + `engine.modelId` errors and `sc start` returns **1053**. The operations doc now states
this and the installer finish page names the remedy.

Install #2 (silent, after uninstalling #1 and writing `indexer-config.json` first) exited **0** and left the
service **RUNNING** as `NT AUTHORITY\LocalService`, recovery actions and Event Log source present.

## 2. Unattended tagging — OPS-02 ✓

No desktop app was running; nothing was clicked after the drop.

```
06:27:27  three fixtures copied into the watched folder
06:27:32  WorkIntake: Enqueued 1 of 1 candidate(s)   (×3, one per file; 5 s debounce)
06:27:36  --status: Pending 3
06:28:36  --status: InFlight 3
06:28:52  WorkflowRunner: tagging batch finished: 3 ok, 0 failed, 3 total
06:28:57  --status: Completed 3, Pending 0, InFlight 0
06:28:52  BatchExecutor: batch 1: claimed=3 completed=3 transient=0 failed=0 preserved=0 elapsed=23,2s
```

Tags read back from the files (XMP `dc:subject`, `dc:description`, `photoshop:Headline`):

| File | Category | Keywords | Description |
|------|----------|----------|-------------|
| beach.jpg | Flag | Flag, Blue, Red, Triangle, Sandy | A simple flag with a blue background and a red triangle pointing downwards on a sandy surface. |
| forest.jpg | Landscape | Trees, Path, Background, Color, Simplicity | A simple illustration of five green coniferous trees arranged in a row, with a brown path leading from the foreground to the background. |
| skyline.jpg | Cityscape | Building, City, Skyline, Urban, Structure | A simple illustration of five buildings against a blue background. |

## 3. Clean stop, crash recovery and parity — OPS-03 ✓

**Clean stop.** A 12-file batch was dropped, the service was stopped once it owned work
(`Pending 1 / InFlight 1 / Completed 3`). `sc.exe stop` → STOPPED in **5 212 ms** (budget 10 s); the sidecar's
graceful shutdown timed out and the supervisor killed the tree
(`Graceful shutdown failed (A task was canceled.); killing process tree` → `Sidecar stopped`); **no orphan**
process (`synapic-inference`, `Synapic.Indexer`) remained — re-checked after the shutdown completed, because the
first check a moment after SCM reported STOPPED still saw both inside their final 5 s kill window. The queue
after the stop held `Pending 12, InFlight 0, Completed 3` — the claimed row was returned during shutdown, nothing
lost.

**Crash recovery.** The service was restarted, a 6-file batch added, and with **18 rows InFlight** the process
was killed hard (`taskkill /F /PID 47220`). Immediately after: `Pending 0, InFlight 18, Completed 3` — the rows
survived the crash. The SCM recovery action restarted the service within the 60 s window, and startup logged
`Recovered 18 InFlight job(s) left by the previous run; they are Pending again.`; the batch then ran to
`Pending 0, InFlight 0, Completed 21, TransientFailure 0, Failed 0`. Tags on recovered files carry exactly five
keywords (no duplicate re-write). The hard kill took the sidecar with it (`Get-Process` empty two seconds later),
and the restarted service launched a fresh one (pid 35496); no stale inference process was left.

**Parity (live model).** The desktop's kernel (`ProcessingOrchestrator` + `InferenceSidecarService`, real
sidecar, CPU) tagged a fresh copy of `beach.jpg` with the same model/device/thresholds the service used; both
results were read back from the written XMP:

```
Desktop kernel: Flag | Flag, Blue, Red, Triangle, Sandy | A simple flag with a blue background and a red triangle…
Service:        Flag | Flag, Blue, Red, Triangle, Sandy | A simple flag with a blue background and a red triangle…
Equal category: True   Equal keywords: True   Equal description: True
```

Recorded in `artifacts/phase-a/parity-live.txt`. This is the desktop **kernel** with the live model, not the
GUI: `Synapic.Main.exe` has no headless entry point, so a windowed run was not driven.

## 4. Uninstall — data retained

The packaged uninstaller (`unins000.exe /VERYSILENT`) exited **0**: `sc query SynapicIndexer` → **1060**,
`C:\Program Files\Synapic` removed, `%ProgramData%\Synapic` intact (`indexer-config.json`, four log files,
`state\indexer.db` 45 056 bytes), and `artifacts/win-x64/Synapic.Indexer.exe --status` still reads the queue
(`Completed 21`). Event Log entries from the `SynapicIndexer` source are present for start, stop and recovery.

## 5. Documentation — OPS-04 ✓

[docs/indexer-operations.md](../../../docs/indexer-operations.md) (318 lines, install → configure → keys →
credentials → account → operating → troubleshooting → uninstall), the doc-vs-schema drift test
(`ConfigDocumentationTests`, 3 passed), links from `README.md` and `docs/README.md`, and the payload section in
[docs/packaging.md](../../../docs/packaging.md). Details in [08-02-SUMMARY.md](08-02-SUMMARY.md).

## 6. Build and suite — OPS-05 ✓

```
dotnet build Synapic.Net.sln -c Release --nologo -v minimal   → Build succeeded. 0 Warning(s) 0 Error(s)

dotnet test Synapic.Net.sln -c Release --nologo -m:1          → TEST_EXIT=0
Synapic.Shared.Tests        6 passed
Synapic.Main.Tests        598 passed, 3 skipped
Synapic.Integration.Tests   5 passed
Synapic.Core.Tests        148 passed
Synapic.Indexer.Tests     224 passed, 1 skipped   (the opt-in real sidecar-kill test)
                          ─────────────────────
                          981 passed, 4 skipped, 0 failed
```

**Stated limitation (unchanged from Phase 7, now with a workaround):** the *default* solution run, which runs the
test projects in parallel, is not green on this machine. Two runs produced two different single failures
(`RouteSplitTests.Settings_lays_the_source_form_out_above_the_engine_section`,
`TagFieldCheckboxTests.APartialSelection_WarnsWithTheFieldsThatWillBeWritten`), each passes in isolation and in the
project's own run (`Main.Tests` alone: 598 passed / 3 skipped / 0 failed). Serializing the projects (`-m:1`)
gives a clean solution run, which is the command recorded above. No test was skipped, weakened or removed.

## Requirement matrix

| Requirement | Result | Evidence |
|---|---|---|
| OPS-01 packaging ships indexer + sidecar, ProgramData ACLs, Event Log source | ✓ | 08-01 payload listing; install #2 `sc qc`, `qfailure`, `icacls`, `SourceExists=True` |
| OPS-02 unattended tagging of a dropped image | ✓ | drop 06:27:27 → Completed 06:28:57, tags read from the files |
| OPS-03 clean stop ≤10 s, no orphan, crash recovery, parity | ✓ | 5 212 ms stop; no orphan; `Recovered 18 InFlight`; identical live-model tags |
| OPS-04 docs: install/uninstall, configuration, logs, five failure modes | ✓ | `docs/indexer-operations.md`; `ConfigDocumentationTests` 3 passed |
| OPS-05 build 0/0 and solution suite green | ✓ | build 0/0; `-m:1` solution run 981 passed / 4 skipped / 0 failed |

## Milestone v1.1 verdict

| Phase | Verdict | Proving record |
|-------|---------|----------------|
| 4 Extract headless engine core | ✓ complete | `04-04-SUMMARY.md` (CORE-01..04, build 0/0, 756 cases) |
| 5 Indexer service host and configuration | ✓ complete | `05-04-SUMMARY.md` (HOST-01..07, elevated install/uninstall, stop 4.87 s) |
| 6 Work detection and durable job queue | ✓ complete | `06-04-SUMMARY.md` (SCAN-01..07, recovery via a seeded `InFlight` row) |
| 7 Tagging pipeline and write-back | ✓ complete | `07-04-SUMMARY.md` (TAG-01..08, real mid-batch sidecar kill, parity harness) |
| 8 Packaging, install and unattended acceptance | ✓ complete | this file (OPS-01..05) |

## Stated limits (not passes)

- **MSI channel not built or installed here.** WiX is not installed on this machine; the Inno installer is the
  shipping channel and carries the same payload.
- **Daminion was not exercised against a live server.** The indexer's `datasource.daminion` path is covered by
  the fake-network tests (`ResilienceTests`), not by this acceptance; the local path is what ran.
- **Parity is kernel-level and live-model, not GUI.** `Synapic.Main.exe` has no headless processing entry, so the
  comparison ran the desktop's own `ProcessingOrchestrator` with the real sidecar rather than driving the window.
- **A first install still ends with the service stopped** until a valid `indexer-config.json` exists; the finish
  page, the operations doc and 08-01 all say so. This is a design consequence (a service must not run on defaults
  it cannot honour), not a defect that was worked around.
- Residue left on the machine: the service is **uninstalled**; `%ProgramData%\Synapic` (acceptance config, logs,
  queue with 21 Completed rows) and `C:\ProgramData\Synapic-Acceptance\watch` (21 tagged fixtures) remain, as the
  plan requires data retention. The pre-existing dev ProgramData tree is still at
  `artifacts/phase-a/programdata-backup-20261023-003455`.
