---
phase: 03-collapse-navigation-and-retire-settings-dialogs
plan: 02
subsystem: ui
tags: [avalonia, navigation, shell-current, chrome-deletion, pins, ui-design-10]

# Dependency graph
requires:
  - phase: 02-add-operation-template-and-dashboard
    provides: OperationLayout three regions (02-01), shared source panel + Region A (02-02), DashboardView + ShellViewModel.Current (02-03)
  - phase: 03-collapse-navigation-and-retire-settings-dialogs
    provides: settings dialogs retired and parameters inline in Region B (03-01); dedup Apply busy state (03-03)
provides:
  - One navigation model — ShellViewModel.Current (null = dashboard) — and no route strings, route booleans or step chain anywhere in src/
  - WizardViewModel replaced by OperationShellViewModel, which owns the six mode view models and hands each mode its Parameters/Run/Report content
  - MainLayout without the sidebar, the Back/Next/StartOver action bar or the step DataTemplates; the sidebar's styles removed from App.axaml
  - MainLayout.axaml.cs pushing the four template slots on Shell.Current (the element's own DataContext cannot reach the shell)
  - Every chrome-, route- and step-pinning fact rewritten to the dashboard/template pins (19 renamed, 3 gained, 1 removed with its host)
affects: [03-04 final gate, any future test that expects a numbered step, a sidebar entry or a route string]

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "Absence is the invariant: the chrome facts assert the deleted classes are not in the tree (navItem/navMode, ← Back / Next → / Start Over) rather than asserting the sidebar is hidden"
    - "Content is pushed, not bound: the four template slots are StyledProperties written by MainLayout.axaml.cs on Shell.Current change, because a binding on OperationLayout resolves against its own DataContext (the mode's step view model)"
    - "Gating moved from navigation to the action: every 'refused to leave step' fact became an IsRunEnabled + RunDisabledReason fact on the operation adapter (ui-design D6)"

key-files:
  created:
    - src/Synapic.Main/ViewModels/Operations/OperationShellViewModel.cs
    - src/Synapic.Main/Views/Operation/SourceLimitsPanel.axaml(.cs)
    - tests/Synapic.Main.Tests/OperationGatingTests.cs (renamed from WizardNavigationTests.cs)
    - tests/Synapic.Main.Tests/OperationLockWiringTests.cs (renamed from WizardProcessingLockWiringTests.cs)
  removed:
    - src/Synapic.Main/ViewModels/WizardViewModel.cs
    - src/Synapic.Main/Views/Operation/DataSourceStrip.axaml(.cs)
    - src/Synapic.Main/Views/Wizard/EngineModelPicker.axaml(.cs)
    - src/Synapic.Main/Views/Wizard/Step1Datasource.axaml(.cs)
    - src/Synapic.Main/Views/Wizard/Step2TagSettings.axaml(.cs)
  modified:
    - src/Synapic.Main/ViewModels/MainWindowViewModel.cs
    - src/Synapic.Main/Views/MainLayout.axaml
    - src/Synapic.Main/Views/MainLayout.axaml.cs
    - src/Synapic.Main/Views/Operation/OperationLayout.axaml(.cs)
    - src/Synapic.Main/Views/Dashboard/DashboardView.axaml
    - src/Synapic.Main/Views/Wizard/DatasourceSourcePanel.axaml
    - src/Synapic.Main/App.axaml
    - src/Synapic.Main/Services/HelpService.cs
    - tests/Synapic.Main.Tests/MainLayoutShellTests.cs
    - tests/Synapic.Main.Tests/WorkflowOrderTests.cs
    - tests/Synapic.Main.Tests/RouteSplitTests.cs
    - tests/Synapic.Main.Tests/UpscaleRouteTests.cs
    - tests/Synapic.Main.Tests/UiLayoutAuditTests.cs
    - tests/Synapic.Main.Tests/Step2EngineTests.cs
    - tests/Synapic.Main.Tests/MainWindowPopulationTests.cs
    - tests/Synapic.Main.Tests/StepDedupViewModelTests.cs
    - tests/Synapic.Main.Tests/HelpServiceTests.cs
    - tests/Synapic.Main.Tests/HelpScopeTests.cs
    - tests/Synapic.Main.Tests/ServerDetectionTests.cs

key-decisions:
  - "WizardViewModel was replaced rather than trimmed: the slimming left nothing that was not 'the mode's content', so the type became OperationShellViewModel (D-03) and every reference moved in one pass"
  - "Navigation derives instead of being stored: IsDashboardVisible/IsOperationVisible/OperationTitle/Breadcrumb are computed from Shell.Current, so a mode change is one write and every surface — chrome, title and help topic — follows"
  - "RouteTitle became OperationTitle plus a Breadcrumb (\"Dashboard / Tagging\"): the header states where you are, which is the job the sidebar's active entry used to do"
  - "MainLayout.axaml.cs writes the template's slots on Shell.Current changes rather than each region binding the shell: the element's DataContext is the mode's step view model, so a binding there is silently null"
  - "Region A's limits row (§8) was moved into its own SourceLimitsPanel inside the data-source region, so the template's Region A renders the source and its limits as one region"
  - "The sidebar's orphan styles (navItem/navItem.active/navMode/navGroup) were deleted from App.axaml: the phase deletes the chrome, and dead styling is drift evidence for D-01"
  - "docs/ui-design D6 gating is pinned on the run: entering a mode is free and the action carries the reason, replacing the three 'Next refuses to leave Datasource' facts"

patterns-established:
  - "A deleted navigation model is proven deleted at three levels: the view (no chrome in the tree), the view model (the route strings/booleans do not exist — the compiler enforces it) and the assembly (no dialog types)"
  - "A mode's content is a slot the shell fills from a key→content map (ParametersFor/RunFor/ReportFor), which is what makes 'one layout, three modes' checkable per mode in one loop"

requirements-completed: []

# Metrics
duration: 58min
completed: 2026-10-09
---

# Phase 3 Plan 02: Collapse navigation onto ShellViewModel.Current Summary

**The numbered sidebar, the Back/Next/StartOver bar and the step chain are gone from the source, `ShellViewModel.Current` is the only navigation state, and every chrome/route/step-pinning fact is migrated — build 0 warnings/0 errors, full suite 6 + 5 + 460 passed / 0 failed**

## Performance

- **Duration:** 58 min
- **Tasks:** 2
- **Files changed:** 46 changed (26 modified, 7 deleted, 7 added, 6 renamed)
- **Cases:** 451 → 460 in Synapic.Main.Tests (Δ +9: 19 facts rewritten in place, 3 gained, 1 removed with its host, and the 2 previously failing device facts now pass)

## Accomplishments

- **One navigation model.** [MainWindowViewModel.cs](src/Synapic.Main/ViewModels/MainWindowViewModel.cs) lost `Route`/`HomeRoute`/`RouteTitle`/`IsHomeVisible`/`IsWizardVisible`/`IsTaggingRoute`/`IsDedupRoute`/`IsUpscaleRoute` and the three `Enter*Route` calls: `Shell.Current` (null = dashboard) is the state, and `IsDashboardVisible`, `IsOperationVisible`, `OperationTitle`, `Breadcrumb`, `ContextHelpTopic` and `IsNavigationLocked` all derive from it. `grep -rn "HomeRoute\|IsDedupRoute\|IsUpscaleRoute\|EnterTaggingRoute" src/Synapic.Main/` → no matches.
- **The step chain is gone, not hidden.** `WizardViewModel` (including `CurrentStepIndex`, `NextCommand`, `GoToStep*Command`, the tab visibility flags, `ValidationError` and the wizard's half of the tag-field gate) is deleted; [OperationShellViewModel.cs](src/Synapic.Main/ViewModels/Operations/OperationShellViewModel.cs) hosts the six mode view models and exposes `Source`, `ParametersFor`/`RunFor`/`ReportFor`, `EnterMode`/`LeaveMode` and `NavigationLockChanged`. `grep -rn "CurrentStep\|GoToStep\|NextCommand\|StartOverCommand\|ShowTaggingTabs\|CanGoTo" src/Synapic.Main/` → no matches.
- **The chrome is deleted.** [MainLayout.axaml](src/Synapic.Main/Views/MainLayout.axaml) is header (title · breadcrumb · source profile · ← Dashboard · ⚙ Settings) → content (DashboardView **or** OperationLayout) → server footer → diagnostics drawer; the sidebar column, the action bar row and the step DataTemplates are gone, together with `DataSourceStrip`, `EngineModelPicker`, `Step1Datasource` and `Step2TagSettings`.
- **The template really swaps content per mode.** [OperationLayout.axaml](src/Synapic.Main/Views/Operation/OperationLayout.axaml) takes `SharedSource`/`Parameters`/`Run`/`Report` as StyledProperties and [MainLayout.axaml.cs](src/Synapic.Main/Views/MainLayout.axaml.cs) writes them from `Shell.Current`, so a mode switch re-renders the regions instead of leaving the previous mode's form on screen.
- **Every pin migrated** — see the mapping table below; the criterion-7 fact (server status + Help reachable from the dashboard and all three modes) is new.

## Old fact → new fact mapping (D-06: no coverage removed)

### Deleted navigation (routes, steps, chrome)

| # | Old fact (file) | New fact (file) | What the new fact asserts |
|---|---|---|---|
| 1 | `App_opens_on_the_dashboard_with_no_route_selected` (RouteSplitTests) | `App_opens_on_the_dashboard_with_nothing_open` (RouteSplitTests) | `IsDashboardVisible`, not `IsOperationVisible`, `Breadcrumb == "Dashboard"`, empty `OperationTitle`, `Shell.Current == null` |
| 2 | `Tagging_route_starts_the_wizard_at_source_and_model` (RouteSplitTests) | `Tagging_route_opens_the_template_on_the_tagging_mode` (RouteSplitTests) | Entering shows the template with the adapter on `Shell.Current`, the breadcrumb `Dashboard / Tagging`, and `ParametersFor`/`RunFor`/`ReportFor("tag")` handing out the tagging content |
| 3 | `Dedup_route_offers_only_Datasource_and_Deduplication` (RouteSplitTests) | `Dedup_route_opens_the_template_on_the_dedup_mode` (RouteSplitTests) | Dedup's Parameters **and** Run slots are the dedup view model, `ReportFor("dedup")` is null, and no tagging content is reachable |
| 4 | `Dedup_route_Next_skips_the_wizard_and_lands_on_Dedup_with_the_source` (RouteSplitTests) | `Dedup_mode_reads_the_shared_source_with_no_step_in_between` (RouteSplitTests) | The mode reads the shared source on open (folder carried over, `IsLocal`), and with a real folder the run is enabled with no reason |
| 5 | `Dedup_route_refuses_to_leave_Datasource_without_a_source` (RouteSplitTests) | `Dedup_run_is_gated_without_a_source_and_says_why` (RouteSplitTests) | Entering without a source is allowed; `DedupOperation.IsRunEnabled` is false and `RunDisabledReason` names the folder (D6) |
| 6 | `Dedup_route_selects_the_catalog_source_on_the_dedup_step` (RouteSplitTests) | `Dedup_mode_reads_the_catalog_source_from_the_shared_source` (RouteSplitTests) | The catalog source reaches dedup through the shared instance (`IsDaminion`, not `IsLocal`) |
| 7 | `Dashboard_panels_and_route_visibility_are_bound_in_the_real_window` (RouteSplitTests) | `Dashboard_panels_and_mode_visibility_are_bound_in_the_real_window` (RouteSplitTests) | The three cards are wired and visible; exactly one of DashboardView/OperationLayout is visible per state; each mode sets `layout.Parameters`/`layout.Run` from its own view model and lights the header entry |
| 8 | `Home_returns_to_the_dashboard_and_another_route_opens_at_the_source` (RouteSplitTests) | `Home_returns_to_the_dashboard_and_another_mode_opens_on_the_shared_source` (RouteSplitTests) | Home clears `Shell.Current` and the breadcrumb; another mode opens on the same shared source |
| 9 | `Upscale_route_opens_on_Datasource_with_the_tagging_and_dedup_tabs_gated` (UpscaleRouteTests) | `Upscale_route_opens_the_template_on_the_upscale_mode` (UpscaleRouteTests) | The upscale view model is both content regions, `ReportFor("upscale")` is null, tagging/dedup content unreachable |
| 10 | `Upscale_route_Next_skips_the_wizard_and_lands_on_the_upscale_step` (UpscaleRouteTests) | `Upscale_mode_reads_the_shared_source_and_is_ready_to_run` (UpscaleRouteTests) | `SourceReady` + `SourceSummary` from the shared source on open; the run is enabled with no reason |
| 11 | `Upscale_route_refuses_to_leave_Datasource_without_a_source` (UpscaleRouteTests) | `Upscale_run_is_gated_without_a_source_and_says_why` (UpscaleRouteTests) | Entering is free, `SourceReady` false, the run disabled with a reason (D6) |
| 12 | `Third_card_and_upscaling_tab_are_bound_in_the_real_window` (UpscaleRouteTests) | `Third_card_opens_the_upscale_mode_and_the_template_wires_its_regions` (UpscaleRouteTests) | The third card opens the mode; the same OperationLayout instance swaps to dedup's content — no second template, no tagging form |
| 13 | `Home_returns_to_the_chooser_and_the_upscale_route_can_be_reentered` (UpscaleRouteTests) | `Home_returns_to_the_dashboard_and_the_upscale_mode_reopens_with_its_state` (UpscaleRouteTests) | Home clears `Shell.Current`; re-entering keeps the mode's own settings (`SelectedWorkflow`) and the source |
| 14 | `Sidebar_reads_as_the_numbered_setup_then_the_steps_of_the_chosen_operation` (WorkflowOrderTests) | `Dashboard_reads_as_settings_then_the_three_operations_with_no_sidebar` (WorkflowOrderTests) | The four dashboard panels by name; the three route cards; **no** `navItem`/`navMode` entry and no Back/Next/StartOver label in the tree on the dashboard or inside any mode |
| 15 | `Operation_entry_returns_to_the_dashboard_from_any_step` (WorkflowOrderTests) | `Dashboard_entry_returns_from_every_mode_and_keeps_the_modes_state` (WorkflowOrderTests) | The header entry is visible and enabled in all three modes, clears `Shell.Current`, and a parameter set inside a mode survives the round trip |
| 16 | `Sidebar_enters_a_mode_and_marks_the_entry_that_is_on_screen` (MainLayoutShellTests) | `Dashboard_panel_enters_a_mode_and_the_breadcrumb_marks_it` (MainLayoutShellTests) | The dashboard panel enters the mode and the breadcrumb (`Dashboard / Tagging`) is the "where am I" marker the active sidebar entry used to be; Home clears it |
| 17 | `Shell_has_a_header_a_sidebar_an_action_bar_a_footer_and_the_log` (MainLayoutShellTests) | `Shell_has_a_header_a_footer_and_the_log_and_no_sidebar_or_action_bar` (MainLayoutShellTests) | Header + source profile + 4 panels; one Settings entry (the header shortcut); the deleted classes and labels are absent; one binding per route command; the log drawer toggles; the run bar's primary action carries the accent |
| 18 | `OnStep2_NextAndStart_DisabledUntilAtLeastOneTagField` / `OnStep1_Next_IsNotBlockedByTagFields` (WizardNavigationTests) | `Tagging_run_stays_disabled_until_at_least_one_tag_field_is_on` / `Opening_the_tagging_mode_is_never_blocked_by_the_tag_fields` (OperationGatingTests) | The run's `StartCommand` is what the tag fields gate; the mode opens regardless (D6) |
| 19 | `Step3_IsRunning_refreshes_wizard_navigation` (WizardProcessingLockWiringTests) | `A_running_batch_locks_navigation_and_unlocks_when_it_stops` (OperationLockWiringTests) | A run's `IsRunning` flip raises `NavigationLockChanged` and the shell re-notifies `IsNavigationLocked` both ways |
| 20 | `F1_opens_the_topic_for_the_step_on_screen` (HelpServiceTests) | `F1_opens_the_topic_for_the_mode_on_screen` (HelpServiceTests) | The dashboard falls back to the help home; each mode resolves its own topic and opens it; Home goes back to the home topic |

### Deleted views and hosts

| # | Old fact (file) | New fact (file) | What the new fact asserts |
|---|---|---|---|
| 21 | `Source_and_model_step_renders_the_model_picker_over_the_engine_view_model` (WorkflowOrderTests) | `Tagging_model_picker_lives_in_the_parameters_region_not_the_dashboard` (WorkflowOrderTests) | The Settings panel has no model list; the model list in Tag's Parameters region is bound to `TagParameters.LocalModels` |
| 22 | `Tagging_settings_page_summarizes_the_form_that_lives_in_the_parameters_region` (WorkflowOrderTests) | `The_engine_form_has_one_editable_home_in_the_parameters_region` (WorkflowOrderTests) | The engine form is editable in Region B and the run region carries no second copy (no checkboxes) |
| 23 | `No_step_renders_a_view_model_type_name_as_text` (WorkflowOrderTests) | `No_mode_renders_a_view_model_type_name_as_text` (WorkflowOrderTests) | Walking the three modes, no `Synapic.*` text is rendered (the unmatched-DataTemplate trap) |
| 24 | `Wizard_navigates_and_all_step_views_populate` (MainWindowPopulationTests) | `Every_operation_view_populates_itself_from_the_host_content` (MainWindowPopulationTests) | Each mode view populates with the host's view model, and the region lookups hand out those same instances |
| 25 | `Tagging_steps_have_no_overlapping_or_clipped_controls` (UiLayoutAuditTests) | `Tagging_mode_has_no_overlapping_or_clipped_controls` (UiLayoutAuditTests) | The whole tagging mode audits clean at 4 sizes |
| 26 | `Dedup_and_upscale_steps_have_no_overlapping_or_clipped_controls` (UiLayoutAuditTests) | `Dedup_and_upscale_modes_have_no_overlapping_or_clipped_controls` (UiLayoutAuditTests) | Same, per mode, 4 sizes |
| 27 | `Operation_routes_audit_clean_with_the_three_regions_in_order` (UiLayoutAuditTests) | `Operation_modes_audit_clean_with_the_three_regions_in_order` (UiLayoutAuditTests) | Three regions in geometry order + clean audit, 3 modes × 4 sizes |
| 28 | `Inline_parameters_have_no_overlapping_or_clipped_controls` (UiLayoutAuditTests) | same name (UiLayoutAuditTests) | Body only: no step to walk to (every case audits the mode as it opens) |
| 29 | `EngineFormInTheOperationTemplate_NeverNullsTheDeviceSelection` / `LeavingTheStep_WithTheViewLive_NeverNullsTheDeviceSelection` (Step2EngineTests) | `EngineFormInTheParametersRegion_NeverNullsTheDeviceSelection` / `LeavingTheMode_WithTheViewLive_NeverNullsTheDeviceSelection` (Step2EngineTests) | The form in Region B never nulls the device selection, on refresh and across a mode round trip |
| 30 | `DedupView_WiresConfirmHook_AndStep1Scope` (StepDedupViewModelTests) | same name (StepDedupViewModelTests) | Body only: the host (`OperationShellViewModel`) hands the shared source in for the Daminion scope summary |
| 31 | `Settings_panel_is_the_five_section_app_wide_settings_view` (SettingsViewTests) | same name (SettingsViewTests) | Body only: `IsDashboardVisible`, and no source form **or** engine form on the panel |
| 32 | `F1_opens_the_scope_of_the_focused_control_instead_of_the_fallback_topic` (HelpScopeTests) | same name (HelpScopeTests) | Body only: the focus scope wins over the dashboard's fallback, which is now the help home (`index.html`) |
| — | (removed with its host) `Tagging_settings_page_...` sidebar "3 · Settings…" page | — | The page was the numbered sidebar's settings step; its entry point and its host (`Step2TagSettings`) are deleted, and its invariant (form not summarised away) is carried by fact 22 |

### Gained

| # | New fact (file) | What it asserts |
|---|---|---|
| +1 | `Server_status_and_help_stay_reachable_on_the_dashboard_and_every_mode` (MainLayoutShellTests) | Criterion 7: the server status and the Help entry are on screen and working on the dashboard and in all three modes |
| +2 | `The_engine_form_has_one_editable_home_in_the_parameters_region` (WorkflowOrderTests) | Criterion 4's complement: Region B edits it, Region C does not duplicate it |
| +3 | `Dashboard_reads_as_settings_then_the_three_operations_with_no_sidebar` (WorkflowOrderTests) | The dashboard read plus the absence of every deleted chrome element, inside each mode too |

## Verification

- `grep -rn "navItem\|navMode\|routeCard\|HomeRoute\|IsDedupRoute\|IsUpscaleRoute\|IsTaggingRoute\|IsHomeVisible\|IsWizardVisible\|EnterTaggingRoute\|EnterDedupRoute\|EnterUpscaleRoute" src/Synapic.Main/Views/MainLayout.axaml src/Synapic.Main/ViewModels/` → **no matches** (PASS)
- `grep -rn "CurrentStep\|GoToStep\|NextCommand\|StartOverCommand\|ShowTaggingTabs\|CanGoTo" src/Synapic.Main/ --include=*.cs --include=*.axaml` → **no matches** (PASS)
- `dotnet build Synapic.Net.sln -v q` → **0 Warning(s) 0 Error(s)** (PASS)
- `dotnet test tests/Synapic.Main.Tests/Synapic.Main.Tests.csproj -c Release --filter "~MainLayoutShellTests|~WorkflowOrderTests|~RouteSplitTests"` → **passed, 0 failed** (PASS)
- `dotnet test Synapic.Net.sln -c Release` → Shared **6/6**, Integration **5/5**, Main **460/460** — **0 failures** (PASS)
- Layout audit: `UiLayoutAuditTests` + `Inline_parameters_...` green at 900×600, 1024×700, 1280×800, 1600×900 for the dashboard and all three modes (PASS)

## Decisions Made

- **`WizardViewModel` → `OperationShellViewModel` by replacement, not trimming.** Nothing left in the wizard was navigation-free except "the mode's content" and the cross-cutting wiring the step view models depended on it for (the tag-field re-check on the run's gate, the run lock, the commit-to-store on leaving the mode), so the type became the host and the six view models moved with it.
- **Navigation is derived, never stored.** `Breadcrumb` replaced `RouteTitle` because the header has to say *where you are* once the sidebar's active entry is gone; keeping the old name would have hidden that the chrome's job moved rather than vanished.
- **The template's slots are pushed from code-behind.** A binding written on `OperationLayout` resolves against the element's own `DataContext` (the mode's step view model), so the shell has to hand the content in — and it re-hands it on every `Shell.Current` change, which is what makes a mode switch re-render instead of leaving the old form.
- **The gate lives on the action, not on navigation (D6).** The three "Next refuses to leave Datasource" facts became `IsRunEnabled`/`RunDisabledReason` facts; entering a mode is free and the blocked thing says why.
- **Orphan sidebar styles deleted from `App.axaml`.** `Button.navItem`, `Button.navItem.active`, `Button.navMode` and `TextBlock.navGroup` have no binders left; deleting the chrome but keeping its styling is the drift D-01 exists to catch.

## Deviations from Plan

1. **[Rule 3 — blocker] The plan's two-file test list was actually twelve files.** `files_modified` named `MainLayoutShellTests`, `WorkflowOrderTests` and `RouteSplitTests`, but `WizardViewModel`'s deletion breaks the compile of every test that names the step chain: `RouteSplitTests`, `UpscaleRouteTests`, `UiLayoutAuditTests`, `Step2EngineTests`, `MainWindowPopulationTests`, `StepDedupViewModelTests`, `HelpServiceTests`, `HelpScopeTests` and two wizard-named files all had to be migrated for the solution to build. Files modified: as listed in `key-files`. Verification: full build 0/0; full suite 460/460.
2. **[Rule 2 — missing critical] `HelpScopeTests` was not in the plan's list but pinned the deleted fallback topic.** Its fact asserted the unscoped F1 fallback was `step1-datasource.html`; under the collapse the dashboard's topic is `HelpTopics.Home`, so the fact's expectation (not its invariant — the focus scope still has to win) was updated. Verification: `HelpScopeTests` green in the full run.
3. **[Rule 2 — missing critical] The criterion-7 fact the plan requires was added, not merely kept.** No existing fact asserted server status + help on every mode, so `Server_status_and_help_stay_reachable_on_the_dashboard_and_every_mode` was written (server status visible and the Help entry opening a topic, on the dashboard and in each mode).
4. **[Rule 1 — bug, pre-existing] The two `ServerDetectionTests` device facts were fixed, which is what let the phase gate read green.** 03-01-SUMMARY recorded them as pre-existing machine-dependent failures and left the fix to whoever needed a green gate. Reproduced at HEAD (`9c16104`) in a scratch worktree: both fail there with none of this plan's changes. Cause: the fixture set `session.Engine.Device = "cuda"` *before* constructing the shell, and `Step2EngineViewModel`'s availability rule normalizes a device this machine cannot offer back to the CPU, so both device-mismatch expectations inverted on a CUDA-less machine. Fix: state the device **after** the shell is built (the subject is `ApplyServerDevice`'s comparison, not the availability rule), with a comment saying why the order matters. Verification: those two facts pass on this machine for the first time; no other fact changed behaviour.
5. **[Rule 3 — blocker] `HelpTopics.ForStepIndex`'s doc comment referenced the deleted `WizardViewModel`.** Comment updated to describe the legacy step-index naming the compiled help still carries; the mapping itself is deliberately unchanged (the topic files are named for the old steps, and the adapters read the entry their mode maps to). Recorded as a follow-up rather than renamed in this plan.

**Total deviations:** 5 auto-fixed (1 bug, 2 blockers, 2 missing-critical). **Impact:** all inside the plan's intent; no assertion weakened, no fact deleted without its host.

## Issues Encountered

- **The settings view files were uncommitted prerequisites.** `src/Synapic.Main/ViewModels/SettingsViewModel.cs`, `src/Synapic.Main/Views/Dashboard/SettingsPanel.axaml(.cs)`, the `ConfigService`/`SynapicLog` changes and `tests/Synapic.Main.Tests/SettingsViewTests.cs` were untracked (03-01's commit predates them) while `MainWindowViewModel` now depends on them, so this commit carries them. They are 03-01's feature by content; nothing about them changed here except the panel's `IsDashboardVisible`/engine-form assertions in the migrated fact.
- **Two `OperationShellViewModel` facts were asserted against behaviour the ctor resets.** The first draft of `Opening_the_tagging_mode_is_never_blocked_by_the_tag_fields` cleared the tag fields on the *session* before construction, which the engine form then re-hydrates from its own defaults; the fact now clears them through the view model (what the user does), which is also what makes the run's gate observable.
- **`Server_status_and_help_...` needed a weaker uniqueness claim than expected.** Two elements carry `StatusText` in the tree, so the fact requires that the status is visible somewhere rather than that it occurs once — noted in the fact so a future reader does not "tighten" it back into a flake.

## Next Phase Readiness

- **03-04's final gate can now sweep criteria 1–9 item by item with a green suite** (460/460, build 0/0), including criterion 2 (no sidebar/route booleans — the compiler plus the greps prove it) and criterion 7 (pinned by name).
- **Criterion 9's layout audit** walks the dashboard and all three modes at four sizes; the wide two-column form of the dashboard remains the one carried deviation from Phase 2.
- **The help corpus still numbers the old steps** (`setup-guide.html`, `step*-*.html`) while the shell's topics are mode-named: a wording sweep, not a functional gap, and the natural first task of the docs pass.
- **`ShellViewModel.Open`/`Home`/`Operations` now have their caller** (the route commands), so the Phase 2 note about them being unrendered navigation surface is closed.

---
*Phase: 03-collapse-navigation-and-retire-settings-dialogs*
*Completed: 2026-10-09*

## Self-Check: PASSED

- Key files exist on disk: this SUMMARY, `OperationShellViewModel.cs`, `SourceLimitsPanel.axaml`, `OperationGatingTests.cs`, `OperationLockWiringTests.cs`; `WizardViewModel.cs` and the five deleted views are absent.
- All task acceptance criteria re-run and PASS (greps, build, filtered tests).
- Plan-level verification re-run: build 0/0; full suite 6 + 5 + 460 passed / 0 failed; audit green at four sizes.
- Mapping table accounts for all 32 replaced/retired facts, the 3 gained and the 1 removed with its host.
