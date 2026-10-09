---
phase: 03-collapse-navigation-and-retire-settings-dialogs
plan: 01
subsystem: ui
tags: [avalonia, settings, dialogs, parameters-region, pins, ui-design-10]

# Dependency graph
requires:
  - phase: 02-add-operation-template-and-dashboard
    provides: OperationLayout three regions (02-01), shared DataSourceStrip (02-02), DashboardView + Shell.Current (02-03)
provides:
  - The three operation settings dialogs deleted (Views/Settings removed; no type, file or reference remains in src/)
  - Tagging's full engine form is the Parameters region, so all three modes' parameters are inline in Region B
  - The dialog entry points (shell Settings command, the two page ⚙ buttons, the per-step OpenSettingsRequested hooks) removed
  - The five dialog-pinning facts rewritten to inline-parameter pins, plus an assembly-level "no dialog type" pin
affects: [03-02 collapsing navigation, 03-04 final gate, any future test that expects a settings window]

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "Criterion 4 is pinned twice: a behavioural pin (the form is a descendant of the parametersRegion Border inside the MainWindow) and a reflection pin (the three dialog types do not exist in the built assembly)"
    - "A retired host is only gone when its entry point is gone too: the page ⚙ buttons and the per-step OpenSettingsRequested hooks were deleted rather than left as no-op commands"

key-files:
  created: []
  removed:
    - src/Synapic.Main/Views/Settings/EngineSettingsDialog.axaml(.cs)
    - src/Synapic.Main/Views/Settings/DedupSettingsDialog.axaml(.cs)
    - src/Synapic.Main/Views/Settings/UpscaleSettingsDialog.axaml(.cs)
  modified:
    - src/Synapic.Main/ViewModels/MainWindowViewModel.cs
    - src/Synapic.Main/Views/MainLayout.axaml
    - src/Synapic.Main/Views/Operation/OperationLayout.axaml
    - src/Synapic.Main/Views/Wizard/StepDedup.axaml
    - src/Synapic.Main/Views/Wizard/StepUpscale.axaml
    - src/Synapic.Main/Views/Wizard/Step2TagSettings.axaml
    - src/Synapic.Main/ViewModels/Steps/Step2EngineViewModel.cs
    - src/Synapic.Main/ViewModels/Steps/StepDedupViewModel.cs
    - src/Synapic.Main/ViewModels/Steps/StepUpscaleViewModel.cs
    - tests/Synapic.Main.Tests/MainLayoutShellTests.cs
    - tests/Synapic.Main.Tests/WorkflowOrderTests.cs
    - tests/Synapic.Main.Tests/UiLayoutAuditTests.cs
    - tests/Synapic.Main.Tests/Step2EngineTests.cs

key-decisions:
  - "D-02 landed as deletion, not redirection: OpenSettings and CreateSettingsDialog are gone from MainWindowViewModel instead of being kept as a no-op, so the compiler — not a grep — proves no window can be built"
  - "The surviving header Settings entry is app-wide (ui-design §6.1): SettingsCommand goes to the dashboard's Settings panel, and the sidebar/action-bar entries bind the same command until 03-02 deletes them"
  - "Tagging's Parameters region is the whole Step2Engine form, which is what the retiring dialog hosted; the step-1 model picker and the Parameters form still ride one view model, so the 'one view model, two views' invariant survives the dialog"
  - "The two page ⚙ Settings buttons (StepDedup, StepUpscale) were deleted with their view-model hooks: leaving them would have shipped a button that opens nothing"

patterns-established:
  - "Retired-surface pins assert absence at two levels: the visual tree (no Window ancestor, region membership) and the assembly (GetType returns null for each removed type)"

requirements-completed: []

# Metrics
duration: 42min
completed: 2026-10-09
---

# Phase 3 Plan 01: Retire the operation settings dialogs Summary

**The three settings dialogs are gone from the source and from the built assembly, and tagging's engine form now renders inline in the operation template's Parameters region — build 0 warnings/0 errors, Main.Tests 444 passed with 2 pre-existing machine-dependent failures unrelated to this plan**

## Performance

- **Duration:** 42 min
- **Tasks:** 2
- **Files changed:** 25 changed, 6 deleted
- **Cases:** 442 → 446 in Synapic.Main.Tests (5 facts replaced, 1 gained; Δ +4)

## Accomplishments

- **The dialogs are gone, not disabled.** `src/Synapic.Main/Views/Settings/` and its six files were deleted; `grep -rn "SettingsDialog" src/ --include=*.cs --include=*.axaml` returns no matches, `CreateSettingsDialog()`/`OpenSettings()` no longer exist on [MainWindowViewModel.cs](src/Synapic.Main/ViewModels/MainWindowViewModel.cs), and the two doc comments that referenced the deleted types ([DedupSettingsPanel.axaml.cs](src/Synapic.Main/Views/Wizard/DedupSettingsPanel.axaml.cs), [UpscaleSettingsPanel.axaml.cs](src/Synapic.Main/Views/Wizard/UpscaleSettingsPanel.axaml.cs)) now point at the operation template.
- **Every parameter is inline in Region B.** [OperationLayout.axaml](src/Synapic.Main/Views/Operation/OperationLayout.axaml) gained the `vms:Step2EngineViewModel → steps:Step2Engine` template beside the existing dedup and upscale panels, so tagging's model list, device, tag fields, scoring and prompts render in the Parameters region — the region that previously fell through to the empty template.
- **The entry points went with the dialogs.** The shell's Settings command is now app-wide (`SettingsCommand` → the dashboard's Settings panel, ui-design §6.1); the per-step `OpenSettingsRequested` hooks and the two page ⚙ buttons were removed with the view models and views that carried them; user-visible copy that pointed at the dialog ("Change any of it with ⚙ Settings", "The settings of the operation itself live in the ⚙ dialog", the dedup/upscale rule-line hints) now names the Parameters region.
- **The pins moved to the inline reality** — see the mapping table below, including the reflection pin that fails if any of the three dialog types is re-introduced.

## Old fact → new fact mapping (D-06: no coverage removed)

| # | Old fact (file) | New fact (file) | What the new fact asserts |
|---|---|---|---|
| 1 | `Settings_dialog_shows_the_engine_view_over_the_wizards_own_view_model` (MainLayoutShellTests) | `Tagging_parameters_are_inline_in_the_parameters_region_not_a_window` (MainLayoutShellTests) | The three dialog types are absent from the built assembly; the engine form is a descendant of the `parametersRegion` Border, bound to `Wizard.Step2`, with the tag-field checkbox inside it, and its outermost window ancestor is the MainWindow |
| 2 | `Settings_opens_the_dialog_of_the_operation_on_screen` (WorkflowOrderTests) | `Settings_entry_point_opens_the_settings_panel_and_never_a_window` (WorkflowOrderTests) | One Settings entry is on screen and wired on the dashboard; invoking it from inside an operation returns to the dashboard with `Shell.Current` null and the `SettingsPanel` effectively visible — and no window is constructed |
| 3 | `Tagging_settings_page_is_a_summary_and_the_dialog_holds_the_form` (WorkflowOrderTests) | `Tagging_settings_page_summarizes_the_form_that_lives_in_the_parameters_region` (WorkflowOrderTests) | The 3 · Settings page carries no checkbox/text box and shows `TagFieldSummary`, while the same view model's form (with `TagCategoriesBox`) is in Region B of the same window |
| 4 | `Run_pages_report_the_settings_that_moved_to_the_dialog` (WorkflowOrderTests) | `Run_pages_report_the_settings_that_moved_to_the_parameters_region` (WorkflowOrderTests) | Name only — the assertions (tagging/dedup/upscale read-back lines) are unchanged |
| 5 | `Operation_settings_dialogs_have_no_overlapping_or_clipped_controls` (UiLayoutAuditTests) | `Inline_parameters_have_no_overlapping_or_clipped_controls` (UiLayoutAuditTests) | Theory × 4 sizes (1600×900 … 900×600): the tagging settings step, the dedup step and the upscale step each audit clean with their parameters region on screen — the three dialogs were audited at one authored size each, inline they survive all four |
| + | — (coverage gain) | `Every_route_renders_its_parameters_inline_in_the_parameters_region` (WorkflowOrderTests) | Each mode's own form (`Step2Engine` / `DedupSettingsPanel` / `UpscaleSettingsPanel`) is a descendant of the `parametersRegion` Border, bound to that mode's view model, with its distinctive controls present |
| 6 | `SettingsDialogView_NeverNullsTheDeviceSelection` (Step2EngineTests) | `EngineFormInTheOperationTemplate_NeverNullsTheDeviceSelection` (Step2EngineTests) | Deviation — the fact constructed a deleted type; it now drives the two real hosts (step 1's picker, then Region B's form, then back) and refreshes the device list at each stop |

## Verification

- `grep -rn "SettingsDialog" src/ --include=*.cs --include=*.axaml` → **no matches** (PASS)
- `grep -rn "OpenSettings" src/Synapic.Main/ViewModels/MainWindowViewModel.cs` → **no matches** (PASS)
- `ls src/Synapic.Main/Views/Settings` → directory removed (PASS)
- `dotnet build Synapic.Net.sln --nologo -v minimal` → **0 Warning(s) 0 Error(s)** (PASS)
- `dotnet test tests/Synapic.Main.Tests/... -c Release --filter "~MainLayoutShellTests|~WorkflowOrderTests|~UiLayoutAuditTests|~Step2EngineTests"` → **42 passed, 0 failed** (PASS)
- `dotnet test Synapic.Net.sln -c Release` → Shared **6/6**, Integration **5/5**, Main **444 passed / 2 failed / 446**; the two failures are pre-existing (see Issues) (PASS for this plan, phase gate not yet claimed)

## Decisions Made

- The Settings shortcut is app-wide and navigates to the dashboard Settings panel; per-operation parameters are inline, so no command needs to build a window.
- Both page-level ⚙ buttons went with the dialogs, together with the `OpenSettingsRequested` hooks on all three step view models — a dead entry point is a shipped defect, and the phase's criterion 4 is about the entry points as much as the classes.
- Tagging's Parameters region is the full engine form (not a trimmed variant): §4.4 lists model picker, device, tag fields, scoring and prompts as tagging's parameters, which is exactly what `Step2Engine` renders.

## Deviations from Plan

1. **[Rule 1 — bug] Two page ⚙ Settings buttons and the three per-step `OpenSettingsRequested` hooks were removed.** Found during: Task 1. The plan's file list stopped at `MainWindowViewModel`, but its verify step (`grep -rn "SettingsDialog" src/` → no matches) and D-02 ("dialogs retired, parameters inline") cannot be satisfied while `StepDedup.axaml`/`StepUpscale.axaml` still bind an `OpenSettingsCommand` whose only effect was to open the dialog just deleted. Files modified: `StepDedup.axaml`, `StepUpscale.axaml`, `Step2EngineViewModel.cs`, `StepDedupViewModel.cs`, `StepUpscaleViewModel.cs`. Verification: full build 0/0, filtered segment 42/42.
2. **[Rule 1 — bug] `tests/Synapic.Main.Tests/Step2EngineTests.cs` was not in the plan's file list but constructs `EngineSettingsDialog`**, so the solution could not compile without it. Rewritten to the new hosts with the same invariant (no null device selection). Verification: `Step2EngineTests` 15/15 in the filtered run.
3. **[Rule 3 — blocker] `MainLayoutShellTests` needed a usable source for the inline pin.** `NewShell()` has no datasource, so `GoToStep2Command` validated the route out and Region B stayed empty; the fact now builds a session with a temp-folder source (the same harness the other shell facts use).

**Total deviations:** 3 auto-fixed (2 bugs, 1 blocker). **Impact:** all inside the plan's intent; no assertion weakened, 5 facts replaced 1:1 and one gained.

## Issues Encountered

- **Two pre-existing failures in `ServerDetectionTests` (not this plan).** `Reported_device_matching_the_selection_is_shown_without_a_warning` (line 549) and `Cpu_fallback_under_a_cuda_selection_is_called_out` (line 566) fail because both set `session.Engine.Device = "cuda"` and let `Step2EngineViewModel`'s constructor run `TrySelectDevice("cuda")`, which falls back to CPU when `ComputeDeviceProbe.Detect()` finds no CUDA driver — this machine has no `nvidia-smi`/`nvcuda.dll`, so the selection silently becomes `"cpu"` and both device-mismatch expectations flip. **Evidence they are pre-existing:** a clean `git worktree` at HEAD (`d175a8b`), with none of this plan's changes, reproduces exactly the same 2 failures, and `ServerDetectionTests` alone reproduces them too (2 failed / 26 passed). The class already accepts an `availabilityProbe` for precisely this ("instead of depending on the GPU under the test runner") — these two facts just do not pass one. Left unfixed: outside this plan's scope (device detection is not the settings-dialog chrome), and the fix is a one-line probe injection per fact if the phase gate should read green. No other test regressed (444 passed vs the 442 baseline, Δ +4 new cases).

## Next Phase Readiness

- 03-02 can delete the sidebar and action bar without touching settings: their Settings buttons already point at `SettingsCommand`, and the sidebar's "3 · Settings…" entry is the last per-operation-sounding label left.
- Region B now renders one form per mode on its step; the step chain itself (which is what makes the region depend on `Wizard.CurrentStep`) is 03-02's to collapse.
- Criterion 4 is pinned by name and ready for 03-04's item-by-item sweep; the two pre-existing `ServerDetectionTests` failures need a decision before the final gate can be reported green.

---
*Phase: 03-collapse-navigation-and-retire-settings-dialogs*
*Completed: 2026-10-09*

## Self-Check: PASSED

- Key files exist on disk: SUMMARY (this file), `OperationLayout.axaml` with the `Step2EngineViewModel` template, `Views/Settings` absent.
- `git log --oneline --grep="03-01"` returns the plan's commits after the commit below.
- All task acceptance criteria re-run and PASS (greps, build, filtered tests).
- Plan-level verification re-run: build 0/0; full suite 444/446 with the 2 documented pre-existing failures.
