---
phase: 04-extract-headless-engine-core
plan: 03
subsystem: core
tags: [extraction, tagging-kernel, dependency-injection, core-01, core-02]

# Dependency graph
requires:
  - phase: 04-extract-headless-engine-core
    provides: 04-01 (Core, log facade, sidecar), 04-02 (metadata writer, Daminion client)
provides:
  - Synapic.Core.Services.Processing — WorkflowRunner, ProcessingOrchestrator, PauseToken, ProcessStatus,
    DatasourceSelection, ProcessWorkItem, ProcessItemResult and the progress/ETA types
  - Main's processing services (DedupScanHandler, UpscaleItemHandler, DedupService, Step view models)
    constructed from Core types
  - pre-move baseline for the 04-04 count gate — Shared 6, Integration 5, Main 738 passed / 3 skipped / 0 failed
affects: [04-04, Phase 6 (queue feeds the kernel), Phase 7 (batch executor calls the kernel)]

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "A handler is a dependent, not a copy: DedupScanHandler stays in Main and implements Core's IWorkflowItemHandler"
    - "The desktop keeps its own services and only borrows the kernel, so a shippable app remains possible at every step of the move"

key-files:
  modified:
    - src/Synapic.Main/Services/EngineServiceRegistration.cs
    - src/Synapic.Main/ViewModels/Steps/Step3ProcessViewModel.cs, Step4ResultsViewModel.cs,
      StepDedupViewModel.cs, StepUpscaleViewModel.cs (using updates)
    - src/Synapic.Main/Services/Processing/DedupScanHandler.cs, UpscaleItemHandler.cs,
      DedupService.cs, DedupFingerprintCache.cs (using updates)
    - tests/Synapic.Main.Tests/*.cs (using updates)
  created:
    - src/Synapic.Core/Services/Processing/ProcessingOrchestrator.cs (moved)
    - src/Synapic.Core/Services/Processing/WorkflowRunner.cs (moved)
    - src/Synapic.Core/Services/Processing/PauseToken.cs (moved)
    - src/Synapic.Core/Services/Processing/ProcessStatus.cs (moved)

key-decisions:
  - "The kernel moved with its per-item status vocabulary; Main re-exports nothing, so there is one ProcessStatus, not a mapping layer"
  - "DedupScanHandler implements the Core handler interface in place — moving it would have pulled dedup, a v2 background concern, into the service's dependency graph"

patterns-established:
  - "Kernel types are referenced from Core directly at call sites; no type forwarding, no aliases, so a future host sees the same names the desktop does"

requirements-completed: [CORE-01, CORE-02]

# Metrics
duration: ~35min
completed: 2026-10-22
---

# Phase 4 Plan 03: Tagging kernel into Core, desktop repointed

**Download → `/tag` → write-back now runs from `Synapic.Core` with no desktop assembly in the path; `Synapic.Main` compiles against it and the app's suite is unchanged at 738 passed / 3 skipped**

## Performance

- **Duration:** ~35 min
- **Tasks:** 3
- **Files changed:** 4 moved into Core, ~20 desktop files repointed for the namespace only

## Accomplishments

- **The kernel is in Core.** `WorkflowRunner` (fetch, pause, bounded concurrency, progress, one-throwing-item isolation), `ProcessingOrchestrator` (per-item download → `/tag` → local write or Daminion check-in, `ProcessItemResult` status), `PauseToken` and `ProcessStatus` live in `Synapic.Core.Services.Processing`, with `DatasourceSelection`, `ProcessWorkItem`, `ProcessItemResult` and the progress/ETA types.
- **The desktop compiles against it, not a copy.** `DedupScanHandler`, `UpscaleItemHandler`, `DedupService`, `DedupFingerprintCache` and the four step view models reference the Core types directly; the only edits are `using` changes and the `EngineServiceRegistration` additions (metadata writer, sidecar wiring).
- **`DedupScanHandler` stayed where it belongs** and implements Core's `IWorkflowItemHandler`. The service host will supply its own handler for the same kernel; dedup's relationship to the kernel is that of a dependent, not a duplicate.
- **Nothing was rewritten during the move.** The orchestrator's download → tag → write order, its status values, the `WorkflowRunner` pause/concurrency/progress behavior and the Daminion check-in call are the pre-move code.

## Verification

- `dotnet build Synapic.Net.sln --nologo -v minimal` → **0 warnings, 0 errors**
- `dotnet test` per project at this gate → Shared **6/6**, Integration **5/5**, Main **738 passed / 3 skipped / 0 failed** — the 04-04 count baseline
- `git diff -M --stat` → the four kernel files show as renames, not delete + add
- `grep -rn "class DedupScanHandler" src/Synapic.Main` → still in Main, now implementing `Synapic.Core.Services.Processing.IWorkflowItemHandler`

## Decisions Made

- The kernel's status vocabulary moved with it: one `ProcessStatus` type, no adapter, so a status the desktop shows is the status the service records.
- Dedup stayed desktop-only; only its handler interface crosses into Core, which is what lets the kernel serve both hosts without dragging v2 work into v1.1.

## Issues Encountered

- The solution-wide `dotnet test Synapic.Net.sln -c Release` run hits the 10-minute command cap on this machine, so the baseline was recorded per test project. `Synapic.Main.Tests` runs serially (`xUnit.parallelizeTestCollections=false`) here: parallel headless Avalonia collections crash the host on this machine, a pre-existing condition unrelated to the move.
