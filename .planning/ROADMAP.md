# Roadmap: Synapic.NET — UI Refactor (Dashboard + Operation Template)

## Overview

Reorganize the Synapic desktop UI around the approved design in `docs/ui-design.md`: the app
starts on a 4-panel dashboard (Settings, Tag, Dedup, Upscale) and every mode renders one shared
operation layout (Data source → Parameters → Output/Report). The roadmap implements
`docs/ui-refactor-plan.md` in three strangler phases — extract the duplicated run machinery
behind an `IOperationViewModel` contract, build the shared template + dashboard, then collapse
the legacy navigation. Each phase keeps the app shippable and ends at a hard verification gate.

**Phase Numbering:**

- Integer phases (1, 2, 3): Planned milestone work
- Decimal phases (2.1, 2.2): Urgent insertions (marked with INSERTED)

## Phases

- [ ] **Phase 1: Extract run state and operation contract** - Pull the copy-pasted run machinery into a shared base; no visible change
- [ ] **Phase 2: Add operation template and dashboard** - One shared three-region layout + 4-panel dashboard the app boots to
- [ ] **Phase 3: Collapse navigation and retire settings dialogs** - Delete the legacy wizard chrome; inline parameters; busy-state fix

## Phase Details

### Phase 1: Extract run state and operation contract

**Goal:** Extract the duplicated run machinery with zero visible change: introduce `RunStateViewModel`
(owns ProgressPercent/ProgressText/EtaText/CurrentFile/IsRunning/IsPaused, Start/Pause/Resume/Stop
commands, CTS lifecycle, the UI-marshalled 2000-cap `AppendLog`, and the shared `ProcessProgress`
ETA mapper) and have Step3ProcessViewModel, StepUpscaleViewModel and the dedup scan adopt it; collapse
the four verbatim `AppendLog` copies into one `RunLog` helper; add `IOperationViewModel` (Key, Title,
Description, HelpTopic, ParametersSummary, Run, IsRunEnabled, RunDisabledReason, Report) with thin
adapters over the three existing step view models. Service layer (WorkflowRunner/IWorkflowItemHandler)
untouched. Source: `docs/ui-refactor-plan.md` §3, §4 Phase 1.

**Requirements**: TBD
**Depends on**: Nothing (first phase)
**Success Criteria** (what must be TRUE):

  1. `dotnet build Synapic.Net.sln` exits 0 with 0 warnings and 0 errors
  2. `dotnet test Synapic.Net.sln -c Release` passes with zero failures (no test weakened or skipped)
  3. `git diff --stat` for the phase touches only files under `src/Synapic.Main/ViewModels/` and `src/Synapic.Main/Services/` — no `.axaml` view file changed
  4. `RunStateViewModel` exists in `src/Synapic.Main/ViewModels/Operations/` and Step3 + StepUpscale + dedup scan inherit or compose it
  5. Exactly one implementation of the `while (LogLines.Count > 2000)` cap remains in ViewModels/

**Plans:** 4

Plans:

**Wave 1**
- [ ] `01-01` — Create RunLog + RunStateViewModel; Step3 and StepUpscale adopt the base

**Wave 2** *(blocked on Wave 1 completion)*
- [ ] `01-02` — Dedup adopts the base; MainWindowViewModel log cap delegates to RunLog
- [ ] `01-03` — IOperationViewModel + three thin adapters + contract test

**Wave 3** *(blocked on Wave 2 completion)*
- [ ] `01-04` — Phase gate sweep (build/test/scope/duplication gates + decision coverage)

**Cross-cutting constraints:**

- D-06: zero diffs under src/Synapic.Main/Services/ and zero .axaml diffs

### Phase 2: Add operation template and dashboard

**Goal:** Build the shared operation template and the dashboard: `OperationLayout.axaml` with the three
regions (Region A shared `DataSourceStrip` bound to one shell-owned source; Region B Parameters via
per-mode DataTemplates; Region C Output = shared `RunStateBar` + per-mode Report template);
`DashboardView` with the four panels (Settings, Tag, Dedup, Upscale) replacing the start screen, with
`ShellViewModel.Current == null` meaning dashboard and the app cold-starting there; delete the dedup
duplicate source card and `PrefillDedupSource()`; rewrite the pinned navigation tests to the new
`ui-design.md` §10 pins. Source: `docs/ui-design.md` §2, §3, §4, §6; `docs/ui-refactor-plan.md` §4 Phase 2.

**Requirements**: TBD
**Depends on:** Phase 1
**Success Criteria** (what must be TRUE):

  1. Cold start lands on the dashboard with exactly four panels (Settings, Tag, Dedup, Upscale), each opening its view
  2. A headless layout test asserts the same three regions in order `DataSource → Parameters → Output` on tagging, dedup and upscale routes
  3. Source configured inside Tag is shown identically in Dedup and Upscale (single shared instance)
  4. `UiLayoutAuditTests` passes at 900×600, 1024×700, 1280×800, 1600×900 for dashboard + all three routes, including the stacked variant
  5. `dotnet build` 0 warnings/errors and the full test suite green (rewritten pins only, no coverage removed)

**Plans:** 5

Plans:

**Wave 1**
- [ ] `02-01` — OperationLayout (three regions) + shared RunStateBar, hosted for the three routes

**Wave 2** *(blocked on Wave 1 completion)*
- [ ] `02-02` — DataSourceStrip in Region A; delete dedup's parallel source path (PrefillDedupSource)
- [ ] `02-03` — DashboardView (4 panels) + ShellViewModel.Current; cold start on dashboard

**Wave 3** *(blocked on Wave 2 completion)*
- [ ] `02-04` — Rewrite pinned navigation tests to §10 criteria 1–3; extend layout audit (criterion 5)

**Wave 4** *(blocked on Wave 3 completion)*
- [ ] `02-05` — Phase gate sweep (criteria 1, 2, 3, 5, 9 + decision coverage)

### Phase 3: Collapse navigation and retire settings dialogs

**Goal:** Delete the legacy navigation chrome and consolidate settings: remove the numbered sidebar,
the Back/Next/StartOver action bar, the start-screen chooser and the step DataTemplates from
MainLayout; retire `EngineSettingsDialog`/`DedupSettingsDialog`/`UpscaleSettingsDialog` — parameter
panels live only in Region B; slim `WizardViewModel` into (or replace by) `OperationShellViewModel`;
`MainWindowViewModel` loses its route switches (RouteTitle/CreateSettingsDialog derive from `Current`);
dedup Apply gains `IsApplying` + indeterminate bar + enabled Stop (closes `UI-REVIEW.md` carried-forward
finding #1). Source: `docs/ui-design.md` §5, §6, §9, §10; `docs/ui-refactor-plan.md` §4 Phase 3.

**Requirements**: TBD
**Depends on:** Phase 2
**Success Criteria** (what must be TRUE):

  1. No operation settings dialog class remains in the tree; every operation parameter is reachable inline in Region B
  2. No sidebar, Back/Next/StartOver, or `HomeRoute`/`IsDedupRoute`/`IsUpscaleRoute` booleans remain in MainLayout/MainWindowViewModel/WizardViewModel
  3. Dedup Apply shows a busy state with Stop for the whole operation duration (UI-REVIEW finding #1 closed)
  4. All nine `ui-design.md` §10 acceptance criteria pass item-by-item
  5. `dotnet build` 0 warnings/errors, full suite green, layout audit green at 4 window sizes × 4 routes

**Plans:** 4

Plans:

**Wave 1**
- [ ] `03-01` — Retire the three settings dialogs; params inline in Region B; rewrite dialog pins

**Wave 2** *(blocked on Wave 1 completion)*
- [ ] `03-02` — Delete sidebar/Back-Next/step chain; ShellViewModel.Current is the only navigation
- [ ] `03-03` — Dedup Apply busy state (IsApplying + Stop) closes UI-REVIEW finding #1

**Wave 3** *(blocked on Wave 2 completion)*
- [ ] `03-04` — Final gate: all nine ui-design §10 criteria + roadmap gates, item-by-item
