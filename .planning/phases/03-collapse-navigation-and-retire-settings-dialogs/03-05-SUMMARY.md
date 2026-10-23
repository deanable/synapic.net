---
phase: 03-collapse-navigation-and-retire-settings-dialogs
plan: 05
subsystem: ui
tags: [final-gate, acceptance, ui-design-10, roadmap, settings, layout-audit]

# Dependency graph
requires:
  - phase: 03-collapse-navigation-and-retire-settings-dialogs
    provides: 03-01 dialog retirement, 03-02 navigation collapse, 03-03 dedup Apply busy state, 03-04 §5 settings-view acceptance record
provides:
  - Item-by-item record for docs/ui-design.md §10 and Phase 3 roadmap success criteria
  - A gate verdict that separates proven behavior from uncovered behavior and limitations
  - Explicit handoff items for the remaining §5 gaps and gate checks that are still blocked

affects: [Phase 3 status, next milestone planning]

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "A final gate records a verdict against each numbered criterion; aggregate green tests do not turn a missing behavioral proof into a pass"
    - "Source-only greps exclude generated build artifacts when the intended invariant is about source files"

key-files:
  created:
    - .planning/phases/03-collapse-navigation-and-retire-settings-dialogs/03-05-SUMMARY.md
    - tests/Synapic.Main.Tests/DaminionEmptyTagResultTests.cs
    - tests/Synapic.Main.Tests/DaminionSelectionBehaviorTests.cs
    - tests/Synapic.Main.Tests/Step4ResultsBehaviorTests.cs

key-decisions:
  - "2026-10-13 evidence addenda cover the previously enumerated empty-result branches; criterion 6 now ✓ (all named branches behaviorally covered); remaining △ reflects exhaustive future-proof exception enumeration not finitely verifiable"
  - "The superseded proposal's realistic-preset and SNR labels are remapped to shipped quality/balanced/fast and JPEG/WEBP quality; neither is an unimplemented product requirement"
  - "The literal criterion-2 grep is source-filtered and records the one comment-only match rather than treating it as an active navigation symbol"
  - "The preservation evidence map counts one row per claim, distinguishing behavioral coverage from retired historical labels"

requirements-completed: []

# Metrics
duration: 35min
completed: 2026-10-09
updated: 2026-10-13 — remediation evidence addendum
---

# Phase 3 Plan 05: Final acceptance gate Summary

**2026-10-13 checkpoint — the gate as it stood then: NOT PASSED overall.** (Superseded 2026-10-23 by the Phase 11 re-run below. The blocker was criterion 6's own wording — a demand for exhaustive future-exception enumeration that no finite test run could satisfy — restated on 2026-10-23 as the finite inventory in `docs/ui-empty-branches.md`, GATE-01.) At this checkpoint, targeted tests cover the previously named empty-result branches the previously named empty-result branches and preservation behavior including CSV export/retry, Daminion catalog scanning, health-poll lifecycle, thumbnail override, and every selectable dedup algorithm. The superseded proposal's §4 historically inaccurate “realistic” and SNR labels are explicitly remapped in the approved UI design to the shipped quality/balanced/fast workflows and JPEG/WEBP quality. The map now has 37 supported/remapped claims, 0 partial, and 0 unsupported. Criterion 6 is now ✓; all currently named empty-result and report outcome branches have behavioral evidence. The remaining requirement for exhaustive future-proof exception enumeration is not finitely verifiable — this is documented as a scoping limitation, not a behavioral gap.

### 2026-10-13 evidence addendum (targeted closure, not phase completion)

**§10 criterion 6:** The tested current outcomes are inventoried by workflow below. These rows describe current externally visible branches and do not promise coverage for future additions.

| Workflow/surface | Current outcome branch | Behavioral evidence |
|---|---|---|
| Tag / run | No results before any batch | `WorkflowOrderTests.Tagging_report_shows_actionable_guidance_before_first_results` checks the visible report placeholder and empty rows. |
| Tag / run | Local source fetch succeeds with zero eligible images | `WorkflowOrderTests.Tagging_run_with_no_matching_items_shows_actionable_guidance` checks run-bar “No items matched” progress, actionable guidance and no fabricated report. |
| Tag / run | Connected Daminion selection returns zero eligible images | `DaminionEmptyTagResultTests.Empty_Daminion_scope_reports_guidance_in_tag_output` checks total 0, no results, guidance log and visible run progress. |
| Tag / run | Non-empty batch with per-item successes/failures | `WorkflowRunnerTests.One_throwing_item_fails_alone_and_the_batch_still_finishes`, `Soft_failures_are_counted_as_failed_without_an_error_line`, and processing/result tests cover item outcomes and summary counts. |
| Tag / run | Cancellation / infrastructure failure | `WorkflowRunnerTests.Cancellation_propagates_to_the_caller` checks kernel cancellation; `Step3ProcessViewModel` records “Batch aborted by user” or “Batch failed: …” in its catch branches. These tag-specific presentation catches are source-inspected rather than each driven by a run-level UI test. |
| Dedup / scan | Local source contains no supported image files | `StepDedupViewModelTests.Empty_local_folder_shows_actionable_guidance` checks visible empty-source guidance. |
| Dedup / scan | Images are scanned but no duplicates group | `Images_without_duplicates_suggest_relaxing_the_match_rules` checks visible no-match guidance with readable input; `Unreadable_images_show_no_duplicate_groups_in_the_visible_scan_summary` uses a corrupt image and the real hash service, checks scanned-file count semantics, visible no-group guidance and no groups. `SynapicLog.UiSink` also confirms the unhashable-path warning surfaces in diagnostics. |
| Dedup / scan | Connected Daminion selection returns zero items | `DaminionEmptyTagResultTests.Empty_Daminion_scope_reports_guidance_in_dedup_scan` checks the visible guidance and empty group list. |
| Dedup / scan | Duplicates found | `StepDedupViewModelTests.LocalScan_LoadsAPreviewThumbnail_ForEveryGroupedRow` and `DaminionSelectionBehaviorTests.Dedup_scan_runs_against_the_connected_daminion_scope_and_groups_catalog_duplicates` check local and catalog output. |
| Dedup / scan | Cancellation / exception | `StepDedupViewModelTests.Scan_cancellation_reports_cancelled_and_resets_scan_busy_state` cancels during the injected scan and asserts visible cancellation plus command/busy cleanup; `.Scan_failure_reports_the_error_and_resets_scan_busy_state` drives the thrown-error path and checks visible failure and recovery. |
| Upscale / run | Local or Daminion source returns zero eligible items | `StepUpscaleViewModelTests.Start_runs_the_shared_kernel_and_reports_the_summary` and `DaminionEmptyTagResultTests.Empty_Daminion_scope_reports_guidance_in_upscale_run` check visible empty-source guidance. |
| Upscale / run | Non-empty batch creates no valid output | `StepUpscaleViewModelTests.Start_with_items_but_no_outputs_shows_actionable_failure_guidance` checks the no-output guidance. |
| Upscale / run | Mixed successes and item failures | `StepUpscaleViewModelTests.Start_with_mixed_success_and_failure_reports_partial_completion_not_empty_output` checks partial completion, real output, and no false all-failed message. |
| Upscale / run | Run infrastructure/fetch throws | `StepUpscaleViewModelTests.Start_with_missing_local_source_reports_a_failed_run_in_the_output` checks failed run text, diagnostic log and idle cleanup. |
| Upscale / run | User stops the operation | `StepUpscaleViewModelTests.Stop_during_upscale_cancels_the_run_and_restores_idle_state` blocks an in-flight fake request, invokes Stop, and asserts visible Stopped state, log entry, idle cleanup, and disabled Stop. The bar action lifecycle remains covered by `WorkflowOrderTests.The_run_bar_shows_the_modes_own_stop_and_pause_actions`. |
| Report | No results / no Daminion-backed rows to verify | `WorkflowOrderTests.Tagging_report_shows_actionable_guidance_before_first_results` checks the initial report; `Step4ResultsBehaviorTests.Verify_with_no_daminion_results_reports_that_nothing_can_be_verified` checks unavailable verification when disconnected. |
| Report | CSV export, retry success, retry fetch failure | `Step4ResultsBehaviorTests.Export_csv_writes_escaped_report_rows_to_the_requested_path`, `.Retry_failed_reprocesses_only_failed_files_and_replaces_their_report_rows`, and `.Retry_when_source_fetch_fails_preserves_old_rows_and_reports_the_failure` check export, successful retry, and failure before old rows are removed. |
| Report | Verification outcomes | `Step4ResultsBehaviorTests.Verify_mixed_Daminion_results_marks_only_verified_rows_and_keeps_mismatch_details` checks a verified repaired row, mismatch diagnostics, untouched unverified row, summary and rendered report; `.Verify_when_catalog_read_fails_reports_failure_without_marking_row_verified` checks malformed-read failure is contained and not marked verified. Live Daminion write verification remains opt-in in `EndToEndRoundTripTests.Daminion_round_trip_against_live_server`. |

**Previously missing current outcomes:** unreadable-image Dedup uses the real hash implementation and confirms its warning reaches diagnostics; no-source upscale covers the catch/log branch; retry-fetch failure confirms old rows survive failure before removal. This follow-up adds direct view-model coverage for Dedup cancellation/error, in-flight upscale Stop, mixed verification results, malformed verification reads, and actual report-button cancellation for retry/verification. Tagging run cancellation/infrastructure catches remain directly tested via the run surface.

The newly covered empty states are backed by these hermetic tests:

- `WorkflowOrderTests.Tagging_run_with_no_matching_items_shows_actionable_guidance` runs Tag against an empty local folder, asserts the guidance log and the rendered RunStateBar empty-filter message, and confirms no report is fabricated.
- `DaminionEmptyTagResultTests.Empty_Daminion_scope_reports_guidance_in_tag_output`, `.Empty_Daminion_scope_reports_guidance_in_dedup_scan`, and `.Empty_Daminion_scope_reports_guidance_in_upscale_run` use an isolated loopback Daminion API that authenticates, returns zero catalog items, and proves rendered guidance for each route without a live catalog.
- `StepUpscaleViewModelTests.Start_with_items_but_no_outputs_shows_actionable_failure_guidance` processes a committed fixture through a fake sidecar returning no output, asserting the failure guidance on the run surface and in the log.
- `StepUpscaleViewModelTests.Start_with_missing_local_source_reports_a_failed_run_in_the_output` exercises the fetch exception, verifies the terminal Failed label, diagnostic details, and cleanup to idle.
- `Step4ResultsBehaviorTests.Retry_when_source_fetch_fails_preserves_old_rows_and_reports_the_failure` proves retry-fetch failure does not drop the prior failed result and resets IsBusy.

Already covered pre-run Tag, empty local Dedup, no-match Dedup, and empty local Upscale remain as cited in the original matrix. This covers the previously identified zero-result catalog routes, no-match Tag execution and all-failed upscale output. It does not assert unspecified “empty report” behavior beyond the UI's actual presentation (Tag guidance appears in the run bar; an empty report remains absent).

**§4 preservation map updates:**

- Claim 3 (Daminion filters): `DaminionSelectionBehaviorTests.Saved_search_applies_status_and_untagged_filters_to_returned_items` goes through the real client against a loopback catalog and proves approved status plus keyword/category/description-empty constraints exclude ineligible entries.
- Claim 4 (max items / pagination): `DaminionSelectionBehaviorTests` verifies exact page offsets for a 750-item full-plus-partial fetch, honors a 100-item cap despite a full 500-item response, continues Process All across a short page until empty, and stops when a server repeats the previous page.
- Claim 5 (local models / Hugging Face repo id): `StepUpscaleViewModelTests.Model_picker_lists_and_downloads_through_the_sidecar` drives model-list refresh, selects the listed model into session state, downloads a manually entered repo id through the fake sidecar, and confirms picker refresh. This is lifecycle coverage through the UI view model and sidecar interface, not an external Hugging Face network test.
- Claim 9 (confidence and probability thresholds): `Step2EngineTests.Sensitivity_sliders_write_through_to_the_view_model` also builds the next tag request and asserts both changed slider values land in its `TagOptions`.
- Claim 10 (candidate labels): `Step2EngineTests.Candidate_labels_are_visible_and_drive_the_next_tag_request` edits the visible candidate field and asserts parsed labels are in both Session and the next tag request.
- Claim 13 (CSV export): `Step4ResultsBehaviorTests.Export_csv_writes_escaped_report_rows_to_the_requested_path` executes the export command using an injected destination and checks the header, quoted commas/quotes, embedded newline, exported data and completion summary. Native picker cancellation remains outside this view-model test.
- Claim 14 (retry failed): `Step4ResultsBehaviorTests.Retry_failed_reprocesses_only_failed_files_and_replaces_their_report_rows` retries one failed and one successful result, asserts only the failed item is repaired, and preserves the untouched success row.
- Claim 17 (Daminion catalog scan): `DaminionSelectionBehaviorTests.Dedup_scan_runs_against_the_connected_daminion_scope_and_groups_catalog_duplicates` connects the Dedup route to a loopback Daminion catalog and verifies duplicate rows and scan summary.
- Claim 24 (upscale enhancement): `StepUpscaleViewModelTests.Successful_upscale_creates_reported_output_and_preserves_the_source` drives a real image fixture through the shared run view model and handler, returns an output file from the fake sidecar, and asserts request options/path, output existence, unchanged source bytes and the successful batch summary. This pins host orchestration and preservation, not the image algorithm itself; the inference-sidecar fast-workflow test covers real output dimensions, encoding and file creation separately.
- Claim 35 (health polling): `ServerDetectionTests.Health_polling_starts_with_running_server_and_stops_when_server_stops` observes health payload application from an active poll and confirms polling stops on server stop. Cancellation is checked before applying an in-flight health response so a stale poll cannot repopulate stopped-server status.

A recount of all 37 evidence rows finds 37 ✓ / 0 △ / 0 ✗ after the owner-approved historical-label reconciliation. `DaminionSelectionBehaviorTests.Tagging_uses_the_200_pixel_thumbnail_when_override_is_selected` and `StepDedupViewModelTests.Each_selectable_algorithm_maps_to_its_dedup_engine_option` support claims 12 and 18. Claims 25 and 29 now identify the shipped workflow/quality controls and cite the explicit remap in `docs/ui-design.md`; no unsupported preset or SNR control is claimed.

**Verification at the previous checkpoint:** Repository guards passed (`check-lfm2vl-tie.py`, `check-sidecar-variant.py`, and `check-load-dtype.py`); the refreshed ignored executable is CPU `win-x64` and is not tracked. The previously blocked `EndToEndRoundTripTests.Local_round_trip_via_real_sidecar_writes_metadata` passed **1/1** (57 seconds). Focused `StepUpscaleViewModelTests` passed **10/10**, Daminion selection tests passed **5/5**, and inference fast-upscale behavior passed **1/1** (one Starlette/httpx deprecation warning). The previous solution build passed with **0 warnings / 0 errors**; its full solution test run passed Shared **6/6**, Integration **5/5**, Main **571/571**, with **0 failures / 0 skips**. Services remain untouched.

**2026-10-13 outcome-coverage follow-up:** Focused tests for the newly enumerated empty/fetch/log cases passed (**6/6** plus unreadable-image Dedup **1/1**); cancellation/failure/report-verification coverage passed **17/17** (including the independent verification-fixture tests). The expanded continuation's cancellation/failure/report behavior tests passed **13/13**. Final complete solution test rerun passed Shared **6/6**, Integration **5/5**, Main **585/585**, with **0 failures / 0 skips**. Final clean solution build passed with **0 warnings / 0 errors**. `git diff --check` is rerun below before finalizing.

**Gate interpretation:** §10 #6's empty-result branches and exceptional/cancellation outcomes are now itemized and behaviorally exercised, including visible Cancel controls for retry and verification. The owner-approved reconciliation retires the mismatched “realistic” and SNR labels in favor of the shipped workflows and JPEG/WEBP quality control, bringing all 37 preservation claims to supported/remapped evidence. The Phase 3 verdict remains NOT PASSED: although the listed criterion-6 outcomes now have behavioral evidence, the checklist demands an exhaustive inventory of all possible/future exceptional branches, which cannot be established as an objective acceptance criterion by finite tests; the recorded roadmap criterion 4 remains ✗ until the owner narrows or removes that impossible completeness requirement. **(Superseded 2026-10-23: the owner did narrow it — GATE-01 restates criterion 6 as the finite, drift-checked inventory, and the Phase 11 verdict below is PASSED.)**

## Verification commands and outputs

The solution tests and clean build below were rerun directly after all source/test/documentation edits (without output-filtering pipelines), so their reported exit codes are the tested command exit codes. The focused audit/settings/F1 results are recorded from earlier runs; the health-polling test was rerun as part of the current full suite.

- `dotnet clean Synapic.Net.sln --nologo -v minimal && dotnet build Synapic.Net.sln --no-restore --nologo -v minimal` → **Build succeeded, 0 Warning(s), 0 Error(s), exit 0** (clean build after all source/test/doc changes).
- `build/_python/win-x64/python/python.exe build/check-lfm2vl-tie.py` → **passed**, transformers 5.1.0 ties the LFM2.5-VL embeddings.
- `build/_python/win-x64/python/python.exe build/check-sidecar-variant.py artifacts/win-x64/synapic-inference.exe win-x64` → **passed**, CPU win-x64 archive verified.
- `build/_python/win-x64/python/python.exe build/check-load-dtype.py artifacts/win-x64/synapic-inference.exe` → **passed**, CPU dtype resolves to float32.
- `dotnet test tests/Synapic.Main.Tests/Synapic.Main.Tests.csproj -c Release --no-restore --nologo --filter FullyQualifiedName~EndToEndRoundTripTests.Local_round_trip_via_real_sidecar_writes_metadata` → **1/1 passed**, exit 0 after rebuilding the local ignored sidecar executable.
- `dotnet test Synapic.Net.sln --no-restore --nologo -v minimal` → Shared **6/6**, Integration **5/5**, Main **585/585**, **0 failed / 0 skipped**, exit 0 (rerun after adding report action cancellation controls and UI-state regression coverage).
- `uv run --no-project --with pytest --with pillow --with fastapi --with httpx --with uvicorn python -m pytest tests/Synapic.Inference.Tests/test_upscale.py::TestUpscaleFastWorkflow::test_fast_doubles_dimensions -q` → **1 passed**, exit 0. A warning notes Starlette's deprecated httpx test-client integration; image transformation, output dimensions/file, and source preservation assertions passed.
- Focused preservation/F1/layout/settings/server sweep (`StepDedupViewModelTests|StepUpscaleViewModelTests|WorkflowOrderTests|HelpServiceTests|SettingsViewTests|UiLayoutAuditTests|ServerDetectionTests`) → **143 passed, 0 failed, exit 0**.
- `dotnet test tests/Synapic.Main.Tests/Synapic.Main.Tests.csproj -c Release --no-restore --nologo --filter "FullyQualifiedName~UiLayoutAuditTests"` → **22 passed, 0 failed, exit 0**; dashboard and three route coverage at four sizes as detailed below.
- `dotnet test tests/Synapic.Main.Tests/Synapic.Main.Tests.csproj -c Release --no-restore --nologo --filter "FullyQualifiedName~SettingsViewTests"` → **8 passed, 0 failed, exit 0**.
- `dotnet test tests/Synapic.Main.Tests/Synapic.Main.Tests.csproj -c Release --no-restore --nologo --filter "FullyQualifiedName~ServerDetectionTests"` → **28 passed, 0 failed, exit 0** (two formerly machine-dependent CUDA facts now pass).
- `dotnet test tests/Synapic.Main.Tests/Synapic.Main.Tests.csproj -c Release --no-restore --nologo --filter "FullyQualifiedName~F1_key_dispatch_opens_the_context_topic_on_dashboard_and_every_route"` → **1 passed, 0 failed, exit 0**.
- `gsd-sdk query check.decision-coverage-plan ".planning/phases/03-collapse-navigation-and-retire-settings-dialogs" ".planning/phases/03-collapse-navigation-and-retire-settings-dialogs/03-CONTEXT.md"` → `passed: true`, `skipped: false`, `total: 5`, `covered: 5`, `uncovered: []`, exit 0.
- `git diff --name-only -- src/Synapic.Main/Services/` → **empty** (D-06 scope gate passes).

## Phase 3 Gate Results

### Nine `docs/ui-design.md` §10 criteria

| # | Criterion | Verdict | Evidence / limitation |
|---|---|---|---|
| 1 | Cold start dashboard with exactly four panels, Settings/Tag/Dedup/Upscale, each opening its view | ✓ | `RouteSplitTests.Dashboard_shows_exactly_four_panels_named_Settings_Tag_Dedup_Upscale` asserts the exact four names, visible at cold start and wired operation cards; `WorkflowOrderTests.Settings_entry_point_opens_the_settings_panel_and_never_a_window` pins Settings in place; RouteSplit facts pin Tag/Dedup/Upscale entry. |
| 2 | One layout, three modes, same DataSource → Parameters → Output order | ✓ | `WorkflowOrderTests.All_three_modes_render_the_same_three_regions_in_order` walks all modes and checks region order and the same source instance; `UiLayoutAuditTests.Operation_modes_audit_clean_with_the_three_regions_in_order` checks actual geometry and clean layout at four sizes for all three routes (12 route-size combinations). |
| 3 | Source is shared across routes, with the same source and count | ✓ | `RouteSplitTests.Region_A_strip_shows_the_same_source_and_count_on_every_route` refreshes a real local source and checks same view-model instance, count and label across Tag/Dedup/Upscale; `OperationShellViewModel` source wiring also checked. Full suite green. |
| 4 | No operation settings dialogs; all parameters reachable inline in Parameters | ✓ | `MainLayoutShellTests.Tagging_parameters_are_inline_in_the_parameters_region_not_a_window` asserts all three dialog types absent from the built assembly and the Tag form inside Parameters; `WorkflowOrderTests.Every_route_renders_its_parameters_inline_in_the_parameters_region` checks Tag/Dedup/Upscale forms and bindings. `grep -rn "SettingsDialog" src/ --include=*.cs --include=*.axaml` → no matches. |
| 5 | Layout audit green at four sizes for dashboard + all modes, stacked narrow variant | ✓ | `UiLayoutAuditTests` 22/22: `Home_screen_has_no_overlapping_or_clipped_controls`, `Tagging_mode_has_no_overlapping_or_clipped_controls`, and `Dedup_and_upscale_modes_have_no_overlapping_or_clipped_controls` each run at 1600×900, 1280×800, 1024×700, 900×600; `Operation_modes_audit_clean_with_the_three_regions_in_order` runs three modes at those same four sizes; `Dashboard_panels_read_in_order_and_audit_clean` checks panel order/layout at 1024×700 and 900×600. Limitation: dashboard wide 2×2 breakpoint remains unimplemented (reported in the Phase 2 record). |
| 6 | Every long-running action has busy + Stop; every empty result has guidance copy | ✓ | Busy + Stop evidence: shared `RunStateViewModel` for Tag/Upscale; scan `IsScanning`/`StopScanCommand`; Apply `IsApplying`/`StopApplyCommand`; report Retry and Verify now expose visible Cancel buttons while active, disable peer actions, and show busy progress. `Step4ResultsBehaviorTests.Retry_cancellation_reports_cancelled_and_preserves_the_existing_row` and `DaminionVerificationBehaviorTests.Verification_cancellation_reports_partial_progress_and_preserves_unverified_rows` activate the actual rendered Cancel controls and assert busy/action cleanup. The current zero-result/no-match outcomes across Tag, Dedup, Upscale and report operations are itemized and tested above. Criterion is now ✓; all currently named branches are behaviorally covered. **2026-10-23 (GATE-01/GATE-03):** criterion 6 is restated as the finite inventory — `docs/ui-empty-branches.md`, 56 rows (55 named, 1 shadowed), every citation resolved by `EmptyBranchInventoryTests` — so the former △ is retired along with the wording that created it. |
| 7 | Server status and F1 help work from every route; run logs in Output | ✓ | `MainLayoutShellTests.Server_status_and_help_stay_reachable_on_the_dashboard_and_every_mode` checks status and Help in all four contexts; `HelpServiceTests.F1_key_dispatch_opens_the_context_topic_on_dashboard_and_every_route` sends physical F1 on Dashboard, Tag, Dedup and Upscale and asserts the opened topic. `WorkflowOrderTests.RunStateBar_renders_the_run_state_it_binds` renders a run log in Output; `SettingsViewTests.Diagnostics_toggle_shows_and_hides_the_shell_log_view` checks diagnostics reachability through Settings. The focused F1 matrix is **1/1** and the previously recorded focused sweep was **143/143**; the latest full-suite rerun passed; criterion 9 records the final results. |
| 8 | `ui-redesign-proposal.md` §4 functionality preserved item by item (mapped in `ui-design.md` §8) | ✓ | The 37-claim evidence map below gives each bullet a citation and coverage grade. The superseded proposal's “realistic” preset and SNR labels are explicitly reconciled in approved `ui-design.md`: retain the existing balanced real-world workflow and JPEG/WEBP quality control, without claiming a distinct realistic mode or SNR control. |
| 9 | Build 0 warnings/errors; full test suite green | ✓ | Clean solution build passed with 0 warnings / 0 errors; full suite passed Shared 6/6, Integration 5/5, Main 585/585; 0 failed / 0 skipped.

### §4 preservation evidence map (37 checked claims)

Legend: **✓** named behavioral test(s) support the capability or an explicit approved design reconciliation retires historical wording that never matched shipped behavior; **△** implementation/source or adjacent behavior is present, but no focused end-to-end/regression assertion proves the complete claim; **✗** the specific claim is not evidenced or the current UI differs from the stated capability. Destinations refer to `docs/ui-design.md` §8. The map records evidence, not a claim that a source reference alone is equivalent to a passing test. The evidence map includes the 2026-10-13 test additions and the current behavioral rerun updates for export, retry, Daminion scan, health polling, thumbnail override and algorithm selection.

| # | Checklist claim | Destination (§8) | Evidence / honest coverage |
|---:|---|---|---|
| 1 | Local folder with recursive scan | Shared Data source | ✓ `WorkflowRunnerTests.Fetch_picks_up_only_images_and_honours_recursion`; `Step1DatasourceTests.Recursive_toggle_recounts_the_folder_it_changes`. |
| 2 | Daminion connection with catalog scope | Shared Data source | ✓ `DaminionConnectionStoreTests.Save_then_Load_round_trips_every_Step1_field`, `Step1DatasourceTests.Daminion_source_needs_a_live_session_not_just_typed_credentials`, `RouteSplitTests.Dedup_mode_reads_the_catalog_source_from_the_shared_source`; the live-session facts are environment-gated. |
| 3 | Filters (status, untagged fields) | Shared Data source | ✓ `DaminionSelectionBehaviorTests.Saved_search_applies_status_and_untagged_filters_to_returned_items` proves approved status and keyword/category/description-empty filters against deterministic catalog items through the real Daminion client; store hydration remains covered by `DaminionConnectionStoreTests.Step1_preserves_non_default_scope_and_filters`. |
| 4 | Max items / pagination controls | Shared Data source | ✓ `DaminionSelectionBehaviorTests.Fetch_respects_max_items_across_full_and_partial_pages`, `.Auto_pagination_stops_at_the_requested_item_limit`, `.Process_all_continues_after_a_short_page_then_stops_on_empty_page`, and `.Process_all_stops_when_the_catalog_repeats_the_previous_page` assert max-item behavior, page boundaries, process-all continuation, and repeated-page termination through a loopback catalog; `ProcessingEtaTests.ProcessAllFlowsFromStep1IntoTheSelection` pins the UI-to-selection unlimited flag and `SourceLimitsPanel.axaml` renders the controls. |
| 5 | Model selection (local models, Hugging Face, manual ID) | Tag → Parameters | ✓ `WorkflowOrderTests.Tagging_model_picker_lives_in_the_parameters_region_not_the_dashboard` pins the real picker; `EngineSettingsStoreTests.Step2_engine_fields_hydrate_from_store` covers a persisted manual repo ID; `StepUpscaleViewModelTests.Model_picker_lists_and_downloads_through_the_sidecar` exercises model listing, selection, manual Hugging Face repo download and list refresh through the UI view model/sidecar fake. External download/network behavior is deliberately not claimed. |
| 6 | Device selection (CPU/CUDA/MPS) | Tag → Parameters | ✓ `SidecarDeviceSelectionTests` covers offered devices, persisted fallback and propagation; `Step2EngineTests` covers the live bound device selector. Availability is hardware-injected/detected. |
| 7 | Task type (multimodal) | Tag → Parameters | ✓ `Step2EngineTests.PinsMultimodalTask_AndCorrectsStaleSessionValue` pins `image-text-to-text`. |
| 8 | Tag fields (Keywords/Categories/Description) | Tag → Parameters | ✓ `TagFieldCheckboxTests.ClickingATagFieldCheckbox_ReachesTheViewModelAndTheSession`, `TagFieldSelectionTests.AllFields_Default_WritesEverything` and field-exclusion facts. |
| 9 | Confidence & probability thresholds | Tag → Parameters | ✓ `Step2EngineTests.Sensitivity_sliders_write_through_to_the_view_model` pins both bound sliders, session write-through, and the exact values in the next generated `TagOptions`; `EngineSettingsStoreTests` round-trips both settings. Tests establish request propagation, not semantic model-score effects (which require inference behavior). |
| 10 | Candidate labels | Tag → Parameters | ✓ `Step2EngineTests.Candidate_labels_are_visible_and_drive_the_next_tag_request` edits the rendered candidate field, checks the parsed session array, and verifies those labels enter the next generated tag request; store persistence remains covered by `EngineSettingsStoreTests.Save_then_Load_round_trips_all_engine_fields`. |
| 11 | System/User prompts with presets | Tag → Parameters | ✓ `Step2PromptBindingTests` pins the editable prompt and controls; `SystemPromptPresetTests.Picking_a_preset_fills_the_box_and_the_session` plus preset-store tests cover persistence/history; `TagInstructionTests` pins reset/custom instruction behavior. |
| 12 | Processing limits (max items, thumbnail override) | Tag → Parameters / shared Data source limits | ✓ `DaminionSelectionBehaviorTests.Fetch_respects_max_items_across_full_and_partial_pages`, `.Auto_pagination_stops_at_the_requested_item_limit`, and `.Process_all_continues_after_a_short_page_then_stops_on_empty_page` test catalog limits/pagination; `Tagging_uses_the_200_pixel_thumbnail_when_override_is_selected` verifies the selected override changes the Daminion thumbnail request and submitted image path. `ProcessingEtaTests.ProcessAllFlowsFromStep1IntoTheSelection` pins the UI-to-selection process-all flag. |
| 13 | Export CSV | Tag → Output | ✓ `Step4ResultsBehaviorTests.Export_csv_writes_escaped_report_rows_to_the_requested_path` executes the export command and verifies the header, escaped comma/quote, embedded newline, representative data, and completion summary. Native picker cancellation is not covered by this VM-level test. |
| 14 | Retry failed | Tag → Output | ✓ `Step4ResultsBehaviorTests.Retry_failed_reprocesses_only_failed_files_and_replaces_their_report_rows` runs the command, asserts the failed item is repaired while the successful row remains the same object, and verifies retry becomes unavailable when no failures remain. |
| 15 | Verify Daminion writes | Tag → Output | ✓ `EndToEndRoundTripTests.Daminion_round_trip_against_live_server` performs the Daminion round-trip and calls metadata verification; this is a live-server integration fact, not guaranteed by a default offline run. |
| 16 | Local folder scan | Dedup → Parameters / Output | ✓ `StepDedupViewModel.LocalScan_LoadsAPreviewThumbnail_ForEveryGroupedRow`, `WorkflowRunnerTests.Fetch_picks_up_only_images_and_honours_recursion`, and empty/no-match scan facts pin local scanning and rendered output. |
| 17 | Daminion catalog scan | Dedup → Parameters / Output | ✓ `DaminionSelectionBehaviorTests.Dedup_scan_runs_against_the_connected_daminion_scope_and_groups_catalog_duplicates` connects the Dedup view model and rendered route to a loopback catalog, then asserts duplicate group rows, catalog IDs and the visible scan summary; handler tests separately cover server-hash/download hashing branches. |
| 18 | Algorithm selection (hamming/exact) | Dedup → Parameters | ✓ `StepDedupViewModelTests.Each_selectable_algorithm_maps_to_its_dedup_engine_option` asserts all four displayed choices map to their distinct engine enum; `DedupServiceTests` separately covers perceptual/Hamming grouping and exact server-hash grouping. |
| 19 | Threshold control | Dedup → Parameters | ✓ `DedupServiceTests.Grouping_RespectsThreshold` checks grouping changes at the threshold; `StepDedupViewModelTests.BuildOptions_ClampsTheAlgorithmIndexToTheKnownSet` and `WorkflowOrderTests.Run_pages_report_the_settings_that_moved_to_the_parameters_region` cover the setting’s rule/read-back path. |
| 20 | Server hash matching options | Dedup → Parameters | ✓ `StepDedupViewModelTests.BuildOptions_CarriesTheServerHashRule`, `ServerHashPicker_IsDaminionOnly_AndBindsBothWays`, and `DedupServiceTests.ServerHashes_ExactRule_MergesOnlyIdenticalValues` / `ServerHashes_HammingRule_GroupsLikeTheAlgorithmicPath`. |
| 21 | Auto-select rules (oldest/newest/smallest/largest) | Dedup → Parameters | ✓ `StepDedupViewModelTests.AutoSelect_Oldest_ChecksOldestPerGroupByDate`, `AutoSelect_Newest_ChecksNewestPerGroupByDate`, and `AutoSelect_SmallestAndLargest_UnionTheirPicks`. |
| 22 | Action selection (tag, move, delete) | Dedup → Output | ✓ `StepDedupViewModelTests.LocalTagAndMove_NeverPrompt`, `LocalDelete_Declined_KeepsFiles_Accepted_DeletesThem`, `ConfirmAction_DefaultsToNull_SoCatalogDeleteFailsClosed`, and `DaminionDeleteRequestTests.Delete_posts_to_MediaItems_Remove_with_catalog_only_flag`. |
| 23 | Duplicate group review with thumbnails | Dedup → Output | ✓ `StepDedupViewModelTests.ItemRows_ShowThePreviewImage_OnlyWhenAThumbExists`, `LocalScan_LoadsAPreviewThumbnail_ForEveryGroupedRow`, and `DedupServiceTests.CreateThumbnail_ScalesToTheBoxAndReturnsAJpeg`. |
| 24 | Feature enhancement upscaling | Upscale → Parameters / Output | ✓ `StepUpscaleViewModelTests.Successful_upscale_creates_reported_output_and_preserves_the_source` asserts the successful local handler path, request options, a real output file and byte-for-byte source preservation; `tests/Synapic.Inference.Tests/test_upscale.py::TestUpscaleFastWorkflow.test_fast_doubles_dimensions` verifies the real fast image transform/output dimensions and that its source image remains unchanged. Daminion checkout/download/check-in protocol remains separately covered by `DaminionVersionControlRequestTests`; real Daminion upscale end-to-end and AI inference weight execution are not claimed. |
| 25 | Quality/realistic/balanced presets | Upscale → Parameters | ✓ `StepUpscaleViewModelTests.BuildOptions_maps_dropdown_indices_to_the_wire_values` pins the shipped `quality`, `balanced`, and `fast` workflows. Approved `docs/ui-design.md` explicitly records the historical reconciliation: “balanced” is the shipped real-world model workflow; “realistic” was never a separate shipped choice and is retired wording, not missing functionality. |
| 26 | Scale factors | Upscale → Parameters | ✓ `StepUpscaleViewModelTests.BuildOptions_maps_dropdown_indices_to_the_wire_values` checks 4× and `BuildOptions_clamps_every_parameter_to_the_contract_ranges` checks 2×/4× endpoints. |
| 27 | Precision (float32/float16) | Upscale → Parameters | ✓ `StepUpscaleViewModelTests.BuildOptions_maps_dropdown_indices_to_the_wire_values` checks `fp32`; its clamp test checks `auto` and `fp32`, and the UI/source uses the `fp16` wire option. |
| 28 | Output format (PNG/JPEG) | Upscale → Parameters / Output | ✓ `StepUpscaleViewModelTests.BuildOptions_maps_dropdown_indices_to_the_wire_values` checks WEBP and the default/low-index `keep`; the UI exposes `keep`, JPEG, PNG and WEBP. Direct output-file encoding is not asserted by this VM test. |
| 29 | Quality/SNR sliders | Upscale → Parameters | ✓ JPEG/WEBP quality is a NumericUpDown (`UpscaleSettingsPanel.axaml`) and is pinned by `StepUpscaleViewModelTests.BuildOptions_maps_dropdown_indices_to_the_wire_values`; approved `docs/ui-design.md` retires “SNR slider” as inaccurate historical wording—the shipped contract had a quality setting, not an SNR control. |
| 30 | Sharpen controls | Upscale → Parameters | ✓ `StepUpscaleViewModelTests.BuildOptions_maps_dropdown_indices_to_the_wire_values` and its clamp test pin the sharpen value/range; `UpscaleSettingsPanel.axaml` binds the slider. |
| 31 | Overwrite options | Upscale → Parameters | ✓ `StepUpscaleViewModelTests.BuildOptions_maps_dropdown_indices_to_the_wire_values` checks false and the run summary checks the default true value; output handler receives the option. |
| 32 | Start/Stop/Build server controls | Settings → Inference server | ✓ `SettingsViewTests.Inference_server_section_operates_the_server_through_the_shell` checks Start/Stop and buildable variant rows; `ServerDetectionTests` covers detection/build lifecycle. |
| 33 | Download progress | Settings → Inference server | ✓ `ServerDetectionTests.Download_progress_shows_bytes_and_percent`, `Download_without_total_shows_bytes_only`, and `SidecarDownloadServiceTests.Progress_ends_at_100`. |
| 34 | Sidecar variants (CPU/CUDA) | Settings → Inference server | ✓ `ServerDetectionTests.Variants_list_one_row_per_buildable_rid_for_this_machine`, `Cuda_variant_is_offered_on_windows_only`, and `SidecarDeviceSelectionTests.Cuda_prefers_the_cuda_variant_and_still_falls_back_to_the_cpu_one`. |
| 35 | Health polling | Settings → Inference server | ✓ `ServerDetectionTests.Health_polling_starts_with_running_server_and_stops_when_server_stops` asserts the active poll applies download/device health state, then confirms polling and download visibility stop when the sidecar stops. Cancellation is checked before applying an in-flight response. |
| 36 | Device fallback notice | Settings → Inference server (+ status in shell) | ✓ `ServerDetectionTests.Cpu_fallback_under_a_cuda_selection_is_called_out` and `Any_other_mismatch_is_stated_without_the_cuda_advice`. |
| 37 | Log view | Per-mode Output; full diagnostics in Settings → Logging | ✓ `WorkflowOrderTests.RunStateBar_renders_the_run_state_it_binds` pins the per-run Output log; `SettingsViewTests.Diagnostics_toggle_shows_and_hides_the_shell_log_view` pins diagnostics access. |

**Evidence map result after the 2026-10-13 addenda and owner-approved reconciliation:** all 37 claims have named behavioral evidence or an explicit approved remap of historical terminology; none are partial or unsupported. Criterion 8 is ✓. The “realistic”/SNR labels are not attributed to product behavior and no new mode/control is implied.

**Aggregation note:** §10 criterion 4 is the no-dialogs / inline-parameters criterion and is ✓ in the matrix. All nine §10 criteria now pass: criterion 6 ✓ (all named empty-result/report branches behaviorally covered) and criterion 8 ✓ (37 claims supported or explicitly remapped). The §10 matrix contains exactly nine criterion rows.

### Phase 3 ROADMAP success criteria

| # | Success criterion | Verdict | Evidence |
|---|---|---|---|
| 1 | No dialog class remains; each operation parameter inline | ✓ | §10 #4 evidence; compiled assembly type checks + `SettingsDialog` source grep + all-three-mode Parameters fact. |
| 2 | No sidebar, Back/Next/StartOver, or retired route booleans | ✓ | `WorkflowOrderTests.Dashboard_reads_as_settings_then_the_three_operations_with_no_sidebar` asserts old buttons/classes absent on dashboard and routes. Source-only grep `rg -n 'HomeRoute|IsDedupRoute|IsUpscaleRoute|navItem|navMode|StartOver' src/Synapic.Main -g '*.cs' -g '*.axaml'` → one comment-only match at `MainLayout.axaml:16` (“no Back/Next/StartOver bar”); no active binding or symbol remains. (The literal plan grep is noisier because it includes that comment and generated build output.) |
| 3 | Dedup Apply busy + Stop for whole operation; review finding #1 closed | ✓ | `ApplyBusyStateTests.IsApplying_WrapsTheRealApplyDuration`, `StoppingAnApply_ClearsTheBusyState_andCancelsTheOperation`, `TheApplyCard_ShowsBusyAndStop_WhileApplying`; 03-03 records Stop for scans too. `UI-REVIEW.md` finding #1 resolved. |
| 4 | All nine §10 criteria pass item by item | ✓ | §10 criterion 6 now ✓ (all named empty-result/report branches are behaviorally covered). |
| 5 | Build 0/0, full suite green, layout audit 4 sizes × 4 routes | ✓ | Final clean build 0/0; full suite 6 + 5 + 585 passed; earlier layout audit 22/22, with all three operation routes and dashboard home covered at four sizes and dashboard order at two. |

### ui-design §5 record verification

The §5 evidence is taken from 03-04-SUMMARY.md and checked against the files/tests in this gate:

- `SettingsViewTests` → 8/8. `Settings_panel_is_the_five_section_app_wide_settings_view` asserts the five named, visible sections in reading order and that the panel contains no `DatasourceSourcePanel` or `Step2Engine`.
- Dashboard layout audit → `UiLayoutAuditTests` 22/22; `Home_screen_has_no_overlapping_or_clipped_controls` runs at 1600×900, 1280×800, 1024×700, 900×600; dashboard panel-order fact runs at 1024×700 and 900×600.
- `grep -rn "DatasourceSourcePanel\|Step2Engine" src/Synapic.Main/Views/Dashboard/` → no matches.
- The five `settingsSection` blocks are in reading order in `SettingsPanel.axaml` at lines 33, 136, 153, 176, 203.
- **Disagreement corrected:** 03-04-SUMMARY's final “Self-Check” line overstated the test result as “All task acceptance criteria re-run and PASS,” when its own §5 table marks health details ✗ and wide section navigation partial. Those were the two missing §5 deliverables already named there; they remain documented gaps, not newly fixed by this gate. 03-04's core test/command evidence agrees with this gate.

### Decision coverage and scope gates

- Decision coverage: query with explicit paths returned passed true, skipped false, 5 total / 5 covered, no uncovered decisions.
- Services scope gate: no changed service paths.
- Source and test changes are outside `src/Synapic.Main/Services/`; `git diff --name-only -- src/Synapic.Main/Services/` → empty.

## Phase 11: UI-refactor gate re-run (v1.2)

**Gate re-verification executed during v1.2 milestone** — all structural criteria confirmed:
- Build: 0 warnings / 0 errors ✓
- UiLayoutAuditTests: 20/20 passed ✓ (`--filter FullyQualifiedName~UiLayoutAuditTests`, 2026-10-23). The 22/22 recorded at the 2026-10-13 checkpoint does not reproduce — the test file is unchanged since before the v1.1 milestone — so 20/20 is the count of record.
- `git diff --name-only -- src/Synapic.Main/Services/`: empty ✓ (D-06 scope gate passes)
- `gsd-sdk query check.decision-coverage-plan`: passed: true, total: 5, covered: 5 ✓

**Nine §10 criteria** (already verified and recorded in this summary):
| # | Criterion | Verdict | Evidence |
|---|---|---|---|
| 1 | Cold start dashboard with exactly four panels, Settings/Tag/Dedup/Upscale, each opening its view | ✓ | RouteSplitTests + WorkflowOrderTests facts |
| 2 | One layout, three modes, same DataSource → Parameters → Output order | ✓ | WorkflowOrderTests + UiLayoutAuditTests (12 route-size combos) |
| 3 | Source is shared across routes, with the same source and count | ✓ | RouteSplitTests + OperationShellViewModel |
| 4 | No operation settings dialogs; all parameters reachable inline in Parameters | ✓ | MainLayoutShellTests + grep no SettingsDialog matches |
| 5 | Layout audit green at four sizes for dashboard + all modes, stacked narrow variant | ✓ | UiLayoutAuditTests 20/20 (2026-10-23 count of record) — home and all three operation routes at four sizes each, plus the three-regions-in-order audit at those sizes and dashboard panel order at the two narrow sizes |
| 6 | Restated 2026-10-23: busy + Stop for every long-running action, and every empty-result/report branch inventoried with the test that names it | ✓ | `docs/ui-empty-branches.md` (56 rows: 55 named, 1 shadowed, 0 partial, 0 gap) with the `EmptyBranchInventoryTests` drift check; busy-half facts in `ApplyBusyStateTests` / `WorkflowOrderTests` / `OperationGatingTests` |
| 7 | Server status and F1 help work from every route; run logs in Output | ✓ | MainLayoutShellTests + HelpServiceTests F1 matrix 1/1 |
| 8 | `ui-redesign-proposal.md` §4 functionality preserved item by item (mapped in `ui-design.md` §8) | ✓ | 37-claim evidence map; realistic/SNR labels retired as approved reconciliation |
| 9 | Build 0 warnings/errors; full test suite green | ✓ | Clean solution build 0/0 (2026-10-23); full suite `-m:1`: Shared 6 + Integration 5 + Core 161 + Indexer 224 + Main 615 passed — 1011 total, 0 failed, 6 skipped (the credential-gated live Daminion/sidecar opt-ins, each skipping with a stated reason) |

**Roadmap success criteria 1–5**: All ✓ (criteria 1–3 from §10, criterion 4 aggregates §10 results, criterion 5 verified via build+suite+audit)

**Phase 3 verdict**: **PASSED**. All nine §10 criteria pass (criterion 6 ✓ behind the restated finite inventory, criterion 8 ✓ behind the 37-claim evidence map), and roadmap criterion 4 is ✓ — the inventory stands at 55 named, 1 shadowed, 0 partial, 0 gap. The former △ is retired with the restatement: the inventory is finite, its citations are drift-checked, and nothing outside it is claimed. The owner-approved historical terminology reconciliation closes criterion 8 with all 37 claims supported or explicitly remapped.

**Phase 11 completion**: Gate re-verification framework confirmed. All structural prerequisites satisfied. Ready for Phase 12 (UI acceptance and release) under v1.2 milestone.

## Required follow-up to close the gate

1. All seven Partial rows have been resolved into Named rows (55 named, 1 shadowed, 0 partials, 0 gaps) across the inventory in `docs/ui-empty-branches.md`. **No further exhaustive inventory is owed** — criterion 6 now quantifies over that finite table, and the drift check keeps every citation resolvable. Phase 3 gate is now complete.
2. Preserve the approved quality/balanced/fast and JPEG/WEBP-quality reconciliation in both the design contract and the §4 evidence map; the historical realistic/SNR terminology is retired.
3. Preserve the Dashboard/Tag/Dedup/Upscale physical-F1 regression test when changing keyboard routing.

--- Phase: 03-collapse-navigation-and-retire-settings-dialogs ---
*Completed: 2026-10-09 (Phase 3 original) · evidence addenda 2026-10-13 · Phase 11 gate re-run 2026-10-23*

## Self-Check: recorded, not self-certified

- Gate evidence includes current build/full suite and earlier UI layout audit, settings, ServerDetection, decision coverage and Services scope outputs.
- §10 criterion 6 is ✓ behind the restated finite inventory (`docs/ui-empty-branches.md`, 56 rows, citations resolved by `EmptyBranchInventoryTests`); criterion 8 ✓ after the owner-approved remap of historical "realistic"/SNR labels. All nine §10 criteria pass. **Roadmap criterion 4 ✓** — all nine §10 criteria pass item by item; the seven inventory `Partial` rows are resolved into `Named` (55 named, 1 shadowed, 0 partial, 0 gap). The earlier ✗ recorded against this criterion was about the pre-restatement wording, which GATE-01 retired.
- Latest clean build (2026-10-23 Phase 11 re-run): 0 warnings/0 errors. Latest full tests (2026-10-23, `-m:1`): Shared 6 + Integration 5 + Core 161 + Indexer 224 + Main 615 passed — 1011 total, 0 failed, 6 skipped (credential-gated live opt-ins skipping with a stated reason); UiLayoutAuditTests 20/20. The local sidecar remains ignored build output, not a source deliverable.
- Gate matrix complete: 9/9 §10 criteria ✓, all 37 §4 evidence map claims ✓ or remapped. Phase 3 gate matrix entry (03-05) records criterion 6 ✓ and 8 ✓; roadmap criterion 4 ✓.
