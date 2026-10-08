---
phase: 01-extract-run-state-and-operation-contract
plan: 01
subsystem: ui
tags: [avalonia, mvvm, run-state, refactor, deduplication]

# Dependency graph
requires: []
provides:
  - RunStateViewModel: shared progress/ETA surface, CTS + pause lifecycle, capped UI-thread log, ProcessProgress mapper
  - RunLog: the single append + trim implementation for operation logs
affects: [02-add-operation-template-and-dashboard, RunStateBar binding, 01-02 dedup adoption, 01-03 IOperationViewModel]

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "Operation run state is inherited from RunStateViewModel; views bind the inherited members unchanged"
    - "Log writes go through RunLog.Append (UI-thread marshal + one cap constant)"

key-files:
  created:
    - src/Synapic.Main/ViewModels/Operations/RunStateViewModel.cs
    - src/Synapic.Main/ViewModels/Operations/RunLog.cs
  modified:
    - src/Synapic.Main/ViewModels/Steps/Step3ProcessViewModel.cs
    - src/Synapic.Main/ViewModels/Steps/StepUpscaleViewModel.cs

key-decisions:
  - "The base owns the shared Start command plus begin/end/cancel/pause/resume helpers; each mode keeps only the run commands it had (tagging: Abort/Pause/Resume, upscaling: Stop) so no new enabled states appear"
  - "LogLines moved onto the base so one collection feeds the one AppendLog implementation"

patterns-established:
  - "Run-state extraction: derived view models implement CanStart, StartCoreAsync and a NotifyRunCommandsCanExecuteChanged hook"
  - "One log cap constant (RunLog.MaxLogLines) for every operation log"

requirements-completed: []

# Metrics
duration: 7min
completed: 2026-10-08
---

# Phase 1 Plan 01: RunLog + RunStateViewModel; Step3 and StepUpscale adopt the base Summary

**Tagging and upscaling now share one run-state implementation (progress/ETA/log/cancellation) with no view change — the duplicated log cap is down to a single constant**

## Performance

- **Duration:** 7 min
- **Started:** 2026-10-08T21:24:25Z
- **Completed:** 2026-10-08T21:31:36Z
- **Tasks:** 3
- **Files modified:** 4 (2 created, 2 refactored)

## Accomplishments

- `RunStateViewModel` (abstract, `ViewModels/Operations/`) owns ProgressPercent/ProgressText/EtaText/CurrentFile/IsRunning/IsPaused, `IsIdle`, the CancellationTokenSource + pause-token lifecycle, the UI-marshalled `AppendLog`, and the shared `ProcessProgress` ETA mapper carrying the exact Python-parity strings.
- `RunLog` is the only implementation of the append-and-trim log behaviour (one `MaxLogLines` constant), replacing the verbatim copies in Step 3 and StepUpscale.
- `Step3ProcessViewModel` and `StepUpscaleViewModel` inherit the base; their views bind the same member names and no `.axaml` file changed.

## Task Commits

1. **Task 1: Create RunLog and RunStateViewModel** — `9f167e1` (feat)
2. **Task 2: Step3ProcessViewModel adopts RunStateViewModel** — `7faaebd` (refactor)
3. **Task 3: StepUpscaleViewModel adopts RunStateViewModel** — `91e3b63` (refactor)

**Plan metadata:** `docs(01-01): complete run-state extraction plan` (this commit)

## Files Created/Modified

- `src/Synapic.Main/ViewModels/Operations/RunLog.cs` — append + trim with UI-thread marshalling; `MaxLogLines`
- `src/Synapic.Main/ViewModels/Operations/RunStateViewModel.cs` — shared run surface (state, CTS/pause lifecycle, log, progress mapper, shared Start command)
- `src/Synapic.Main/ViewModels/Steps/Step3ProcessViewModel.cs` — inherits the base; keeps CanStart, the batch body, Abort/Pause/Resume
- `src/Synapic.Main/ViewModels/Steps/StepUpscaleViewModel.cs` — inherits the base; keeps CanStart, the batch body, Stop

## Verification

| Check | Result |
|-------|--------|
| `dotnet build Synapic.Net.sln --nologo -v minimal` | exit 0 — 0 Warning(s), 0 Error(s) |
| `dotnet test tests/Synapic.Main.Tests/Synapic.Main.Tests.csproj -c Release` | exit 0 — 423 passed, 0 failed, 0 skipped |
| `git grep "void AppendLog" -- src/Synapic.Main/ViewModels/Steps/` | no matches |
| `git grep "LogLines.Count > 2000" -- src/Synapic.Main/ViewModels/` | no matches |
| `git diff --name-only c512ff49..HEAD \| grep -c "\.axaml$"` | 0 |
| `git diff --name-only c512ff49..HEAD` | exactly the four planned files |

## Decisions Made

- The base owns the shared `StartCommand` (abstract `CanStart` + `StartCoreAsync`) and the begin/end/cancel/pause/resume helpers; each operation keeps exactly the run commands it had, so no new enabled states appear (upscaling gains no pause surface).
- `LogLines` moved onto the base: one collection for the one `AppendLog` implementation.
- The class is declared `abstract partial` — `partial` is required by the CommunityToolkit source generators that emit the observable properties and commands.

## Deviations from Plan

None - plan executed exactly as written.

## Issues Encountered

- Task 1's acceptance bullet "RunLog.cs is the only file under `ViewModels/` whose source contains the literal `2000`" cannot be fully reached inside this plan: `MainWindowViewModel` still caps `LogEntries` at two sites, and the wave plan assigns that delegation to **01-02** ("MainWindowViewModel log cap delegates to RunLog"). At 01-01 close the literal lives in `RunLog.cs` plus those two `MainWindowViewModel` sites; `Steps/` is clean and both step view models are migrated. Tracked for 01-02.

## User Setup Required

None - no external service configuration required.

## Next Phase Readiness

- Phase 2's shared `RunStateBar` can bind one state shape: `RunStateViewModel` is the single progress/ETA/log surface for tagging and upscaling.
- 01-02 remains: dedup adopts the base and `MainWindowViewModel` delegates its log cap to `RunLog`; 01-03 adds `IOperationViewModel` plus the three adapters.

---
*Phase: 01-extract-run-state-and-operation-contract*
*Completed: 2026-10-08*

## Self-Check: PASSED
