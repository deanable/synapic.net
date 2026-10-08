# Phase 1: Extract run state and operation contract - Context

**Gathered:** 2026-10-08
**Status:** Ready for planning

<domain>
## Phase Boundary

Extract the duplicated run machinery behind shared types with **zero visible change**: no `.axaml`
view file may change, no navigation or layout change, no service-layer change. The phase delivers
(a) a shared run-state base so progress/ETA/log/cancel logic exists once, and (b) an
`IOperationViewModel` contract with thin adapters over the three existing step view models, so
Phase 2 has a seam to build the shared template against. Source of scope: `docs/ui-refactor-plan.md`
§3 (target contract) and §4 Phase 1 (extract step).

</domain>

<decisions>
## Implementation Decisions

### Run-state extraction
- **D-01:** Create `RunStateViewModel` (abstract, extends `ViewModelBase` in
  `src/Synapic.Main/ViewModels/ViewModelBase.cs`) in `src/Synapic.Main/ViewModels/Operations/`.
  It owns: `ProgressPercent`, `ProgressText`, `EtaText`, `CurrentFile`, `IsRunning`, `IsPaused`;
  Start/Pause/Resume/Stop commands; CancellationTokenSource lifecycle; the UI-marshalled `AppendLog`
  helper with the 2000-line cap; and the shared `ProcessProgress`-driven ETA mapper (currently
  duplicated identically in Step3ProcessViewModel and StepUpscaleViewModel).
- **D-02:** `Step3ProcessViewModel`, `StepUpscaleViewModel`, and the dedup scan path in
  `StepDedupViewModel` adopt `RunStateViewModel` (inherit, or compose where an existing base-class
  conflict forces it — dedup keeps its `IsScanning`/`ScanSummary` semantics on top).
- **D-03:** The four verbatim `AppendLog` implementations across ViewModels/ collapse into exactly
  one implementation (one `RunLog`/base-class helper). After the phase, a repo grep for the
  `2000` log-cap line in `src/Synapic.Main/ViewModels/` must hit exactly one file.

### Operation contract
- **D-04:** `IOperationViewModel` = `Key`, `Title`, `Description`, `HelpTopic`,
  `ParametersSummary`, `Run` (command/task for the mode's primary operation), `IsRunEnabled`,
  `RunDisabledReason`, `Report` (the mode's result object for Region C). The Region A data-source
  is deliberately NOT on the interface — it stays one shell-owned instance (design `ui-design.md` D4).
- **D-05:** Adapters are thin: they expose the existing step view models through the interface
  without rewriting behavior. Tagging's primary `Run` = the existing process step; upscale = the
  upscale run; dedup = the scan (Apply remains dedup-specific until Phase 3 adds its busy state).
- **D-06:** Service layer (`WorkflowRunner`, `IWorkflowItemHandler`, `ProcessingOrchestrator`,
  `ProcessProgress` in `src/Synapic.Main/Services/Processing/`) is untouched — this is a
  presentation-layer extraction only.

### Verification discipline
- **D-07:** Gate is objective: `dotnet build Synapic.Net.sln` 0 warnings/0 errors;
  `dotnet test Synapic.Net.sln -c Release` fully green with **no test weakened, skipped or
  deleted**; `git diff --stat` touches only `src/Synapic.Main/ViewModels/` and
  `src/Synapic.Main/Services/` files (zero `.axaml` diffs).

### Claude's Discretion
- Exact file split (one base file vs. base + `RunLog` helper), interface file placement, XML-doc
  wording, and whether dedup composes rather than inherits if its existing members conflict.

</decisions>

<canonical_refs>
## Canonical References

**Downstream agents MUST read these before planning or implementing.**

### Target contract and sequencing
- `docs/ui-refactor-plan.md` §3 — full target contract: `IOperationViewModel` members,
  `RunStateViewModel` responsibilities, region rules (A/B/C), what stays shell-owned
- `docs/ui-refactor-plan.md` §4 Phase 1 — this phase's steps, risks and gate
- `docs/ui-design.md` §3 — OperationLayout region definitions the contract must satisfy (read for
  interface shape only; layout is Phase 2)

### Code to extract FROM (read; do not restructure)
- `src/Synapic.Main/ViewModels/Steps/Step3ProcessViewModel.cs` — run block + ETA mapper + AppendLog
- `src/Synapic.Main/ViewModels/Steps/StepUpscaleViewModel.cs` — near-verbatim duplicate of the above
- `src/Synapic.Main/ViewModels/Steps/StepDedupViewModel.cs` — third fork (scan state, Apply)
- `src/Synapic.Main/ViewModels/ViewModelBase.cs` — existing base (ObservableObject)
- `src/Synapic.Main/Services/Processing/ProcessingOrchestrator.cs` — `ProcessProgress` record
  (progress payload shape the mapper consumes)

</canonical_refs>

<code_context>
## Existing Code Insights

### Reusable Assets
- `ViewModelBase : ObservableObject` — the base `RunStateViewModel` extends
- The ETA mapper and `AppendLog` already exist verbatim in 2–4 files — this phase moves them, not
  rewrites them

### Established Patterns
- MVVM with CommunityToolkit-style observables; commands as `RelayCommand`/`AsyncRelayCommand`
- Tests pin structure loudly: `MainLayoutShellTests`, `RouteSplitTests`, `WorkflowOrderTests`,
  `UiLayoutAuditTests` must stay green UNCHANGED this phase (they are the no-visible-change proof)

### Integration Points
- Adapters sit between `MainWindowViewModel`/`WizardViewModel` (consumers, unchanged this phase)
  and the three step view models

</code_context>

<deferred>
## Deferred Ideas

- `OperationLayout.axaml`, `RunStateBar.axaml`, `DataSourceStrip`, `DashboardView` — Phase 2
- Deleting sidebar/Back-Next/settings dialogs, `OperationShellViewModel` — Phase 3
- `UI-REVIEW.md` carried-forward finding #1 (dedup Apply has no busy state) — fixed in Phase 3,
  but the `IsApplying` hook may be added to the base class here if free

</deferred>

---

*Phase: 01-extract-run-state-and-operation-contract*
*Context gathered: 2026-10-08*
