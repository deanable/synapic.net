---
phase: 01-extract-run-state-and-operation-contract
plan: 03
subsystem: ui
tags: [avalonia, mvvm, operation-contract, adapters, testing]

# Dependency graph
requires:
  - phase: 01-extract-run-state-and-operation-contract
    provides: RunStateViewModel + RunLog (plan 01-01), dedup on the base (plan 01-02)
provides:
  - IOperationViewModel — the nine-member contract Phase 2's OperationLayout binds
  - TagOperationViewModel / DedupOperationViewModel / UpscaleOperationViewModel thin adapters
  - OperationContractTests — reflection pins on the contract shape and adapter delegation
affects: [02-add-operation-template-and-dashboard, 01-04 phase gate, Phase 2 DataTemplates + ShellViewModel]

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "Thin adapters delegate every member: Run is the wrapped command instance, summaries/topics/gates are read-back"
    - "Adapter change plumbing re-raises all nine members on any wrapped change (extra notification over a stale value)"
    - "HelpTopic values come from HelpTopics (the app's single topic list), never from string literals in a view model"

key-files:
  created:
    - src/Synapic.Main/ViewModels/Operations/IOperationViewModel.cs
    - src/Synapic.Main/ViewModels/Operations/OperationAdapters.cs
    - tests/Synapic.Main.Tests/OperationContractTests.cs
  modified: []

key-decisions:
  - "IOperationViewModel extends INotifyPropertyChanged — the template binds ParametersSummary/IsRunEnabled/RunDisabledReason while a run is live"
  - "Tag's Report is the results step (optional ctor arg): Step3ProcessViewModel exposes no result surface and this plan must not modify it"

patterns-established:
  - "One contract, three adapters, no copied state: the seam Phase 2 binds instead of mode-specific view models"

requirements-completed: []

# Metrics
duration: 12min
completed: 2026-10-08
---

# Phase 1 Plan 03: IOperationViewModel + thin adapters Summary

**The three modes now expose one nine-member contract — Key/Title/Description/HelpTopic/ParametersSummary/Run/IsRunEnabled/RunDisabledReason/Report — with every member delegated to the step view model it wraps, pinned by eight new tests and zero view or service changes**

## Performance

- **Duration:** 12 min
- **Started:** 2026-10-08T21:39:20Z
- **Completed:** 2026-10-08T21:51:41Z
- **Tasks:** 2
- **Files created:** 3
- **Files modified:** 0

## Accomplishments

- `IOperationViewModel` declares exactly the nine members Phase 2's `OperationLayout`/`RunStateBar`/dashboard bind, and deliberately no datasource member — Region A stays shell-owned (D-04).
- Three sealed adapters over the real step view models: `Run` hands out the wrapped command *instance* (tagging's batch start, dedup's `ScanCommand` — Apply stays dedup-specific — upscaling's start), `ParametersSummary` reads the mode's own read-back line, `IsRunEnabled` mirrors that command's `CanExecute`, and `RunDisabledReason` spells out the same conditions the mode's predicate checks.
- `HelpTopic` comes from `HelpTopics.ForStepIndex(2)` / `ForStepIndex(4)` / `HelpTopics.Upscale`, so a renamed topic is a compile error rather than a blank page.
- `Report` is the mode's result surface or null before the first run: tag → the results step, dedup → the reviewed groups, upscale → the run's log lines.
- `OperationContractTests` (8 tests) pins the member names by reflection, the no-Folder/Drive/SourcePath rule, the three sealed adapters with their keys/titles, command delegation by reference, live `CanExecute` mirroring, default-disabled source gates with a reason, and that a wrapped change actually reaches the adapter's bound members.

## Task Commits

1. **Task 1: IOperationViewModel + three adapters** — `d55d87a` (feat)
2. **Task 2: Contract test** — `d4214a6` (test)

**Plan metadata:** `docs(01-03): complete operation-contract plan 03` (this commit)

## Files Created/Modified

- `src/Synapic.Main/ViewModels/Operations/IOperationViewModel.cs` — the contract (nine members + `INotifyPropertyChanged`), documented member by member
- `src/Synapic.Main/ViewModels/Operations/OperationAdapters.cs` — the three sealed adapters plus the shared `OperationChange` change-plumbing helper
- `tests/Synapic.Main.Tests/OperationContractTests.cs` — the pins listed above

## Verification

| Check | Result |
|-------|--------|
| `dotnet build Synapic.Net.sln --nologo -v minimal` | exit 0 — 0 Warning(s), 0 Error(s) |
| `dotnet test tests/Synapic.Main.Tests/Synapic.Main.Tests.csproj -c Release` | exit 0 — 431 passed, 0 failed, 0 skipped (423 before, +8 new) |
| `dotnet test … --filter "FullyQualifiedName~OperationContractTests"` | exit 0 — 7 passed at that point, 8 after the change-plumbing test |
| `grep -c "sealed class.*OperationViewModel" OperationAdapters.cs` | 3 |
| `git status --porcelain \| grep -E "\.axaml$\|Services/"` | 0 — no view and no service file touched |
| Files created vs plan `files_modified` | exactly the three planned paths; nothing else |

## Decisions Made

- **The contract extends `INotifyPropertyChanged`, and the adapters implement it.** The build refused the first version (CS0535 ×3): a template that binds a live `IsRunEnabled` needs change notifications, and returning a non-observable object would have deferred the problem into Phase 2. Each adapter subscribes to the wrapped view model and to its run command's `CanExecuteChanged`, then re-raises all nine member names through the shared `OperationChange.RaiseAll` — deliberately over-notifying rather than mapping which wrapped property feeds which member (an extra notification is cheap, a stale "not ready" reason is not).
- **Tag's `Report` needs the results step.** `Step3ProcessViewModel` exposes only the sidecar and the run surface; the results grid lives on `Step4ResultsViewModel`. This plan may not modify the step view models, so the adapter takes an optional `Step4ResultsViewModel` (null in tests and until the shell hosts one) instead of reaching into `Session.Results` — `Report` is null until a batch has rows.

## Deviations from Plan

1. **`INotifyPropertyChanged` on the contract and `OperationChange.RaiseAll` in the adapters** — required by the contract being bindable (see Decisions). Still no re-implemented *logic*: every member reads the wrapped view model; only the notification plumbing is new.
2. **`TagOperationViewModel`'s optional second constructor argument** (`Step4ResultsViewModel? results = null`) — additive and defaulting to null; needed because tag's result surface does not live on the view model this plan was allowed to wrap.

Both deviations are additive and were verified by the gates above; neither changes any existing binding or behavior.

## Issues Encountered

- The first build failed with `CS0535: 'TagOperationViewModel' does not implement interface member 'INotifyPropertyChanged.PropertyChanged'` (three errors, 0 warnings). Fixed by implementing the interface on all three adapters and forwarding the wrapped mode's changes — not by weakening the contract.

## User Setup Required

None - no external service configuration required.

## Next Phase Readiness

- D-04 and D-05 are observable: exactly nine contract members with no datasource member, three delegating adapters, the contract pinned green.
- Nothing in the app references the new types yet, which is exactly the plan's intent: Phase 2's `OperationLayout`/`RunStateBar` and `ShellViewModel.Current` are the first consumers (02-01, 02-03).
- Wave 2 is complete; plan 01-04 (phase gate sweep) is unblocked.

---
*Phase: 01-extract-run-state-and-operation-contract*
*Completed: 2026-10-08*

## Self-Check: PASSED
