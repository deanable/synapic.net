---
gsd_state_version: 1.0
milestone: v1.0
milestone_name: milestone
status: executing
stopped_at: Completed 03-02-PLAN.md
last_updated: "2026-10-09T14:20:00.000Z"
last_activity: 2026-10-09
progress:
  total_phases: 3
  completed_phases: 2
  total_plans: 13
  completed_plans: 12
  percent: 75
---

# Project State

## Project Reference

See: .planning/PROJECT.md (updated 2026-10-08)

**Core value:** Pick a source, run Tag/Dedup/Upscale, get correct results written back — without fighting the UI.
**Current focus:** Phase 03 — collapse-navigation-and-retire-settings-dialogs

## Current Position

Phase: 03 (collapse-navigation-and-retire-settings-dialogs) — EXECUTING
Plan: 3 of 4 (03-01, 03-02, 03-03 complete; 03-04 the final gate)
Status: Ready to execute
Last activity: 2026-10-09

Progress: [█████████░] 92%

**Suite is fully green:** `dotnet test Synapic.Net.sln -c Release` → Shared 6/6, Integration 5/5, Main 460/460 (0 failed, 0 skipped). One navigation model: `ShellViewModel.Current` is the only state, and the sidebar, action bar and step chain are deleted from `src/` (compiler + greps). Build `0 Warning(s) 0 Error(s)`; layout audit green at 900×600 / 1024×700 / 1280×800 / 1600×900 for the dashboard and all three modes. The two `ServerDetectionTests` device facts recorded as pre-existing failures by 03-01 now pass: their fixture stated the device before the shell was built, and the engine form's availability rule normalized it away on a CUDA-less machine.

## Performance Metrics

**Velocity:**

- Total plans completed: 9
- Average duration: 17 min
- Total execution time: 2.6 hours
- Last plans: 02-03 — 55 min, 2 tasks, 6 files · 02-04 — 35 min, 3 tasks, 4 files · 02-05 — 20 min, 2 tasks, 1 file

**By Phase:**

| Phase | Plans | Total | Avg/Plan |
|-------|-------|-------|----------|
| 01-extract-run-state-and-operation-contract | 4 | 4 | 8 min |
| 02-add-operation-template-and-dashboard | 5 | 5 | 29 min |

**Phase 1 gate sweep (01-04):** all five ROADMAP success criteria ✓ — build 0/0, full solution 442 passed / 0 failed / 0 skipped, 0 `.axaml` and 0 `Services/` diffs, one log-cap file, three inheritors

**Phase 2 gate sweep (02-05):** all five ROADMAP success criteria ✓ — build 0/0 (incl. `--no-incremental`), suite Shared 6/5+0, Integration 5/0, Main 441/0, audit 19/19 at four sizes incl. stacked, decision coverage passed 7/7, `PrefillDedupSource` absent from source, mapping table accounts for all six removed test names
| Phase 03 P01 | 42 min | 2 tasks | 25 files |
| Phase 03 P02 | 58 min | 2 tasks | 46 files |
| Phase 03 P03 | 28 min | 2 tasks | 4 files |

## Accumulated Context

### Roadmap Evolution

- 2026-10-08: .planning bootstrapped; Phases 1–3 added from docs/ui-refactor-plan.md gates

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

## Session Continuity

Last session: 2026-10-09T14:20:00.000Z
Stopped at: Completed 03-02-PLAN.md
Resume file: None

Next: Phase 3 plan 04 — the final gate: sweep all nine docs/ui-design.md §10 criteria and the roadmap gates item by item against the now-green suite (build 0/0, Main 460/460, audit at four sizes), and confirm the decision coverage. Carried deviations: the dashboard's wide 2×2 breakpoint form is not implemented, and the help corpus still numbers the old steps (`setup-guide.html`, `step*-*.html`) — a wording sweep.
