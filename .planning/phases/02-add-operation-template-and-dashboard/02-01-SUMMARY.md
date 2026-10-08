---
phase: 02-add-operation-template-and-dashboard
plan: 01
subsystem: ui
tags: [avalonia, xaml, operation-template, run-state-bar, strangler]

# Dependency graph
requires:
  - phase: 01-extract-run-state-and-operation-contract
    provides: RunStateViewModel (progress/ETA/log/start) + IOperationViewModel (plan 02-03 consumes)
provides:
  - OperationLayout — the three-region template (DataSource → Parameters → Output), hosted on all three routes
  - RunStateBar — the shared run surface bound to RunStateViewModel
  - The step chain moved inside the template's Output region (Phase 3 deletes it from there)
affects: [02-02 DataSourceStrip into Region A, 02-03 dashboard, 02-04 layout audit, 03 collapse navigation]

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "The template dispatches its regions through per-mode DataTemplates over the existing panels/pages; no mode logic in the layout"
    - "Legacy content is hosted inside the new frame (strangler), never duplicated: one instance per view"

key-files:
  created:
    - src/Synapic.Main/Views/Operation/OperationLayout.axaml
    - src/Synapic.Main/Views/Operation/OperationLayout.axaml.cs
    - src/Synapic.Main/Views/Operation/RunStateBar.axaml
    - src/Synapic.Main/Views/Operation/RunStateBar.axaml.cs
  modified:
    - src/Synapic.Main/Views/MainLayout.axaml

key-decisions:
  - "The step chain moved into OperationLayout's Output region instead of rendering beside it — one instance of every view, so the pinned tests keep finding exactly one of each control"
  - "Regions stack in one column (the design's narrow/stacked form): it preserves the reading order at every width and keeps the 900x600 layout audit green; the wide two-column form needs a breakpoint mechanism Phase 2 does not have"
  - "Tagging's Region B stays empty until Phase 3: hosting Step2Engine now would put a second EngineModelPicker on screen while step 1 renders, and the pinned WorkflowOrderTests asserts exactly one"
  - "RunStateBar carries the base's StartCommand only; Pause/Resume/Abort and Stop are mode commands the Phase 1 base never declared, and D-05 names 'primary action' for the bar"

patterns-established:
  - "Region slot = ContentControl + DataTemplates over the mode's existing view; the frame owns position, the mode owns content"

requirements-completed: []

# Metrics
duration: 14min
completed: 2026-10-08
---

# Phase 2 Plan 01: Operation template + shared RunStateBar Summary

**One three-region template now hosts every mode — Data source → Parameters → Output, in that document order — with the shared RunStateBar bound to Phase 1's RunStateViewModel, all three routes rendering through it, zero test edits, and the full suite green**

## Performance

- **Duration:** 14 min
- **Started:** 2026-10-08T22:00:30Z
- **Completed:** 2026-10-08T22:14:00Z
- **Tasks:** 2
- **Files created:** 4
- **Files modified:** 1

## Accomplishments

- [OperationLayout.axaml](src/Synapic.Main/Views/Operation/OperationLayout.axaml) has exactly three regions in order — `dataSourceRegion` (L24), `parametersRegion` (L35), `outputRegion` (L60) — each a named Border slot with a ContentControl. The layout holds no mode logic: Region B dispatches to the existing `DedupSettingsPanel`/`UpscaleSettingsPanel` and Region C dispatches to `RunStateBar` for run steps plus the mode's existing page.
- [RunStateBar.axaml](src/Synapic.Main/Views/Operation/RunStateBar.axaml) binds the Phase 1 base by name — `ProgressPercent`, `ProgressText`, `EtaText`, `CurrentFile`, `IsRunning` (hides Start while a run is in flight), `IsIdle`, `StartCommand`, `LogLines` — and is hosted in Region C only for view models that own a run, so a setup step never grows an empty progress bar.
- [MainLayout.axaml:297](src/Synapic.Main/Views/MainLayout.axaml#L297) now renders `<op:OperationLayout DataContext="{Binding Wizard.CurrentStep}" />`: the step DataTemplate chain moved *into* the template's Output region, so the sidebar, Back/Next action bar, start screen and footer behave exactly as before while every mode renders through the frame.
- No duplication of any view: the legacy pages are hosted inside the frame (one instance each), which is why the pinned tests still find exactly one `EngineModelPicker`, one `SourceStatusStrip` and one of each route-command button.

## Task Commits

1. **Task 1: OperationLayout + RunStateBar** — `19c4dff7` (feat)
2. **Task 2: host the template on the three routes** — `402a1f5e` (feat)

**Plan metadata:** `docs(02-01): complete operation template plan 01` (this commit)

## Files Created/Modified

- `src/Synapic.Main/Views/Operation/OperationLayout.axaml` — the three regions, region slots, per-mode DataTemplates, region chrome (no new colours, no new fonts)
- `src/Synapic.Main/Views/Operation/OperationLayout.axaml.cs` — `InitializeComponent` only
- `src/Synapic.Main/Views/Operation/RunStateBar.axaml` — the shared run surface (progress, ETA, current file, primary action, run log)
- `src/Synapic.Main/Views/Operation/RunStateBar.axaml.cs` — `InitializeComponent` only
- `src/Synapic.Main/Views/MainLayout.axaml` — the six step DataTemplates replaced by the template host (net −14 lines)

## Verification

| Check | Result |
|-------|--------|
| `dotnet build Synapic.Net.sln --nologo -v minimal` | exit 0 — 0 Warning(s), 0 Error(s) (run twice: before and after the last XAML edit) |
| `dotnet test Synapic.Net.sln -c Release --nologo` | exit 0 — 442 passed / 0 failed / 0 skipped (Shared 6, Integration 5, Main 431) |
| `dotnet test tests/Synapic.Main.Tests -c Release` (plan gate) | exit 0 — 431 passed, 0 failed — same count as before the plan, so no pinned fact was rewritten |
| `grep -n "dataSourceRegion\|parametersRegion\|outputRegion"` | 3 markers, document order L24 → L35 → L60 |
| `grep -n "OperationLayout" src/Synapic.Main/Views/MainLayout.axaml` | present (L297 is the host) |
| Region B element reuse | `<steps:DedupSettingsPanel />` L49, `<steps:UpscaleSettingsPanel />` L52 — elements, not copied controls |
| `git status --porcelain tests/` | 0 — no test file modified or deleted |
| New colour literals / FontFamily in the new views | 0 hex colours; only the existing `Consolas,monospace` log font |
| Code-behind beyond `InitializeComponent` | none (1 occurrence per file) |
| Layout audit (`UiLayoutAuditTests`, 900×600 → 1600×900, tagging steps 1–4 + dedup + upscale) | green — the routes now render through the regions and clip nothing |

## Decisions Made

- **The legacy pages are hosted *inside* the Output region, not beside it.** Rendering them next to the frame would have duplicated every run control, and the pinned tests (`Assert.Single(EngineModelPicker)`, the three `OpenSettingsCommand` buttons, the two route-command buttons per mode) would have caught it. Because the chain moved rather than copied, all 431 tests pass with no edits at all.
- **The regions stack in one column.** The design's wide form is two columns (Parameters left, Output right) with the stacked form below ~1000 px. Avalonia has no XAML breakpoint mechanism, and the alternatives (a code-behind size hook — barred by this plan — or an Avalonia.Labs responsive panel — a new dependency) are out of scope this wave. The single column keeps the mandated reading order at every width and keeps the 900×600 audit green; the two-column form and its switch point belong to 02-04's audit extension.
- **Tagging's Region B is intentionally empty for now.** `EngineSettingsDialog` hosts `Step2Engine`, which contains the one `EngineModelPicker`; hosting it in Region B today would put a second picker on screen while step 1 renders and break `WorkflowOrderTests` (which the plan requires to pass unmodified). It moves in when Phase 3 retires the dialog.
- **RunStateBar's primary action is the base's `StartCommand` only.** Phase 1's base declares Start; Pause/Resume/Abort (tagging) and Stop (upscaling) are mode commands, and D-05 spells the bar's job as "progress, ETA, current file, log, primary action". The bar hides Start while running; the mode pages keep their Stop until Phase 3.

## Deviations from Plan

1. **Stacked regions instead of the wide two-column arrangement** (see Decisions) — the plan's own acceptance criteria require the three regions in order, not a column split; documented here so 02-04 can pin the wide variant deliberately.
2. **Tagging's Region B template deferred** — the plan's action text asked for the tagging settings view in Region B; the plan's acceptance criteria name only the dedup and upscale panels, and hosting the tagging panel now would break a pinned test the same plan requires to pass unmodified. Deferred to Phase 3 with its dialog.
3. **RunStateBar has no Pause/Resume/Stop buttons** — the base declares none of them (see Decisions); the bar carries the primary action, which is what D-05 and the plan's acceptance criteria require.
4. **Known strangler redundancy, called out rather than hidden:** for the three run steps the run surface is on screen twice — the shared bar and the legacy run page hosted below it inside the same region. Phase 3 removes the legacy page; until then the bar is the surface that survives, and it is deliberately the one that renders the shared state.

## Issues Encountered

None — the build was green on the first attempt after the views were written, and the suite passed on the first run with no test edits.

## User Setup Required

None.

## Next Phase Readiness

- Region A is an empty named slot waiting for 02-02's `DataSourceStrip`; Region B's dedup/upscale templates prove the dispatch pattern the tagging panel will reuse.
- The three regions render on all three routes (the tagging route's hosted `Step1Datasource` is what the pinned test finds its `EngineModelPicker` in; the dedup and upscale routes pass the layout audit through the regions), so 02-04 can assert the region order by name.
- `RunStateBar` is the only view binding `RunStateViewModel`; 02-03's dashboard panels and Phase 3's deletions both build on it.

---
*Phase: 02-add-operation-template-and-dashboard*
*Completed: 2026-10-08*

## Self-Check: PASSED
