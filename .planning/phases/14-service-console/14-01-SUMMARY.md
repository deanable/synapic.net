# 14-01 SUMMARY — The console app, the split, and the verified save

**Status:** Complete
**Requirements:** SVC-06 ✓, SVC-07 ✓

## The bug this milestone closes

"The watch folder does not persist." True in two ways. The machine's
`indexer-config.json` was last written 2026-10-23 06:26 — every change made
through the main app's UI touched the *app's* source folder (the Connection
tab), a different setting from the service's `schedule.watchPaths`. And the
v1.3 tab's save was fire-and-forget: a declined UAC prompt or a validation
refusal exited invisibly behind a "Save requested" note, so even a real attempt
could look saved without being saved. v1.4 separates the settings (their own
application) and makes the save verifiable.

## What was built

| Piece | Files |
|-------|-------|
| **The console app** | `src/Synapic.ServiceConsole/` (new): one window ("Synapic Service"), three sections, a five-second poll the window owns. Everything moved from `Synapic.Main` wholesale: the view model (now the window's DataContext), the view, the monitor/launcher seams. Deliberately small: contract reference only — no engine, no sidecar host, no DI container |
| **The verified save** | `--apply-config` writes `<patch>.result.json` on every exit path (exit, restarted, messages); the console's launcher awaits the elevated process (three-minute budget for the UAC prompt) and reads it; the note reports applied+restarted / applied+next-start / the refusal messages (edit stays dirty) / not-started. A `ProgressBar` shows the in-flight state, and the save button is off while it runs |
| **The split** | `Synapic.Main` is back to seven Settings tabs: the tab, the shell's `IndexerService` property, `Services/Indexer/`, and the `Synapic.Indexer.Contract` reference are gone; `SettingsViewTests` and the layout audit pin seven again |
| **Tests** | `tests/Synapic.ServiceConsole.Tests` (new): 17 facts — the moved behavior suite plus the verified-save facts (in-flight state included). `ApplyConfigCommandTests` now asserts the result file on success and refusal. `EmptyBranchInventoryTests.TestExists` resolves citations across **all** `tests/*` projects; the inventory rows S-03…S-10 name the console tests and the two new save-failure branches (S-09 refused, S-10 not-started) are inventoried — 64 rows |
| **Packaging** | `package-windows.ps1` publishes the console into the shared payload (the same D-11-28 pattern as the indexer); Inno gains a Start-menu icon, the MSI a shortcut component; the payload-drift checks pass unchanged (the console rides the glob — it is not in the required list, because the service runs without it) |
| **Docs** | `indexer-operations.md` (the console as the service's UI, the result file), `ui-design.md` (seven tabs; the service row becomes a pointer to the separate app), `csharp-reference.md`, `testing.md`, `docs/help/service.html` (names the console, documents the verified save, and states that the app's Connection folder is a different thing) |

## Verification (2026-10-24)

- Clean solution build 0 warnings / 0 errors (nine projects now, the console and
  its tests included)
- `Synapic.ServiceConsole.Tests` 17/17; `ApplyConfigCommandTests` 6/6 (result
  file asserted on success and refusal); `EmptyBranchInventoryTests` 5/5 over
  the 64-row inventory; the Main pinning suites back at seven tabs
- `docs/help/check-help.py` green (32 topics)
- Live: the console reads the real service (RUNNING, AUTO_START, the queue); the
  elevated apply round trip is recorded below

## Live round trip (the fix proven)

Through the elevated verb with a real patch: set `schedule.watchPaths` to a real
temp folder → result file `exit 0`, `restarted true`, config file rewritten and
the service restarted with the new folder; then reverted the same way. The
config file's last-write finally moved past 2026-10-23 06:26 — the persistence
the v1.3 tab could not prove, now reported by the command itself.
