---
gsd_state_version: 1.0
milestone: v1.0
milestone_name: milestone
status: executing
stopped_at: 03-05 final gate refresh; Phase 3 remains open on criteria 6 and 8
last_updated: "2026-10-10"
last_activity: 2026-10-10
progress:
  total_phases: 3
  completed_phases: 2
  total_plans: 14
  completed_plans: 14
  percent: 67
---

# Project State

## Project Reference

See: .planning/PROJECT.md (updated 2026-10-09)

**Core value:** Pick a source, run Tag/Dedup/Upscale, get correct results written back — without fighting the UI.
**Current focus:** Phase 03 — collapse-navigation-and-retire-settings-dialogs

## Current Position

Phase: 03 (collapse-navigation-and-retire-settings-dialogs) — EXECUTING
Plan: 5 of 5 executed (03-01 through 03-05); 03-05 gate verdict remains incomplete
Status: Executed, remediation required
Last activity: 2026-10-09

Progress: 14 of 14 plans executed — Phase 1 4/4, Phase 2 5/5, Phase 3 5/5 plans executed but phase acceptance remains open (03-05 §10 criteria 6 and 8 ✗)

**Suite is currently green (verified by 03-05):** `dotnet test Synapic.Net.sln -c Release --no-restore --nologo` exits 0 → Shared 6/6, Integration 5/5, Main **463/463** (0 failed, 0 skipped); `dotnet build Synapic.Net.sln --no-restore --nologo -v minimal` → 0 warnings / 0 errors. Test-count history: 442 (01-04) → 441 Main-only at 02-05 plus Integration 5 and Shared 6 → 446 (03-01) → 451 (03-03) → 460 (03-02) → 463 (this resumed 03-05 run). Empty-state and cross-route F1 tests now pass. A green suite does not mean the phase gate passed: §10 criteria 6 (not every empty-state branch has behavioral evidence) and 8 (preservation checklist has partial and unsupported claims) remain incomplete; see the per-claim 37-item map in 03-05-SUMMARY.md.

**State of the redesign after 03-05:** one navigation model (`ShellViewModel.Current`, null = dashboard); the numbered sidebar and step chain are deleted, operation settings dialogs are gone, and parameters are inline in Region B; §5 settings view is recorded by 03-04; layout audit is green at four sizes for the dashboard and all modes. The F1 matrix now passes on Dashboard, Tag, Dedup and Upscale. Remaining gate work: criterion 6's untested zero-result/all-failed branches and criterion 8's partially evidenced or unsupported feature-preservation claims (see 03-05-SUMMARY.md); Phase 3 is not complete.

## Performance Metrics

**Velocity:**

- Total plans executed: 14
- Average duration: 25 min
- Total execution time: 5.4 hours
- Last recorded plans: 03-01 — 42 min, 2 tasks, 25 files · 03-02 — 58 min, 2 tasks, 46 files · 03-03 — 28 min, 2 tasks, 4 files · 03-04 — 20 min, 3 tasks, 2 files · 03-05 — 35 min, 3 tasks, 1 planning file; gate ran but verdict incomplete

**By Phase:**

| Phase | Plans | Total | Avg/Plan |
|-------|-------|-------|----------|
| 01-extract-run-state-and-operation-contract | 4 | 4 | 8 min |
| 02-add-operation-template-and-dashboard | 5 | 5 | 29 min |
| 03-collapse-navigation-and-retire-settings-dialogs | 5 | 5 | 31 min |

**Phase 1 gate sweep (01-04):** all five ROADMAP success criteria ✓ — build 0/0, full solution 442 passed / 0 failed / 0 skipped, 0 `.axaml` and 0 `Services/` diffs, one log-cap file, three inheritors

**Phase 2 gate sweep (02-05):** all five ROADMAP success criteria ✓ — build 0/0 (incl. `--no-incremental`), suite Shared 6/5+0, Integration 5/0, Main 441/0, audit 19/19 at four sizes incl. stacked, decision coverage passed 7/7, `PrefillDedupSource` absent from source, mapping table accounts for all six removed test names

**Phase 3 sweeps:** 03-01 (dialogs retired, params inline), 03-02 (navigation collapse), 03-03 (dedup Apply busy + Stop) and 03-04 (§5 settings record: 8 §5 pins + 22 audit facts, no source changed) are recorded. Resumed 03-05 verification: build 0/0, full solution 6 + 5 + 463 passed, focused UI/F1/settings/server sweep 143/143, layout audit 22/22, F1 route matrix 1/1; decision coverage 5/5. Criterion 7 now passes. Criterion 6 remains ✗ because zero-result Daminion and all-failed Upscale branches are not pinned; criterion 8 remains ✗ with the full 37-row map now recorded (23 ✓, 12 △, 2 ✗). Phase 3 acceptance remains open.

## Accumulated Context

### Roadmap Evolution

- 2026-10-08: .planning bootstrapped; Phases 1–3 added from docs/ui-refactor-plan.md gates
- 2026-10-09: **Phase 3 gained unplanned work.** The §5 app-wide settings view (the dashboard Settings panel) landed with 03-02 before it had an owner. Plan `03-04` later adopted it and recorded section-by-section evidence in 03-04-SUMMARY.md; `03-05` verifies that record. The plan numbering now follows execution order.
- 2026-10-09: **03-05 final gate re-run; still not passed.** Build 0/0, solution tests 6 + 5 + 463, focused sweep 143/143, audit 22/22, all-route F1 matrix 1/1, decision coverage 5/5 and Services scope empty. Criterion 7 now passes; criterion 6 lacks complete empty-branch coverage, criterion 8 has a per-item 37-row map but retains 12 partial and 2 unsupported entries, so Phase 3 success criterion 4 remains ✗. See 03-05-SUMMARY.md; no Phase 3 complete checkbox is set.

### Decisions

Decisions are logged in PROJECT.md Key Decisions table.
Recent decisions affecting current work:

- UI SSOT is docs/ui-design.md; docs/ui-refactor-plan.md defines the phase gates (D1–D7 decisions recorded there)
- Service layer (WorkflowRunner/IWorkflowItemHandler) is out of scope — presentation layer only
- [Phase 01]: RunStateViewModel owns the shared Start command and the begin/end/cancel/pause/resume helpers; each operation declares only the run commands it had, so no new enabled states appear — Gives Phase 2 one run-state shape to bind while keeping tagging and upscaling behavior identical
- [Phase 01]: Dedup keeps ScanCommand as its primary run; the inherited StartCommand delegates to the same scan body — Preserves every dedup binding while giving Phase 2 one run shape for all three modes
- [Phase 01]: IOperationViewModel extends INotifyPropertyChanged and the adapters re-raise all nine members on any wrapped change — A template binding a live IsRunEnabled/RunDisabledReason must never read a stale gate
- [Phase 01]: TagOperationViewModel takes the results step as an optional argument for Report — Step3ProcessViewModel exposes no result surface and plan 01-03 may not modify it
- [Phase 01]: D-03 is satisfied as one append-and-trim implementation for operation logs; the shell's UiLogEvent log shares the cap via RunLog.Trim and is not one of the four verbatim copies — Phase 2 must not route the shell log through the string helper
- [Phase 02]: The legacy step chain was moved *into* OperationLayout's Output region rather than rendered beside it — one instance per view keeps every pinned single-instance assertion true; Phase 3 deletes the legacy page from inside the frame
- [Phase 02]: Regions stack in one column for now (the design's stacked form) — Avalonia has no XAML breakpoint; the wide two-column form and its switch point belong to 02-04's audit extension
- [Phase 02]: Dedup's source properties read and write Step 1's state directly (private fields are only the standalone fallback), so deleting PrefillDedupSource removed a copy instead of adding a second path — Gives the phase exactly one source instance, verifiable at the strip
- [Phase 02]: Region A's shared source is handed to OperationLayout by MainLayout.axaml.cs (a binding written on the element resolves against the element's own DataContext — the mode's step view model — and silently yields null)
- [Phase 02]: Dashboard panels are always enabled (D6); the Settings panel hosts the existing settings content (source panel + model picker) until Phase 3 builds §5's settings view, and ShellViewModel.Current is written only by the route commands
- [Phase 02]: Navigation pins are renames-with-increases, not silent rewrites — each old fact name maps to its replacement in 02-04-SUMMARY.md, and the invalidated D6 gating fact is replaced by one asserting both halves (entry free, run gated with a reason)
- [Phase 02]: Region order is pinned twice — document order in the functional tests (criterion 2), geometry in the audit facts — because document order alone cannot catch a region laid out in the wrong place
- [Phase 02]: The gate sweep's `PrefillDedupSource` check is recorded source-only (`--include=*.cs --include=*.axaml`): the bare grep also matches stale gitignored DLLs under `bin/obj/`, which are build outputs, not code — Phase 3's chrome-deletion greps should refine the same way and say so
- [Phase 02]: A `ContentControl` whose content matches no `DataTemplate` prints the content's `ToString()`, so every region host ends with a last, inheritance-matching empty `DataTemplate` (region hosts may render nothing, never a type name) — a new step view model that lands without a template now fails `No_step_renders_a_view_model_type_name_as_text` instead of shipping
- [Phase 02]: `ShellViewModel.Open`/`Home`/`Operations` have no caller yet (the panels bind the route commands; help reads `ContextHelpTopic`) — they are Phase 3's rendered-navigation surface, and `Shell.Current` is written by the route machine, so Phase 3 should make Shell the entry point and let `Route` go
- [Phase 02]: A pin's name must state what its assertions can actually fail on — auditing the four rewritten test files name-first caught a "resumes where it was" fact whose assertions show a fresh route entry, and a D6 fact still asserting the retired `CanStartRoute` route gate (no view binds the property; Phase 3 deletes it, and `GoHome`'s "returning resumes" doc was wrong for the same reason)
- [Phase 03]: `WizardViewModel` was replaced by `OperationShellViewModel` rather than trimmed — nothing left in it was navigation-free except "the mode's content" plus the cross-cutting wiring the step view models expected it to own (the run's tag-field gate, the run lock, the commit-to-store on leaving a mode)
- [Phase 03]: Navigation is derived, never stored — `IsDashboardVisible`/`IsOperationVisible`/`OperationTitle`/`Breadcrumb`/`ContextHelpTopic`/`IsNavigationLocked` all compute from `Shell.Current`, so a mode change is one write and every surface follows; `RouteTitle` became `OperationTitle` + a `Breadcrumb` because the header has to say *where you are* once the sidebar's active entry is gone
- [Phase 03]: The template's four slots are StyledProperties pushed by `MainLayout.axaml.cs` on `Shell.Current` changes, because a binding written on `OperationLayout` resolves against its own DataContext (the mode's step view model) and is silently null
- [Phase 03]: Gating lives on the action (D6) — the three "Next refuses to leave Datasource" facts became `IsRunEnabled` + `RunDisabledReason` facts on the operation adapters, and entering a mode is free
- [Phase 03]: Deleting chrome means deleting its styling too — the orphan sidebar styles (`Button.navItem`/`.active`, `Button.navMode`, `TextBlock.navGroup`) were removed from `App.axaml`, and a re-introduced sidebar entry now fails the absence pins in `WorkflowOrderTests`/`MainLayoutShellTests` as well
- [Phase 03]: A fact that names a device on a session before the shell is built is testing the engine form's availability rule, not the behaviour it names — the two `ServerDetectionTests` device facts now state the device after construction (the first green run of those tests on this machine)
- [Phase 03]: `HelpTopics.ForStepIndex` still maps 0–5 because the compiled help's topic files are named for the old steps; the adapters read the entry their mode maps to, and renaming the topics is a docs-pass task, not this phase's
- [Phase 03]: **The §5 settings view is owned by plan `03-04`** (was unplanned). It was written as untracked files (`SettingsViewModel.cs`, `Views/Dashboard/SettingsPanel.axaml(.cs)`, `SettingsViewTests.cs`, the `ConfigService.UiSettings` defaults, `SynapicLog`'s level switch, the startup theme apply) that `MainWindowViewModel` then depended on, so 03-02's commit carried them. 03-04, written 2026-10-09 after delivery, states the must-haves and records the evidence in 03-04-SUMMARY.md — five sections in order, nothing configured in two places, per-section reach, persistence — and the `03-05` gate verifies that record instead of sweeping §5 as an extra item
- [Phase 03]: **A plan added mid-phase renumbers the gate rather than landing after it** — the §5 record took `03-04` and the final gate became `03-05`, keeping the phase's last plan its gate (as in `01-04`/`02-05`) and plan order equal to execution order; the "03-04 final gate" references in ROADMAP, PROJECT.md and the three earlier summaries were repointed in the same change
- [Phase 03]: **A record may not map a nearby control onto a missing one** — 03-04-SUMMARY.md records two §5 rows ✗ (no health-details surface; §5's wide section-nav layout is not implemented, the single scrolling column being the form §5 allows at every size), because a technicality pass would leave the next reader unable to tell what §5 is missing
- [Phase 03, unplanned]: The Defaults section seeds a session that has no saved engine state, and the shell applies it before the mode view models are built — which means a session handed to the shell with a device already chosen has that choice rewritten. Harmless today (a real first run passes a fresh session), but it is why the two `ServerDetectionTests` device facts read as failures until they stated the device after construction: worth a guard on `ApplyDefaultsToNewSession` if a second caller ever hands in a configured session
- [2026-10-10, outside a plan]: The dashboard grid is the design's 2×2 at last — the four panels are placed by explicit `Grid.Row`/`Grid.Column` rather than by a `UniformGrid`, because a UniformGrid gives every cell its tallest child's height and the Settings panel still hosts the §5 form inline, so the three operation cards would have become as tall as that form. `DashboardView` stacks them into one reading-order column below 600 px per panel (~1200 px of dashboard width); §7's "~1000 px" was a window figure and is corrected in the SSOT as decision **D8**. The audit pins both forms, and each form's fact asserts the switch rule (`DashboardView.MinPanelWidth`) before measuring, so neither can pass on the other form's geometry — the same failure mode the earlier name-first pin audit found in facts that never checked which screen they were measuring
- [2026-10-10, outside a plan]: `Step2EngineViewModel`'s constructor loaded its state from two sources that fought: it read the engine store and then read the session back over it, and the session writes each property change made while loading were what hid that — and what threw a freshly seeded session away (`SettingsViewTests.Defaults_seed_a_session_that_has_no_saved_engine_state` caught it: the form pushed its own 0.3 over the Defaults section's 0.6). Now exactly one source fills the form per launch — the store when it has a last run, the session otherwise — `HydrateFromStore`'s result says which, no property change writes to the session while `_loading`, and the loaded, corrected state is pushed once at the end of loading. The device rule is unchanged: a device this machine cannot offer is still replaced by one it can, from whichever source asked for it
- [2026-10-10, outside a plan]: `EngineSettingsStore` wrote its two double thresholds with the process culture and read them the same way, so the registry text was locale-specific (`"0,25"` on this machine) and a later run in another regional format read a different number or none — a comma-decimal value read under a dot-decimal culture takes the comma as a group separator, never as the decimal point it was written to mean. The writer now formats invariantly and the reader parses invariantly with a comma accepted as the decimal separator (deterministic: nothing in that value is ever a group separator), which keeps a value an earlier build wrote meaningful. Pinned from both ends — raw stored text, cross-culture read, and a legacy comma value — by `EngineSettingsStoreTests.Saved_thresholds_survive_a_culture_change`, which failed before the change (recorded: expected `"0.42"`, actual `"0,42"`). A downgrade to a build before this is still wrong and is not a supported path
- [2026-10-10, outside a plan — user-requested layout]: The operation view's left column is the mode switch: Tagging, Dedup and Upscale as buttons, the open one lit, that mode's settings in the column beside it and Output full width below (SSOT updated as **D9**). The rail is not the retired sidebar — no numbering, no step chain, no stored selection: its lit state is derived from `Shell.Current` like the rest of the chrome, and pressing the open mode's own button keeps it open rather than toggling the view empty. It is the *second binding* of the same route commands, so `MainLayoutShellTests`' "each operation is entered from exactly one place" assertions now count what is on screen (the rail is off screen on the dashboard), and `WorkflowOrderTests.Mode_rail_switches_the_mode_and_lights_only_the_open_one` pins switching, the single lit button, the rail sitting left of the settings, and the rail leaving the screen on Home. Two things the verification found, both fixed before anything was called working: hidden templates are not realized, so a fact cannot count the rail before a mode has been opened (it now asserts the rail off screen *after* Home instead); and the rail had to be a fixed 200 px rather than `Auto`, because an Auto column is measured at infinite width — the rail's wrapping hint line took the room it wanted and left the settings column 144 px at a 900 px window, which `UiLayoutAuditTests` reported as clipped tagging sliders

## Session Continuity

Last session: 2026-10-09T15:08:26.000Z
Stopped at: 03-05 final gate refresh; phase acceptance incomplete
Resume file: None

Next: close the two remaining 03-05 gate gaps before considering Phase 3 complete: (1) add evidence-backed tests for untested empty-result branches (notably zero-result Daminion Tag/Dedup and all-failed Upscale) for criterion 6; (2) resolve the two unsupported historical claims and strengthen the partial rows in the 37-item criterion-8 map. Cross-route F1 key dispatch passes. The §5 record 03-04 has been verified by 03-05; its health-details and wide section-navigation gaps remain explicit. Gate record: `.planning/phases/03-collapse-navigation-and-retire-settings-dialogs/03-05-SUMMARY.md`. All 14 plans have been executed, but Phase 3 remains NOT PASSED. The dashboard's wide 2×2 layout is implemented (2026-10-10, D8); the help corpus's old step names remain a carried deviation.
