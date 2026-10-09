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

key-decisions:
  - "The gate is PARTIAL / NOT PASSED: criteria 6's every-empty-state coverage and 8's preservation checklist lack complete evidence; Phase 3 success criterion 4 therefore cannot be ✓"
  - "The literal criterion-2 grep is source-filtered and records the one comment-only match rather than treating it as an active navigation symbol"
  - "The decision-coverage query is run with explicit phase/context paths; without them the SDK returned a vacuous skipped pass (CONTEXT.md missing)"

requirements-completed: []

# Metrics
duration: 35min
completed: 2026-10-09
---

# Phase 3 Plan 05: Final acceptance gate Summary

**Phase 3 final gate is NOT PASSED / incomplete: build, full tests, layout audit, structure, busy states, all-route F1 dispatch and §5 record have green evidence, but §10 criterion 6 (every empty-result state) remains partially covered and criterion 8 (item-by-item feature preservation) contains partial and unsupported checklist claims.**

## Verification commands and outputs

All commands below ran in this checkout after the latest source changes; commands were run directly (without an output-filtering pipeline), so their reported exit codes are the tested command exit codes.

- `dotnet build Synapic.Net.sln --nologo -v minimal` → **Build succeeded, 0 Warning(s), 0 Error(s), exit 0**.
- `dotnet test Synapic.Net.sln -c Release --no-restore --nologo` → Shared **6/6**, Integration **5/5**, Main **463/463**, 0 failed / 0 skipped, **exit 0**.
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
| 1 | Cold start dashboard with exactly four panels, Settings/Tag/Dedup/Upscale, each opening its view | ✓ | `RouteSplitTests.Dashboard_shows_exactly_four_panels_named_Settings_Tag_Dedup_Upscale` asserts the exact four names, visible at cold start and wired operation cards; `WorkflowOrderTests.Settings_entry_point_opens_the_settings_panel_and_never_a_window` pins Settings in place; RouteSplit facts pin Tag/Dedup/Upscale entry. Full suite 463/463. |
| 2 | One layout, three modes, same DataSource → Parameters → Output order | ✓ | `WorkflowOrderTests.All_three_modes_render_the_same_three_regions_in_order` walks all modes and checks region order and the same source instance; `UiLayoutAuditTests.Operation_modes_audit_clean_with_the_three_regions_in_order` checks actual geometry and clean layout at four sizes for all three routes (12 route-size combinations). |
| 3 | Source is shared across routes, with the same source and count | ✓ | `RouteSplitTests.Region_A_strip_shows_the_same_source_and_count_on_every_route` refreshes a real local source and checks same view-model instance, count and label across Tag/Dedup/Upscale; `OperationShellViewModel` source wiring also checked. Full suite green. |
| 4 | No operation settings dialogs; all parameters reachable inline in Parameters | ✓ | `MainLayoutShellTests.Tagging_parameters_are_inline_in_the_parameters_region_not_a_window` asserts all three dialog types absent from the built assembly and the Tag form inside Parameters; `WorkflowOrderTests.Every_route_renders_its_parameters_inline_in_the_parameters_region` checks Tag/Dedup/Upscale forms and bindings. `grep -rn "SettingsDialog" src/ --include=*.cs --include=*.axaml` → no matches. |
| 5 | Layout audit green at four sizes for dashboard + all modes, stacked narrow variant | ✓ | `UiLayoutAuditTests` 22/22: `Home_screen_has_no_overlapping_or_clipped_controls`, `Tagging_mode_has_no_overlapping_or_clipped_controls`, and `Dedup_and_upscale_modes_have_no_overlapping_or_clipped_controls` each run at 1600×900, 1280×800, 1024×700, 900×600; `Operation_modes_audit_clean_with_the_three_regions_in_order` runs three modes at those same four sizes; `Dashboard_panels_read_in_order_and_audit_clean` checks panel order/layout at 1024×700 and 900×600. Limitation: dashboard wide 2×2 breakpoint remains unimplemented (reported in the Phase 2 record). |
| 6 | Every long-running action has busy + Stop; every empty result has guidance copy | ✗ | Busy + Stop evidence remains: shared `RunStateViewModel` for Tag/Upscale, Stop binding in `StepUpscale.axaml`, scan `IsScanning`/`StopScanCommand`, Apply `IsApplying`/`StopApplyCommand`; `ApplyBusyStateTests` pins Apply lifecycle and rendered Stop. Empty-state guidance was added and is now pinned for pre-run Tag (`WorkflowOrderTests.Tagging_report_shows_actionable_guidance_before_first_results`), empty local Dedup (`StepDedupViewModelTests.Empty_local_folder_shows_actionable_guidance`), no-match Dedup (`StepDedupViewModelTests.Images_without_duplicates_suggest_relaxing_the_match_rules`), and empty Upscale source (`StepUpscaleViewModelTests.Start_runs_the_shared_kernel_and_reports_the_summary`). Code also supplies guidance for all-failed upscale output, but no named test covers that branch; Daminion-specific zero-result and tag zero-match execution are not separately exercised. Partial coverage only; the criterion remains ✗ because the requirement is “every” state and these remaining branches are not covered. |
| 7 | Server status and F1 help work from every route; run logs in Output | ✓ | `MainLayoutShellTests.Server_status_and_help_stay_reachable_on_the_dashboard_and_every_mode` checks status and Help in all four contexts; `HelpServiceTests.F1_key_dispatch_opens_the_context_topic_on_dashboard_and_every_route` sends physical F1 on Dashboard, Tag, Dedup and Upscale and asserts the opened topic. `WorkflowOrderTests.RunStateBar_renders_the_run_state_it_binds` renders a run log in Output; `SettingsViewTests.Diagnostics_toggle_shows_and_hides_the_shell_log_view` checks diagnostics reachability through Settings. The focused F1 matrix is **1/1**; the focused full sweep is **143/143** and the solution suite is **6 + 5 + 463 passed, 0 failed**. |
| 8 | `ui-redesign-proposal.md` §4 functionality preserved item by item (mapped in `ui-design.md` §8) | ✗ | The 37-claim evidence map immediately below has an individual citation and a coverage grade for every checked bullet. It distinguishes test-backed behavior, source-only implementation evidence, and unverified/mismatched claims; this is not converted to ✓ merely because the solution suite passes. |
| 9 | Build 0 warnings/errors; full test suite green | ✓ | Exact commands above: build 0/0 exit 0; solution test Shared 6, Integration 5, Main 463 passed; 0 failures/skips exit 0. |

### §4 preservation evidence map (37 checked claims)

Legend: **✓** named behavioral test(s) support the capability; **△** implementation/source or adjacent behavior is present, but no focused end-to-end/regression assertion proves the complete claim; **✗** the specific claim is not evidenced or the current UI differs from the stated capability. Destinations refer to `docs/ui-design.md` §8. The map records evidence, not a claim that a source reference alone is equivalent to a passing test.

| # | Checklist claim | Destination (§8) | Evidence / honest coverage |
|---:|---|---|---|
| 1 | Local folder with recursive scan | Shared Data source | ✓ `WorkflowRunnerTests.Fetch_picks_up_only_images_and_honours_recursion`; `Step1DatasourceTests.Recursive_toggle_recounts_the_folder_it_changes`. |
| 2 | Daminion connection with catalog scope | Shared Data source | ✓ `DaminionConnectionStoreTests.Save_then_Load_round_trips_every_Step1_field`, `Step1DatasourceTests.Daminion_source_needs_a_live_session_not_just_typed_credentials`, `RouteSplitTests.Dedup_mode_reads_the_catalog_source_from_the_shared_source`; the live-session facts are environment-gated. |
| 3 | Filters (status, untagged fields) | Shared Data source | △ `DaminionConnectionStoreTests.Step1_preserves_non_default_scope_and_filters` proves the selected status/untagged fields survive hydration; `Step1DatasourceViewModel` and `WorkflowRunner` pass those fields into count/fetch. No focused test asserts returned catalog items obey both filters. |
| 4 | Max items / pagination controls | Shared Data source | △ `ProcessingEtaTests.ProcessAllFlowsFromStep1IntoTheSelection` proves the unlimited flag reaches the request selection; `WorkflowRunner` contains bounded and process-all page loops and `SourceLimitsPanel.axaml` renders the controls. No paging-loop regression test covers page boundaries/repeated IDs. |
| 5 | Model selection (local models, Hugging Face, manual ID) | Tag → Parameters | △ `WorkflowOrderTests.Tagging_model_picker_lives_in_the_parameters_region_not_the_dashboard` pins the real local-model picker and its VM; `EngineSettingsStoreTests.Step2_engine_fields_hydrate_from_store` covers a persisted manual repo ID. No named test exercises local model selection/download or the full picker’s Hugging Face flow. |
| 6 | Device selection (CPU/CUDA/MPS) | Tag → Parameters | ✓ `SidecarDeviceSelectionTests` covers offered devices, persisted fallback and propagation; `Step2EngineTests` covers the live bound device selector. Availability is hardware-injected/detected. |
| 7 | Task type (multimodal) | Tag → Parameters | ✓ `Step2EngineTests.PinsMultimodalTask_AndCorrectsStaleSessionValue` pins `image-text-to-text`. |
| 8 | Tag fields (Keywords/Categories/Description) | Tag → Parameters | ✓ `TagFieldCheckboxTests.ClickingATagFieldCheckbox_ReachesTheViewModelAndTheSession`, `TagFieldSelectionTests.AllFields_Default_WritesEverything` and field-exclusion facts. |
| 9 | Confidence & probability thresholds | Tag → Parameters | △ `Step2EngineTests.Sensitivity_sliders_write_through_to_the_view_model` pins both sliders; `EngineSettingsStoreTests` round-trips both values. No named test proves each threshold changes a completed inference result. |
| 10 | Candidate labels | Tag → Parameters | △ Candidate values round-trip in `EngineSettingsStoreTests.Save_then_Load_round_trips_all_engine_fields`; the visible field/source path exists, but no focused test pins candidate parsing through a tagging request and response. |
| 11 | System/User prompts with presets | Tag → Parameters | ✓ `Step2PromptBindingTests` pins the editable prompt and controls; `SystemPromptPresetTests.Picking_a_preset_fills_the_box_and_the_session` plus preset-store tests cover persistence/history; `TagInstructionTests` pins reset/custom instruction behavior. |
| 12 | Processing limits (max items, thumbnail override) | Tag → Parameters / shared Data source limits | △ Max-item/process-all transfer is pinned by `ProcessingEtaTests.ProcessAllFlowsFromStep1IntoTheSelection`; `DaminionConnectionStoreTests` round-trips max items, resize and thumbnail override. `ProcessingOrchestrator` implements the thumbnail path, but no focused test verifies the override changes the submitted image. |
| 13 | Export CSV | Tag → Output | △ `Step4Results.axaml` binds Export CSV to `Step4ResultsViewModel.ExportCsvCommand`, and that implementation writes escaped result fields. No named test exercises the picker/write/cancel or CSV contents. |
| 14 | Retry failed | Tag → Output | △ `Step4ResultsViewModel.RetryFailedAsync` filters non-success results and runs the shared kernel; the control is bound in `Step4Results.axaml`. No named test invokes retry and asserts repaired result state. |
| 15 | Verify Daminion writes | Tag → Output | ✓ `EndToEndRoundTripTests.Daminion_round_trip_against_live_server` performs the Daminion round-trip and calls metadata verification; this is a live-server integration fact, not guaranteed by a default offline run. |
| 16 | Local folder scan | Dedup → Parameters / Output | ✓ `StepDedupViewModel.LocalScan_LoadsAPreviewThumbnail_ForEveryGroupedRow`, `WorkflowRunnerTests.Fetch_picks_up_only_images_and_honours_recursion`, and empty/no-match scan facts pin local scanning and rendered output. |
| 17 | Daminion catalog scan | Dedup → Parameters / Output | △ `DedupScanHandlerTests.ItemsWithServerHash_AreGroupedWithoutDownload`, `ItemsWithoutServerHash_FallBackToDownloadAndHash`, and `MixedItems_SplitBetweenServerHashAndDownload` exercise catalog item hashing; `StepDedupViewModel` wires the shared Daminion source. No complete UI-to-catalog scan test is named. |
| 18 | Algorithm selection (hamming/exact) | Dedup → Parameters | △ `StepDedupViewModelTests.BuildOptions_ClampsTheAlgorithmIndexToTheKnownSet` pins UI option mapping; `DedupServiceTests` covers perceptual threshold/Hamming grouping and exact server-hash grouping. The complete selectable algorithm set has no one fact per algorithm. |
| 19 | Threshold control | Dedup → Parameters | ✓ `DedupServiceTests.Grouping_RespectsThreshold` checks grouping changes at the threshold; `StepDedupViewModelTests.BuildOptions_ClampsTheAlgorithmIndexToTheKnownSet` and `WorkflowOrderTests.Run_pages_report_the_settings_that_moved_to_the_parameters_region` cover the setting’s rule/read-back path. |
| 20 | Server hash matching options | Dedup → Parameters | ✓ `StepDedupViewModelTests.BuildOptions_CarriesTheServerHashRule`, `ServerHashPicker_IsDaminionOnly_AndBindsBothWays`, and `DedupServiceTests.ServerHashes_ExactRule_MergesOnlyIdenticalValues` / `ServerHashes_HammingRule_GroupsLikeTheAlgorithmicPath`. |
| 21 | Auto-select rules (oldest/newest/smallest/largest) | Dedup → Parameters | ✓ `StepDedupViewModelTests.AutoSelect_Oldest_ChecksOldestPerGroupByDate`, `AutoSelect_Newest_ChecksNewestPerGroupByDate`, and `AutoSelect_SmallestAndLargest_UnionTheirPicks`. |
| 22 | Action selection (tag, move, delete) | Dedup → Output | ✓ `StepDedupViewModelTests.LocalTagAndMove_NeverPrompt`, `LocalDelete_Declined_KeepsFiles_Accepted_DeletesThem`, `ConfirmAction_DefaultsToNull_SoCatalogDeleteFailsClosed`, and `DaminionDeleteRequestTests.Delete_posts_to_MediaItems_Remove_with_catalog_only_flag`. |
| 23 | Duplicate group review with thumbnails | Dedup → Output | ✓ `StepDedupViewModelTests.ItemRows_ShowThePreviewImage_OnlyWhenAThumbExists`, `LocalScan_LoadsAPreviewThumbnail_ForEveryGroupedRow`, and `DedupServiceTests.CreateThumbnail_ScalesToTheBoxAndReturnsAJpeg`. |
| 24 | Feature enhancement upscaling | Upscale → Parameters / Output | △ `UpscaleItemHandler` contains local and Daminion image enhancement paths, including Daminion checkout/download/check-in; `DaminionVersionControlRequestTests` covers the check-in protocol. `StepUpscaleViewModelTests` only runs an empty source, so no test proves a successful enhanced output end-to-end. |
| 25 | Quality/realistic/balanced presets | Upscale → Parameters | ✗ The current selector/tested values are `quality`, `balanced`, `fast` (`StepUpscaleViewModelTests.BuildOptions_maps_dropdown_indices_to_the_wire_values`). No distinct `realistic` choice or test establishing equivalence to the checklist’s historical “realistic” preset is present. |
| 26 | Scale factors | Upscale → Parameters | ✓ `StepUpscaleViewModelTests.BuildOptions_maps_dropdown_indices_to_the_wire_values` checks 4× and `BuildOptions_clamps_every_parameter_to_the_contract_ranges` checks 2×/4× endpoints. |
| 27 | Precision (float32/float16) | Upscale → Parameters | ✓ `StepUpscaleViewModelTests.BuildOptions_maps_dropdown_indices_to_the_wire_values` checks `fp32`; its clamp test checks `auto` and `fp32`, and the UI/source uses the `fp16` wire option. |
| 28 | Output format (PNG/JPEG) | Upscale → Parameters / Output | ✓ `StepUpscaleViewModelTests.BuildOptions_maps_dropdown_indices_to_the_wire_values` checks WEBP and the default/low-index `keep`; the UI exposes `keep`, JPEG, PNG and WEBP. Direct output-file encoding is not asserted by this VM test. |
| 29 | Quality/SNR sliders | Upscale → Parameters | ✗ JPEG/WEBP quality is a NumericUpDown (`UpscaleSettingsPanel.axaml`) and is included in BuildOptions tests; no SNR property/control or test is present. The named SNR claim is not substantiated by the current implementation evidence. |
| 30 | Sharpen controls | Upscale → Parameters | ✓ `StepUpscaleViewModelTests.BuildOptions_maps_dropdown_indices_to_the_wire_values` and its clamp test pin the sharpen value/range; `UpscaleSettingsPanel.axaml` binds the slider. |
| 31 | Overwrite options | Upscale → Parameters | ✓ `StepUpscaleViewModelTests.BuildOptions_maps_dropdown_indices_to_the_wire_values` checks false and the run summary checks the default true value; output handler receives the option. |
| 32 | Start/Stop/Build server controls | Settings → Inference server | ✓ `SettingsViewTests.Inference_server_section_operates_the_server_through_the_shell` checks Start/Stop and buildable variant rows; `ServerDetectionTests` covers detection/build lifecycle. |
| 33 | Download progress | Settings → Inference server | ✓ `ServerDetectionTests.Download_progress_shows_bytes_and_percent`, `Download_without_total_shows_bytes_only`, and `SidecarDownloadServiceTests.Progress_ends_at_100`. |
| 34 | Sidecar variants (CPU/CUDA) | Settings → Inference server | ✓ `ServerDetectionTests.Variants_list_one_row_per_buildable_rid_for_this_machine`, `Cuda_variant_is_offered_on_windows_only`, and `SidecarDeviceSelectionTests.Cuda_prefers_the_cuda_variant_and_still_falls_back_to_the_cpu_one`. |
| 35 | Health polling | Settings → Inference server | △ `MainWindowViewModel.EnsureHealthPolling` / `PollHealthAsync` run while server starts/runs and update download/device status; `ServerDetectionTests` pins applying health payloads, but not the periodic poll lifecycle/timing. |
| 36 | Device fallback notice | Settings → Inference server (+ status in shell) | ✓ `ServerDetectionTests.Cpu_fallback_under_a_cuda_selection_is_called_out` and `Any_other_mismatch_is_stated_without_the_cuda_advice`. |
| 37 | Log view | Per-mode Output; full diagnostics in Settings → Logging | ✓ `WorkflowOrderTests.RunStateBar_renders_the_run_state_it_binds` pins the per-run Output log; `SettingsViewTests.Diagnostics_toggle_shows_and_hides_the_shell_log_view` pins diagnostics access. |

**Evidence map result:** 23 claims have named behavioral test evidence, 12 are partial/source-only, and 2 checklist claims remain unsupported (#25’s realistic preset and #29’s SNR control). The count intentionally does not convert partial rows into preservation passes. Criterion 8 remains ✗ until the owner accepts/remaps the two mismatches and closes the partial-evidence gaps required by the checklist.

**Aggregation note:** §10 criterion 4 is the no-dialogs / inline-parameters criterion and is ✓ in the matrix. Separately, Phase 3 ROADMAP success criterion 4 requires all nine §10 criteria to pass; because §10 criteria 6 and 8 are ✗, that ROADMAP criterion is **✗**. The §10 matrix contains exactly nine criterion rows.

### Phase 3 ROADMAP success criteria

| # | Success criterion | Verdict | Evidence |
|---|---|---|---|
| 1 | No dialog class remains; each operation parameter inline | ✓ | §10 #4 evidence; compiled assembly type checks + `SettingsDialog` source grep + all-three-mode Parameters fact. |
| 2 | No sidebar, Back/Next/StartOver, or retired route booleans | ✓ | `WorkflowOrderTests.Dashboard_reads_as_settings_then_the_three_operations_with_no_sidebar` asserts old buttons/classes absent on dashboard and routes. Source-only grep `rg -n 'HomeRoute|IsDedupRoute|IsUpscaleRoute|navItem|navMode|StartOver' src/Synapic.Main -g '*.cs' -g '*.axaml'` → one comment-only match at `MainLayout.axaml:16` (“no Back/Next/StartOver bar”); no active binding or symbol remains. (The literal plan grep is noisier because it includes that comment and generated build output.) |
| 3 | Dedup Apply busy + Stop for whole operation; review finding #1 closed | ✓ | `ApplyBusyStateTests.IsApplying_WrapsTheRealApplyDuration`, `StoppingAnApply_ClearsTheBusyState_andCancelsTheOperation`, `TheApplyCard_ShowsBusyAndStop_WhileApplying`; 03-03 records Stop for scans too. `UI-REVIEW.md` finding #1 resolved. |
| 4 | All nine §10 criteria pass item by item | ✗ | §10 criteria 6 and 8 are ✗ above; the phase gate cannot report all nine green. |
| 5 | Build 0/0, full suite green, layout audit 4 sizes × 4 routes | ✓ | Build and full-suite commands above; audit 22/22, with the three operation routes at all four sizes, dashboard home at four sizes, and narrow dashboard order at two. The design’s `4 sizes × 4 routes` is covered by home + Tag + Dedup + Upscale size cases; additional route-order/layout fact is limited to two dashboard sizes. |

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
- Source changes in the current work are limited to VM presentation/empty-state guidance and tests; no Services paths changed, confirmed by the empty scope grep.

## Phase verdict

**NOT PASSED / incomplete.** Five roadmap criteria are not all green because roadmap criterion 4 (all nine §10 criteria) is explicitly ✗: §10 criteria 6 (untested empty-result branches) and 8 (partial and unsupported checklist claims) remain incomplete. The feature redesign's main structure, layout, build, suite, and §5 testing are in good shape, but this gate does not invent missing proof.

## Required follow-up to close the gate

1. Add rendered/behavioral tests for remaining empty-result paths: zero-item Daminion Tag/Dedup, Upscale all-failed output, and any other empty report route; then re-evaluate criterion 6.
2. Resolve or explicitly retire/remap the unsupported realistic preset and SNR slider claims in proposal §4, then add behavioral coverage for the currently partial rows to close criterion 8.
3. Preserve the new Dashboard/Tag/Dedup/Upscale physical-F1 regression test when changing keyboard routing.

---
*Phase: 03-collapse-navigation-and-retire-settings-dialogs*
*Completed: 2026-10-09*

## Self-Check: recorded, not self-certified

- Gate evidence includes current build, full suite, UI layout audit, settings, ServerDetection, decision coverage and Services scope outputs.
- §10 criteria 6 and 8, and therefore Phase 3 roadmap criterion 4, are explicitly ✗; no all-green phase claim is made. Current full suite is 6 + 5 + 463 passed; focused gate tests passed after correcting two mismatched test assumptions.
