---
gsd_state_version: 1.0
milestone: v1.0
milestone_name: milestone
status: executing
stopped_at: Completed 03-02-PLAN.md
last_updated: "2026-10-09T11:21:06.000Z"
last_activity: 2026-10-09
progress:
  total_phases: 3
  completed_phases: 2
  total_plans: 13
  completed_plans: 12
  percent: 67
---

# Project State

## Project Reference

See: .planning/PROJECT.md (updated 2026-10-09)

**Core value:** Pick a source, run Tag/Dedup/Upscale, get correct results written back — without fighting the UI.
**Current focus:** Phase 03 — collapse-navigation-and-retire-settings-dialogs

## Current Position

Phase: 03 (collapse-navigation-and-retire-settings-dialogs) — EXECUTING
Plan: 3 of 4 (03-01, 03-02, 03-03 complete; 03-04 the final gate)
Status: Ready to execute
Last activity: 2026-10-09

Progress: 12 of 13 plans complete — Phase 1 4/4, Phase 2 5/5, Phase 3 3/4 (03-04 the final gate)

**Suite is fully green (as of 03-02, commit `805a79f`):** `dotnet test Synapic.Net.sln -c Release` exits 0 → Shared 6/6, Integration 5/5, Main **460/460** (0 failed, 0 skipped). Build `0 Warning(s) 0 Error(s)`. Test-count history, so an older figure is never read as current: 442 (01-04) → 441 main-only at 02-05 plus the 5 integration and 6 shared → 446 (03-01) → 451 (03-03) → 460 (03-02). The two `ServerDetectionTests` device failures 03-01 recorded as pre-existing are fixed in 03-02 — their fixture stated the device before the shell was built, and the engine form's availability rule normalized it away on a CUDA-less machine — so the suite has no known failures left.

**State of the redesign after 03-02:** one navigation model (`ShellViewModel.Current`, null = dashboard); the numbered sidebar, the Back/Next/StartOver action bar and the wizard step chain are deleted from `src/`, with the greps returning no matches; the three settings dialogs are deleted and every operation parameter is inline in Region B; layout audit green at 900×600 / 1024×700 / 1280×800 / 1600×900 for the dashboard and all three modes. Remaining: 03-04's item-by-item §10 sweep.

## Performance Metrics

**Velocity:**

- Total plans completed: 12
- Average duration: 25 min
- Total execution time: 5.1 hours
- Last plans: 03-01 — 42 min, 2 tasks, 25 files · 03-02 — 58 min, 2 tasks, 46 files · 03-03 — 28 min, 2 tasks, 4 files

**By Phase:**

| Phase | Plans | Total | Avg/Plan |
|-------|-------|-------|----------|
| 01-extract-run-state-and-operation-contract | 4 | 4 | 8 min |
| 02-add-operation-template-and-dashboard | 5 | 5 | 29 min |
| 03-collapse-navigation-and-retire-settings-dialogs | 3 | 4 | 43 min |

**Phase 1 gate sweep (01-04):** all five ROADMAP success criteria ✓ — build 0/0, full solution 442 passed / 0 failed / 0 skipped, 0 `.axaml` and 0 `Services/` diffs, one log-cap file, three inheritors

**Phase 2 gate sweep (02-05):** all five ROADMAP success criteria ✓ — build 0/0 (incl. `--no-incremental`), suite Shared 6/5+0, Integration 5/0, Main 441/0, audit 19/19 at four sizes incl. stacked, decision coverage passed 7/7, `PrefillDedupSource` absent from source, mapping table accounts for all six removed test names

**Phase 3 partial sweeps:** 03-01 (dialogs retired, params inline) and 03-02 (navigation collapse) each ended with build 0/0 and the full suite green at that commit's content; the phase gate itself is 03-04's, and the settings view (§5) is an extra item it must sweep — see the decision below.

## Accumulated Context

### Roadmap Evolution

- 2026-10-08: .planning bootstrapped; Phases 1–3 added from docs/ui-refactor-plan.md gates
- 2026-10-09: **Phase 3 gained unplanned work.** The §5 app-wide settings view (the dashboard Settings panel:
  inference server, appearance, logging, defaults, about) was built during the phase after 03-01 freed the
  panel of the source form and the model picker, but no plan in the roadmap owns it: 03-01's must-haves cover
  the *dialog retirement* and the inlining, and the Phase 3 goal only implies §5 via its source list. It is
  recorded in ROADMAP.md under "Unplanned work delivered during Phase 3" and must be swept by 03-04 (or a new
  plan added to the phase) before the phase is called complete.

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
- [Phase 03, unplanned]: **The §5 settings view has no owning plan.** It was written as untracked files (`SettingsViewModel.cs`, `Views/Dashboard/SettingsPanel.axaml(.cs)`, `SettingsViewTests.cs`, the `ConfigService.UiSettings` defaults, `SynapicLog`'s level switch, the startup theme apply) that `MainWindowViewModel` then depended on, so 03-02's commit carried them. 03-01's SUMMARY and UI-REVIEW.md describe it as delivered, but no ROADMAP plan lists it as a must-have — it is recorded as unplanned delivered work and 03-04 must verify §5 item by item (five sections present and in order, nothing configured in two places, values persist) rather than assume it is covered
- [Phase 03, unplanned]: The Defaults section seeds a session that has no saved engine state, and the shell applies it before the mode view models are built — which means a session handed to the shell with a device already chosen has that choice rewritten. Harmless today (a real first run passes a fresh session), but it is why the two `ServerDetectionTests` device facts read as failures until they stated the device after construction: worth a guard on `ApplyDefaultsToNewSession` if a second caller ever hands in a configured session

## Session Continuity

Last session: 2026-10-09T11:21:06.000Z
Stopped at: Completed 03-02-PLAN.md
Resume file: None

Next: Phase 3 plan 04 — the final gate: sweep all nine docs/ui-design.md §10 criteria and the roadmap gates item by item against the now-green suite (build 0/0, Main 460/460, audit at four sizes), **plus the unplanned §5 settings view** (five sections present and in order, nothing configured in two places, values persist — see the ROADMAP's "Unplanned work delivered during Phase 3"), and confirm the decision coverage 7/7. Carried deviations: the dashboard's wide 2×2 breakpoint form is not implemented, and the help corpus still numbers the old steps (`setup-guide.html`, `step*-*.html`) — a wording sweep. Open question for the phase gate: whether §5 gets swept by 03-04 or becomes a plan of its own.
