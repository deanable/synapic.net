# 13-01 SUMMARY — The Service tab

**Status:** Complete
**Requirements:** SVC-01 ✓, SVC-02 ✓, SVC-03 ✓, SVC-04 ✓, SVC-05 ✓

## What was built

The GUI face of the background indexer (UI-01, realized): a Service tab on the
Settings view — its eighth — that installs, starts, stops and uninstalls the
`SynapicIndexer` service, manages the folders it watches, and shows its durable
queue live. The watch → queue → tag → write-into-file pipeline itself is the
shipped v1.1 service, unchanged; this plan added the console and the one write
path it needed.

| Piece | Files |
|-------|-------|
| **Shared contract** — the first GUI↔service bridge, so parity is structural (one type set, not two that agree by convention) | `src/Synapic.Indexer.Contract/` (new, net10.0): `IndexerPaths`, `JobState(s)`, `QueueSummary`/`QueueFailure`/`QueueEvent`, `IndexerQueueReader` (the safe cross-process read-only open, summaries, audit events), `IndexerConfig` + `IndexerConfigStore` (moved; `SaveTree` generalized). The Indexer consumes the same types it writes with |
| **The one elevated write path** — `--apply-config <file>` | `Install/ApplyConfigCommand.cs` + `ServiceCommandLine`/`Program`: merges a partial JSON patch over the config (unknown members survive), validates **before** saving (a refusal exits 2 with the file byte-identical), writes, and restarts the service if it was running (`ServiceInstaller.RestartIfRunningAsync`) |
| **The tab** | `ViewModels/Settings/IndexerServiceViewModel.cs`, `Views/Settings/ServiceSection.axaml(.cs)`, registration in `SettingsTab` (eighth tab, three sections: `SectionService`, `SectionWatchFolders`, `SectionQueue`; search suggestions), the shell's `IndexerService` property. Status reads unelevated through `sc.exe` (`Services/Indexer/IndexerServiceMonitor`); every elevated action is one `UseShellExecute` launch (`IndexerProcessLauncher`) — UAC; the poll timer lives in the view's code-behind |
| **Help + docs** | `docs/help/service.html` (+ hhc/hhk/hhp entries; `check-help.py` green: 32 topics), `indexer-operations.md` (the verb + the GUI's role), `ui-design.md` (eight tabs — also recording the previously undocumented Similar tab), `csharp-reference.md`, `testing.md` |

## Design notes

- **One write path, one validation.** The GUI never writes HKLM or ProgramData.
  Saving watch folders writes a patch to `%TEMP%` and runs the indexer's own
  elevated `--apply-config` — the same JSON-tree machinery as
  `--set-daminion-password`, so unknown members survive and the service's own
  validator decides what is acceptable.
- **Cross-platform stays true.** The app remains net10.0: the contract's
  registry read returns "absent" off Windows, and the tab says *Windows only*
  rather than offering actions it cannot perform.
- **Branches are inventoried.** The tab's reachable absences (no service, no
  executable, no queue, unreadable queue, not Windows, duplicate folder, the
  save note) are rows S-03…S-08 of `docs/ui-empty-branches.md`, each named by
  an `IndexerServiceViewModelTests` fact; the drift check covers them.

## Verification (2026-10-24)

- `ApplyConfigCommandTests` (6): merge/preserve, refuse-and-leave-unchanged, restart-when-running, not-restarted-when-stopped/not-installed, unreadable patch — all green
- `ContractQueueReaderTests` (3): counts/failures/events against a service-written database, read-only enforcement, garbage surfaces on query — green
- `IndexerServiceViewModelTests` (13): status→button mapping, the save round trip (patch JSON asserted field by field), queue view, all missing-something branches — green
- Pinning suites green with the eighth tab: `SettingsViewTests` (8 tabs, search), `RouteSplitTests`, `HelpScopeTests`, `RetiredUiConceptTests`, `EmptyBranchInventoryTests` (62 rows)
- `docs/help/check-help.py` → `help sources OK: 32 topics, 33 files in [FILES], 148 Contents/Index entries`
- Live smoke: on the machine with the service installed and RUNNING from the v1.0 MSI, the tab shows RUNNING/AUTO_START with live queue counts (recorded below)

## Live smoke (2026-10-24, dev build over the installed service)

- Status section: RUNNING, AUTO_START, Stop offered, Start refused — matches
  `sc.exe query/qc` for the real service
- Queue section: live counts from `%ProgramData%\Synapic\state\indexer.db`
- The save round trip was exercised through the verb tests; the elevated
  round trip on the live machine is the operator's call to make (one UAC
  prompt per save)
