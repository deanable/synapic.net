---
phase: 02-add-operation-template-and-dashboard
plan: 04
subsystem: tests
tags: [avalonia, headless-tests, layout-audit, pins, ui-design-10]

# Dependency graph
requires:
  - phase: 02-add-operation-template-and-dashboard
    provides: OperationLayout regions (02-01), shared DataSourceStrip (02-02), DashboardView + Shell.Current (02-03)
provides:
  - The three pinned navigation files rewritten to ui-design §10 criteria 1–3
  - Layout audit extended to the three operation routes (all four sizes, region order asserted) and the dashboard at the narrow sizes
  - D-05 coverage: RunStateBar bound to a RunStateViewModel and asserted
affects: [02-05 phase gate sweep, 03 rewriting the pins that still describe the sidebar/dialogs]

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "Dashboard panels are found by the shared dashboardPanel class and their x:Names; the region markers dataSourceRegion/parametersRegion/outputRegion are found by class"
    - "Region order is asserted two ways: document order in the functional tests, geometry (Y then X) in the audit facts"

key-files:
  modified:
    - tests/Synapic.Main.Tests/RouteSplitTests.cs
    - tests/Synapic.Main.Tests/MainLayoutShellTests.cs
    - tests/Synapic.Main.Tests/WorkflowOrderTests.cs
    - tests/Synapic.Main.Tests/UiLayoutAuditTests.cs

key-decisions:
  - "Renames rather than silent rewrites: every fact that moved to the dashboard is named for what it now pins, and the mapping table below accounts for each old name"
  - "The old gating fact is replaced by a D6 fact that asserts both halves of the new rule — panels always enabled, run disabled with a reason — so no coverage was dropped, only re-pointed"
  - "The dashboard's narrow form is pinned as Settings above a single row of the three operation panels (its actual geometry), not as a one-column stack the view does not render"

patterns-established:
  - "New-fact naming: '<surface>_<what is asserted>' so a failing test names the criterion it protects"

requirements-completed: []

# Metrics
duration: 35min
completed: 2026-10-09
---

# Phase 2 Plan 04: Rewrite the navigation pins + extend the layout audit Summary

**The three pinned navigation files now pin the dashboard, the three-region order on all three modes and the one shared source; the audit walks the three operation routes at all four sizes and the dashboard at the narrow ones — 441 passed / 0 failed, coverage up from 431**

## Performance

- **Duration:** 35 min
- **Tasks:** 3
- **Test files modified:** 4
- **Cases:** 431 → 441 in Synapic.Main.Tests (0 failures, previously 1 known-invalidated pin)

## Accomplishments

- [RouteSplitTests.cs](tests/Synapic.Main.Tests/RouteSplitTests.cs) pins ui-design §10 criterion 1 (cold start on the dashboard with exactly four panels, named Settings/Tag/Dedup/Upscale, each opening its operation), D-03 (`Shell.Current` null on the dashboard, the matching adapter once open, cleared by Home) and criterion 3 (the new `Region_A_strip_shows_the_same_source_and_count_on_every_route`: the same `Step1` instance, the same source label and the same record count on tag, dedup and upscale, with dedup and upscale reading that source).
- [WorkflowOrderTests.cs](tests/Synapic.Main.Tests/WorkflowOrderTests.cs) adds `All_three_modes_render_the_same_three_regions_in_order` (criterion 2: one each of `dataSourceRegion`/`parametersRegion`/`outputRegion`, asserted in document order on all three routes, with Region A being the shared instance) and `RunStateBar_renders_the_run_state_it_binds` (D-05: progress value, progress text, ETA, current file, a run-log line and the step's own Start command all resolve).
- [UiLayoutAuditTests.cs](tests/Synapic.Main.Tests/UiLayoutAuditTests.cs) adds `Operation_routes_audit_clean_with_the_three_regions_in_order` (4 sizes × 3 routes: region order by geometry plus the existing clip/overlap audit) and `Dashboard_panels_read_in_order_and_audit_clean` (1024×700 and 900×600: Settings above a single row of Tag/Dedup/Upscale, left to right, audit clean).
- [MainLayoutShellTests.cs](tests/Synapic.Main.Tests/MainLayoutShellTests.cs) keeps all three of its sidebar/dialog facts and gains the four-panel assertion inside the shell fact (criterion 1 seen from the shell), with the start-screen wording corrected to the dashboard.

## Old fact → new fact mapping (D-06: no coverage removed)

| File | Old fact | Where it went |
|------|----------|----------------|
| RouteSplitTests | `App_opens_on_the_chooser_with_no_route_selected` | Renamed `App_opens_on_the_dashboard_with_no_route_selected` (same route/visibility assertions **+** `Shell.Current` null); the "exactly four panels" half is the new `Dashboard_shows_exactly_four_panels_named_Settings_Tag_Dedup_Upscale` |
| RouteSplitTests | `Tagging_route_starts_the_wizard_at_source_and_model` | Same name, **+** `Shell.Current` is the tagging adapter |
| RouteSplitTests | `Dedup_route_offers_only_Datasource_and_Deduplication` | Same name, **+** `Shell.Current` is the dedup adapter |
| RouteSplitTests | `Dedup_route_Next_skips_the_wizard_and_lands_on_Dedup_with_the_source` | Unchanged (now proves the shared source carries over without a prefill) |
| RouteSplitTests | `Dedup_route_refuses_to_leave_Datasource_without_a_source` | Unchanged |
| RouteSplitTests | `Dedup_route_selects_the_catalog_source_on_the_dedup_step` | Unchanged; the "strip shows the same source on both routes" half the plan asked for is the new `Region_A_strip_shows_the_same_source_and_count_on_every_route` |
| RouteSplitTests | `Start_screen_cards_and_route_visibility_are_bound_in_the_real_window` | Renamed `Dashboard_panels_and_route_visibility_are_bound_in_the_real_window` (same assertions against the dashboard panels) |
| RouteSplitTests | `Start_screen_gates_the_workflow_cards_on_a_usable_source` | Replaced by `Dashboard_entry_is_free_and_only_the_run_is_gated`: the disabled-card assertion is superseded by ui-design D6, and is re-pointed at the two facts that replace it — panels always enabled, run disabled **with its reason** until a source exists; the strip-light and folder-line assertions are preserved verbatim |
| RouteSplitTests | `Start_screen_lays_the_source_panel_out_above_the_workflow_cards` | Renamed `Dashboard_lays_the_source_panel_out_above_the_operation_panels` (same measure/arrange assertions) |
| RouteSplitTests | `Daminion_connect_form_lives_on_the_start_screen_only` | Renamed `Daminion_connect_form_lives_on_the_dashboard_Settings_panel_only` **+** asserts the connect button sits inside `SettingsPanel` |
| RouteSplitTests | `Home_returns_to_the_chooser_and_the_route_resumes_where_it_was` | Renamed `Home_returns_to_the_dashboard_and_the_route_resumes_where_it_was` **+** `Shell.Current` null on Home and the adapter after re-entry |
| MainLayoutShellTests | `Shell_has_a_header_a_sidebar_an_action_bar_a_footer_and_the_log` | Same name, **+** the four dashboard panels by name (criterion 1); wording updated |
| MainLayoutShellTests | `Sidebar_enters_a_mode_and_marks_the_entry_that_is_on_screen` | Unchanged (valid until Phase 3) |
| MainLayoutShellTests | `Settings_dialog_shows_the_engine_view_over_the_wizards_own_view_model` | Unchanged (valid until Phase 3) |
| WorkflowOrderTests | all six existing facts | Unchanged; two new facts added beside them |
| UiLayoutAuditTests | `Home_screen_*`, `Tagging_steps_*`, `Dedup_and_upscale_steps_*`, `Operation_settings_dialogs_*` | All kept and passing; `Home_screen_*` now audits the dashboard (home route) and says so |

**No fact was deleted.** Attribute/case counts: RouteSplitTests 11 → 13, MainLayoutShellTests 3 → 3, WorkflowOrderTests 6 → 8, UiLayoutAuditTests 4 theories + 1 fact → 6 theories + 1 fact (13 → 19 audit cases).

## Verification

| Check | Result |
|-------|--------|
| Task 1 gate: `--filter "FullyQualifiedName~RouteSplitTests\|~MainLayoutShellTests"` | exit 0 — **16 passed / 0 failed** |
| Task 2 gate: `--filter "FullyQualifiedName~regions"` | exit 0 — **5 passed / 0 failed** (the three-region fact + the four audited sizes of the route audit) |
| Task 3 gate: `--filter "FullyQualifiedName~UiLayoutAuditTests"` | exit 0 — **19 passed / 0 failed** (was 13 cases) |
| `dotnet build Synapic.Net.sln -c Release --no-incremental` | exit 0 — 0 Warning(s), 0 Error(s) |
| `dotnet test Synapic.Net.sln -c Release --no-build` | exit 0 — Shared **6/6**, Integration **5/5**, Main **441 passed / 0 failed / 0 skipped** (*no* known failure remains) |
| Coverage non-decreasing | attribute counts per file listed above; every removed assertion has a mapped replacement in the table |

## Decisions Made

- **The D6 replacement asserts both halves.** Replacing "cards disabled until a source exists" with "panels enabled" alone would have dropped the coverage that says *what happens without a source*; the new fact asserts the panels are enabled **and** that the run is disabled with `RunDisabledReason == "Choose a folder to scan"`, then that a usable folder flips the run on while the strip's light goes green — strictly more than the old pin checked.
- **Two ways to assert region order.** Functional tests use document order (what §10 criterion 2 literally asks for); the audit facts use geometry, because a document-order check cannot catch a region that is laid out in the wrong place.
- **The dashboard's narrow form is pinned as it is.** The view stacks nothing: the Settings panel is full width and the three operation panels share one row. The fact asserts that geometry (Settings above; Tag, Dedup, Upscale left to right, on one row) rather than claiming a 2×1/1×4 form the view does not render; the wide 2×2 with a real breakpoint stays a known deviation (see 02-01/02-03 summaries).

## Deviations from Plan

1. **The wide-vs-stacked coverage the plan asks for is split across two facts** rather than one fact with a "stacked" concept: the operation template's stacked form is asserted by `Operation_routes_audit_clean_with_the_three_regions_in_order` (region order top-to-bottom at all four sizes), and the dashboard's by `Dashboard_panels_read_in_order_and_audit_clean`. The audit helper has no "stacked" concept to reuse, so the geometry assertions are explicit.
2. **MainLayoutShellTests gained an assertion rather than a rewritten fact** — its three facts remain valid until Phase 3 (the plan says not to delete them), so the file's change is the criterion-1 panel assertion plus wording.

## Issues Encountered

- The xUnit analyzer rejected `Assert.Single(collection.Where(predicate))` (xUnit2031, an error because the repo treats warnings as errors) in the six places the new facts filtered for a region marker; rewritten to the `Assert.Single(collection, predicate)` overload it asks for. No assertion was weakened — the same single-item guarantee is asserted.

## Next Phase Readiness

- Every fact ui-design §10 criteria 1, 2, 3 and 5 needs now exists by name; 02-05's gate sweep can quote them item-by-item, and criterion 4 (no settings dialog) plus 6–8 remain Phase 3's.
- The sidebar/action-bar/dialog facts that still pass are the ones Phase 3 must map when it deletes that chrome — they are unchanged here precisely so that sweep starts from a known list.

---
*Phase: 02-add-operation-template-and-dashboard*
*Completed: 2026-10-09*

## Self-Check: PASSED
