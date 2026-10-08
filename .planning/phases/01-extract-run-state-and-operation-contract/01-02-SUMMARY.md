---
phase: 01-extract-run-state-and-operation-contract
plan: 02
subsystem: ui
tags: [avalonia, mvvm, run-state, dedup, logging]

# Dependency graph
requires:
  - phase: 01-extract-run-state-and-operation-contract
    provides: RunStateViewModel + RunLog (plan 01-01)
provides:
  - Dedup route on the shared run-state base (IsScanning forwards to IsRunning)
  - RunLog.Trim: the one log-cap implementation, used by the shell log too
affects: [02-add-operation-template-and-dashboard, 01-04 phase gate, Phase 2 RunStateBar]

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "Modes forward their legacy flags to the shared base (IsScanning => IsRunning) and keep the view-facing names"
    - "Log capping goes through RunLog.Trim for string logs and the shell's event log alike"

key-files:
  created: []
  modified:
    - src/Synapic.Main/ViewModels/Steps/StepDedupViewModel.cs
    - src/Synapic.Main/ViewModels/MainWindowViewModel.cs
    - src/Synapic.Main/ViewModels/Operations/RunLog.cs

key-decisions:
  - "Dedup keeps ScanCommand as its primary run; the inherited StartCommand delegates to the same scan body"
  - "RunLog gained a generic Trim so the shell's UiLogEvent list shares the single cap constant without a string adapter"

patterns-established:
  - "Legacy flag forwarding: the bound name survives, the state lives on the shared base"

requirements-completed: []

# Metrics
duration: 5min
completed: 2026-10-08
---

# Phase 1 Plan 02: Dedup adopts the base; MainWindowViewModel log cap delegates to RunLog Summary

**All three operation modes now inherit one run-state surface and the log cap exists exactly once — RunLog — with the shell's event log capped through it too**

## Performance

- **Duration:** 5 min
- **Started:** 2026-10-08T21:33:39Z
- **Completed:** 2026-10-08T21:38:34Z
- **Tasks:** 2
- **Files modified:** 3

## Accomplishments

- `StepDedupViewModel` inherits `RunStateViewModel`; `IsScanning` forwards to the base `IsRunning`, so the dedup view keeps its exact bindings while the state lives in one place.
- `MainWindowViewModel`'s two `while (LogEntries.Count > 2000)` trim sites collapsed onto `RunLog.Trim`; the literal exists only in `RunLog.cs` across the whole `ViewModels/` tree.
- No visible change: no `.axaml` file touched, `public void AppendLog(string line)` unchanged, scan copy strings and `SynapicLog` calls untouched.

## Task Commits

1. **Task 1: MainWindowViewModel delegates its trim sites to RunLog** — `348609b` (refactor)
2. **Task 2: StepDedupViewModel adopts RunStateViewModel** — `ff484ad` (refactor)

**Plan metadata:** `docs(01-02): complete run-state plan 02` (this commit)

## Files Created/Modified

- `src/Synapic.Main/ViewModels/Operations/RunLog.cs` — added the generic `Trim` (used by `Append` and the shell log)
- `src/Synapic.Main/ViewModels/MainWindowViewModel.cs` — both trim sites now call `RunLog.Trim(LogEntries)`
- `src/Synapic.Main/ViewModels/Steps/StepDedupViewModel.cs` — inherits the base; `IsScanning` forwards to `IsRunning`; run-command notifications move to the base hook

## Verification

| Check | Result |
|-------|--------|
| `dotnet build Synapic.Net.sln --nologo -v minimal` | exit 0 — 0 Warning(s), 0 Error(s) |
| `dotnet test tests/Synapic.Main.Tests/Synapic.Main.Tests.csproj -c Release` | exit 0 — 423 passed, 0 failed, 0 skipped |
| `git grep "2000" -- src/Synapic.Main/ViewModels/` | only `Operations/RunLog.cs` |
| `git grep "void AppendLog" -- src/Synapic.Main/ViewModels/Steps/` | no matches |
| `git status --short \| grep -c "\.axaml"` | 0 |
| Focused `StepDedupViewModelTests` + `WizardNavigationTests` | 24 passed, 0 failed |

## Decisions Made

- Dedup keeps `ScanCommand` as its primary run; the inherited `StartCommand` delegates to the same scan body, so the base contract holds without a second code path.
- `RunLog.Trim<T>` is generic so the shell's `ObservableCollection<UiLogEvent>` shares the one cap constant instead of a string-only helper.

## Deviations from Plan

None - plan executed exactly as written.

## Issues Encountered

None.

## User Setup Required

None - no external service configuration required.

## Next Phase Readiness

- D-02 and D-03 are now fully observable: three mode view models on one base, one log-cap implementation, shell log signature intact.
- Ready for plan 01-03 (`IOperationViewModel` + adapters + contract test), which is also in wave 2.

---
*Phase: 01-extract-run-state-and-operation-contract*
*Completed: 2026-10-08*

## Self-Check: PASSED
