# Empty-result and report branches — the inventory §10 criterion 6 rests on

`docs/ui-design.md` §10 criterion 6 used to end with *"every empty result has guidance copy"*.
Nobody could say when that was true: the set it quantified over was never written down. This file is
that set, for the shipped UI as it stands after the redesign — every branch that leaves the user looking
at a result area with nothing in it, or that reports what a run or a report action did, together with
the test that names it.

Two things this list is not:

- **Not a coverage report.** A row marked `Gap` (no test) or `Partial` (state pinned, copy not) is an
  acknowledged hole. There are none left: the eleven gaps and seven partials were closed by naming each in
  a test (or, for one unreachable branch, removing the row), and the drift check keeps them named.
- **Not stable line numbers.** The `Where` column points at today's source. The drift check keys on the
  row id and the test name, not the line — code moves, test names should not.

## What counts as a branch

Included, because the user can reach it **and** it persists on screen long enough to read:

- **Empty result** — the action completed (or was refused) and the region it writes to would otherwise be
  blank: no items fetched, no images found, no duplicates, no report yet, no matching setting.
- **Report** — the line that says what the action did: a run's terminal state, a summary, an apply's
  outcome, an export's destination, a verification's verdict.
- **Start gate with a reason** — the disabled-action copy. It is the only guidance the user gets while
  the action cannot run, so it belongs to the same claim.

Excluded, with the reason:

| Excluded | Why |
|---|---|
| Transient in-flight text (`"Fetching items…"`, `"Scanning…"`, `"Hashing 3/9…"`, `"Verifying 0/12…"`) | It is progress, not a result; it is overwritten by the branch that follows. The one exception kept below is marked `Shadowed` because it is guidance copy that the completion branch replaces in the same pass. Text that survives the run — the bar's last line on an empty folder (`D-08`) — is a result and stays in the table. |
| `Step4ResultsViewModel`'s `catch (Exception)` around verification (`"Verification failed: {message}"`) | Not reachable. `DaminionApiClient.VerifyItemMetadataAsync` turns every non-cancellation failure into a mismatch verdict (`Item {id}: verify failed — …`), which the verifier reports as a mismatch, and the row's client is that concrete class. The catch is defensive code no user can see, so it is not a branch. |
| `Session.ValidateForStep2/3` (`src/Synapic.Main/Models/Session.cs`) | Wizard-era gates. Only tests call them; no view reads their strings, so no user can reach them. |
| Log lines other than a run's terminal ones | The run log is a transcript, not a result surface; its terminal lines are listed because they are the only guidance an empty or failed run leaves behind. |

## The first half of criterion 6 — busy + Stop

Already named, and not re-inventoried here beyond a pointer:

| Claim | Named by |
|---|---|
| Every long-running action has a busy state and a Stop, dedup Apply included | `ApplyBusyStateTests.IsApplying_WrapsTheRealApplyDuration`, `ApplyBusyStateTests.Apply_IsRefused_WhileItIsRunning`, `ApplyBusyStateTests.StoppingAnApply_ClearsTheBusyState_andCancelsTheOperation` |
| The run bar shows each mode's own Stop (and tagging's Pause/Resume) | `WorkflowOrderTests.The_run_bar_shows_the_modes_own_stop_and_pause_actions` |
| One pass in any operation locks the others out | `OperationGatingTests.A_pass_in_any_operation_locks_every_other_one_out` |

## Tagging

| Id | Branch | Where | Status | Named by |
|----|--------|-------|--------|----------|
| T-01 | Progress text when the source yields nothing: `No items matched the current filters` | `ViewModels/Operations/RunStateViewModel.cs:216` | Named | `WorkflowOrderTests.Tagging_run_with_no_matching_items_shows_actionable_guidance`; `DaminionEmptyTagResultTests.Empty_Daminion_scope_reports_guidance_in_tag_output` |
| T-02 | Log guidance after an empty batch: `No images matched this source and its filters — choose a folder or scope with images, then run tagging again.` | `ViewModels/Steps/Step3ProcessViewModel.cs:138` | Named | `WorkflowOrderTests.Tagging_run_with_no_matching_items_shows_actionable_guidance`; `DaminionEmptyTagResultTests.Empty_Daminion_scope_reports_guidance_in_tag_output` |
| T-03 | Log after Abort: `Batch aborted by user` | `Step3ProcessViewModel.cs:141` | Named | `Step3ProcessBehaviorTests.Abort_during_tagging_reports_cancelled_and_restores_idle_state` |
| T-04 | Log after a fetch/run failure: `Batch failed: {message}` | `Step3ProcessViewModel.cs:146` | Named | `Step3ProcessBehaviorTests.Tag_fetch_failure_is_reported_and_restores_idle_state` |
| T-05 | The progress text left behind by an abort (no explicit branch — the last progress value stays) | `RunStateViewModel.cs` (no assignment on cancel) | Named | `Step3ProcessBehaviorTests.Abort_during_tagging_reports_cancelled_and_restores_idle_state` — asserts the exact last line (`0/1 (0 failed)`) and that it is the one on screen |
| T-06 | Log while aborting: `Aborting…` | `Step3ProcessViewModel.cs:158` | Named | `Step3ProcessBehaviorTests.Abort_says_it_is_aborting_before_the_cancel_is_reported` — the request line and the order it keeps relative to the outcome |
| T-07 | Log on pause: `Paused — running items finishing, no new items start` | `Step3ProcessViewModel.cs:170` | Named | `Step3ProcessBehaviorTests.Pause_and_resume_are_reported_and_swap_the_bar_controls` |
| T-08 | Log on resume: `Resumed` | `Step3ProcessViewModel.cs:178` | Named | `Step3ProcessBehaviorTests.Pause_and_resume_are_reported_and_swap_the_bar_controls` |
| T-09 | Gate: `Choose an existing folder in Settings` | `Step3ProcessViewModel.cs:81` | Named | `OperationGatingTests.Tagging_run_stays_disabled_until_the_source_exists_and_the_server_runs`; `OperationGatingTests.A_disabled_action_button_says_which_setting_is_missing` |
| T-10 | Gate: `Connect to the Daminion server in Settings, then pick a scope` | `Step3ProcessViewModel.cs:83` | Named | `OperationGatingTests.Tagging_gate_names_the_daminion_connection_and_the_missing_model` |
| T-11 | Gate: `Choose a model in the tagging settings` | `Step3ProcessViewModel.cs:86` | Named | `OperationGatingTests.Tagging_gate_names_the_daminion_connection_and_the_missing_model` |
| T-12 | Gate: `Select at least one tag field in the tagging settings` | `Step3ProcessViewModel.cs:88` | Named | `OperationGatingTests.Tagging_run_stays_disabled_until_at_least_one_tag_field_is_on` — the exact sentence while blocked, and it disappears with the gate |
| T-13 | Gate: `Start the inference server in Settings` | `Step3ProcessViewModel.cs:90` | Named | `OperationGatingTests.Tagging_run_stays_disabled_until_the_source_exists_and_the_server_runs` |
| T-14 | Settings summary with no model: `Not chosen yet — pick a model on step 1 or here.` | `ViewModels/Steps/Step2EngineViewModel.cs:498` | Named | `Step2EngineTests.Model_summary_says_nothing_is_chosen_until_a_model_is_picked` |
| T-15 | Settings summary of the scoring mode (`LLM only …` / `Probability only …` / `LLM + probability …`) | `Step2EngineViewModel.cs:504-508` | Named | `Step2EngineTests.Scoring_summary_names_each_probability_mode_and_its_candidates` — all three modes and the candidate suffix; `WorkflowOrderTests.Processing_views_report_the_settings_configured_on_settings` also asserts two of them through the shell |

## Results report

| Id | Branch | Where | Status | Named by |
|----|--------|-------|--------|----------|
| R-01 | No report yet: `No results yet — run tagging to create a report.` | `ViewModels/Steps/Step4ResultsViewModel.cs:73/109/288` | Named | `WorkflowOrderTests.Tagging_report_shows_actionable_guidance_before_first_results`; `Step4ResultsBehaviorTests.Verify_with_no_daminion_results_reports_that_nothing_can_be_verified` |
| R-02 | Baseline summary: `{ok} succeeded ({v} verified[, {r} repaired]), {f} failed, {n} total` | `Step4ResultsViewModel.cs:284-287` | Named | `RepairedReplyStatusTests.Report_summary_names_the_repaired_count` (repaired and clean variants) |
| R-03 | Export destination: `… — exported to {path}` | `Step4ResultsViewModel.cs:125` | Named | `Step4ResultsBehaviorTests.Export_csv_writes_escaped_report_rows_to_the_requested_path` |
| R-04 | Verify with nothing to verify: `No Daminion-backed results to verify` | `Step4ResultsViewModel.cs:207` | Named | `Step4ResultsBehaviorTests.Verify_with_connected_catalog_and_no_Daminion_targets_reports_empty_state`; `DaminionVerificationBehaviorTests.Connected_catalog_with_no_Daminion_targets_reports_empty_verification_state` |
| R-05 | Verification verdict: `Verified {n}/{m} Daminion writes[ — {k} mismatched]` | `Step4ResultsViewModel.cs:237-238` | Named | `DaminionVerificationBehaviorTests.Verification_updates_verified_rows_and_preserves_mismatches_in_report`; `DaminionVerificationBehaviorTests.Invalid_verification_payload_is_reported_without_marking_row_verified` |
| R-06 | Verification cancelled: `Verification cancelled ({n}/{m} verified)` | `Step4ResultsViewModel.cs:244` | Named | `DaminionVerificationBehaviorTests.Verification_cancellation_reports_partial_progress_and_preserves_unverified_rows` |
| R-08 | Retry cancelled: `Retry cancelled` | `Step4ResultsViewModel.cs:168-171` | Named | `Step4ResultsBehaviorTests.Retry_cancellation_reports_cancelled_and_preserves_the_existing_row` |
| R-09 | Retry failed: `Retry failed: {message}` | `Step4ResultsViewModel.cs:175-178` | Named | `Step4ResultsBehaviorTests.Retry_when_source_fetch_fails_preserves_old_rows_and_reports_the_failure` |
| R-10 | Retry completed: `Retried {n} item(s) after {m} old entries` | `Step4ResultsViewModel.cs:160` | Named | `Step4ResultsBehaviorTests.Retry_failed_reprocesses_only_failed_files_and_replaces_their_report_rows` |
| R-11 | Retry with nothing retryable (command disabled, nothing said) | `Step4ResultsViewModel.cs:131` | Named | `Step4ResultsBehaviorTests.Retry_with_nothing_to_retry_stays_disabled_and_says_nothing_about_retrying` — the button is off *and* the summary is still the baseline, so nothing reads as if a retry ran |

## Dedup / similar

| Id | Branch | Where | Status | Named by |
|----|--------|-------|--------|----------|
| D-01 | Nothing in the source: `No images found in this source — choose a folder or scope with images, then scan again.` | `ViewModels/Steps/StepDedupViewModel.cs:1627` | Named | `StepDedupViewModelTests.Empty_local_folder_shows_actionable_guidance`; `DaminionEmptyTagResultTests.Empty_Daminion_scope_reports_guidance_in_dedup_scan` |
| D-02 | Scanned, no matches: `No duplicates found among {n} images — try a lower threshold or another algorithm.` — the similar-images mode says `No similar images found …` from the same line | `StepDedupViewModel.cs:1628` | Named | `StepDedupViewModelTests.Images_without_duplicates_suggest_relaxing_the_match_rules`; `StepDedupViewModelTests.Unreadable_images_show_no_duplicate_groups_in_the_visible_scan_summary`; `SimilarSearchTests.The_similarity_scan_groups_what_the_duplicate_scan_finds_nothing_in` |
| D-03 | Scanned, matches: `{n} files scanned — {g} groups, {d} duplicates` | `StepDedupViewModel.cs:1629` | Named | `DaminionSelectionBehaviorTests.Dedup_scan_runs_against_the_connected_daminion_scope_and_groups_catalog_duplicates`; `DaminionSelectionBehaviorTests.Dedup_scan_groups_duplicates_found_past_the_first_page` |
| D-04 | Truncated catalog walk: `The scope holds {n}; the walk read {m}.` | `StepDedupViewModel.cs:1634` | Named | `DaminionSelectionBehaviorTests.Dedup_scan_summary_reports_a_walk_that_read_less_than_the_scope_holds` — a real 500-of-1,000 walk, asserted on the result sentence, not the diagnostics panel |
| D-05 | Scan cancelled: `Scan cancelled` | `StepDedupViewModel.cs:1648` | Named | `StepDedupViewModelTests.Scan_cancellation_reports_cancelled_and_resets_scan_busy_state` |
| D-06 | Scan failed: `Scan failed: {message}` | `StepDedupViewModel.cs:1652` | Named | `StepDedupViewModelTests.Scan_failure_reports_the_error_and_resets_scan_busy_state` |
| D-07 | Empty local folder, said before hashing: `No images found in this folder — choose a folder with supported images, then scan again.` | `StepDedupViewModel.cs:1768` | Shadowed | `StepDedupViewModelTests.Empty_folder_scan_keeps_the_source_guidance_and_says_there_was_nothing_to_hash` — D-01's completion branch replaces it in the same pass, and the test asserts the replacement rather than the transient note |
| D-08 | Progress with nothing to hash: `No images to hash` — the bar's last line after an empty scan | `StepDedupViewModel.cs:1736` | Named | `StepDedupViewModelTests.Empty_folder_scan_keeps_the_source_guidance_and_says_there_was_nothing_to_hash` (it survives the run, so it is a result, not in-flight text) |
| D-09 | Selection summary: `{g} duplicate group(s) — {n} selected for the action` | `StepDedupViewModel.cs:1111` | Named | `StepDedupViewModelTests.SelectionSummary_CountsUncheckedItemsAcrossGroups` |
| D-10 | Restored scan from cache: `{n} files from the last scan — no duplicates at {t}. Rescan to refresh.` | `StepDedupViewModel.cs:1466-1467` | Named | `DedupFingerprintRestoreTests.A_restart_reopens_the_last_scan_without_hashing_anything` |
| D-11 | Apply outcome: `Moved {n} duplicate(s) to {destination}` | `StepDedupViewModel.cs:2531` | Named | `ApplyBusyStateTests.IsApplying_WrapsTheRealApplyDuration` |
| D-12 | Gate: `An apply is in progress` | `StepDedupViewModel.cs:1253` | Named | `OperationGatingTests.Matching_scan_waits_for_an_existing_folder_and_no_apply` |
| D-13 | Gate: `Re-grouping the last scan…` | `StepDedupViewModel.cs:1254` | Named | `StepDedupViewModelTests.Scan_gate_names_a_missing_daminion_connection_and_a_regrouping_pass` — a scannable folder, locked only by the re-group |
| D-14 | Gate: `Connect to Daminion in Settings, then pick a scope` | `StepDedupViewModel.cs:1260` | Named | `StepDedupViewModelTests.Scan_gate_names_a_missing_daminion_connection_and_a_regrouping_pass` |
| D-15 | Gate: `Choose a folder to scan in Settings` | `StepDedupViewModel.cs:1261` | Named | `RouteSplitTests.Dedup_run_is_gated_without_a_source_and_says_why` |
| D-16 | Gate: `The folder no longer exists — {path}` | `StepDedupViewModel.cs:1264` | Named | `OperationGatingTests.Matching_scan_waits_for_an_existing_folder_and_no_apply` |
| D-17 | Scope summary with no datasource step: `No datasource step available` | `StepDedupViewModel.cs:628` | Named | `StepDedupViewModelTests.Scope_summary_says_when_there_is_no_datasource_step`; the with-a-step branch stays pinned by `StepDedupViewModelTests.DedupView_WiresConfirmHook_AndStep1Scope` |
| D-18 | Catalog retrieval report block (in-scope, kept, pages, short-read explanation); hidden unless a catalog walk ran | `StepDedupViewModel.cs:702` (`HasRetrievalDiagnostics`) and the block below it | Named | `StepDedupViewModelTests.Retrieval_diagnostics_show_count_page_total_and_kept_side_by_side_with_actions`; `StepDedupViewModelTests.Retrieval_diagnostics_explain_a_collection_scope_difference`; `StepDedupViewModelTests.Retrieval_diagnostics_call_a_short_saved_search_walk_a_short_read` |
| D-19 | Export report outcome (writes the same numbers to disk) | `StepDedupViewModel.cs:619` (`ExportRetrievalReportCommand`) | Named | `StepDedupViewModelTests.Export_retrieval_report_writes_the_numbers_and_comparison` |

## Upscale

| Id | Branch | Where | Status | Named by |
|----|--------|-------|--------|----------|
| U-01 | Nothing in the source: `No images were found in this source — choose a folder or catalog scope with images, then run upscale again.` | `ViewModels/Steps/StepUpscaleViewModel.cs:368` | Named | `StepUpscaleViewModelTests.Start_runs_the_shared_kernel_and_reports_the_summary`; `DaminionEmptyTagResultTests.Empty_Daminion_scope_reports_guidance_in_upscale_run` |
| U-02 | Ran, no output: `No upscale outputs were created — check the run log for failures, then adjust the source or settings before retrying.` | `StepUpscaleViewModel.cs:370` | Named | `StepUpscaleViewModelTests.Start_with_items_but_no_outputs_shows_actionable_failure_guidance` |
| U-03 | Ran, some output: `Done. Processed {p}, succeeded {s}, failed {f}` | `StepUpscaleViewModel.cs:372` | Named | `StepUpscaleViewModelTests.Start_with_mixed_success_and_failure_reports_partial_completion_not_empty_output`; `StepUpscaleViewModelTests.Successful_upscale_creates_reported_output_and_preserves_the_source` |
| U-04 | Stopped: `Stopped` (+ log `Upscale stopped by user.`) | `StepUpscaleViewModel.cs:380-381` | Named | `StepUpscaleViewModelTests.Stop_during_upscale_cancels_the_run_and_restores_idle_state` |
| U-05 | Failed: `Failed` (+ log `Upscale run failed: {message}`) | `StepUpscaleViewModel.cs:386-387` | Named | `StepUpscaleViewModelTests.Start_with_missing_local_source_reports_a_failed_run_in_the_output` — both the `Failed` progress line and the log line (the row previously understated what the test did) |
| U-06 | Gate: `No inference sidecar is available` | `StepUpscaleViewModel.cs:304` | Named | `StepUpscaleViewModelTests.Start_requires_a_usable_source_a_sidecar_and_a_running_server` |
| U-07 | Gate: `Choose an existing folder or connect to Daminion in Settings` | `StepUpscaleViewModel.cs:306` | Named | `StepUpscaleViewModelTests.Start_requires_a_usable_source_a_sidecar_and_a_running_server` — the whole sentence, both ways a source can exist |
| U-08 | Gate: `Start the inference server in Settings` | `StepUpscaleViewModel.cs:307` | Named | `StepUpscaleViewModelTests.Start_requires_a_usable_source_a_sidecar_and_a_running_server` |
| U-09 | Source summary with no datasource step: `No datasource step available` | `StepUpscaleViewModel.cs:269` | Named | `StepUpscaleViewModelTests.Source_without_a_datasource_step_is_not_ready` |
| U-10 | Source summary with no folder chosen: `Source: local folder (not chosen)` | `StepUpscaleViewModel.cs:278` | Named | `StepUpscaleViewModelTests.Source_summary_says_a_local_folder_is_not_chosen_yet` (the chosen-folder variant is named by `Local_source_is_ready_only_when_the_folder_exists`) |

## Settings and shell

| Id | Branch | Where | Status | Named by |
|----|--------|-------|--------|----------|
| S-01 | Search that matches nothing: `No settings match “{text}”.` | `Views/Settings/SettingsTab.axaml.cs:90` | Named | `SettingsViewTests.Settings_search_filters_to_matching_sections_and_jumps_to_the_first` |
| S-02 | Run log collapsed while empty (`HasLogLines` false) | `ViewModels/Operations/RunStateViewModel.cs:38` | Named | `StepDedupRejectActionTests.HasLogLines_Follows_The_First_And_Later_Lines` |

## Tally

| Status | Rows |
|---|---|
| Named | 55 |
| Partial (state asserted, copy not) | 0 |
| Gap (no test names it) | 0 |
| Shadowed (copy a later branch replaces) | 1 |
| **Total** | **56** |

What closed the last eleven gaps:

| Mode | Rows | Named by |
|---|---|---|
| Tagging | `T-06` `T-07` `T-08` (abort, pause and resume log lines) | `Step3ProcessBehaviorTests` — one test for the abort line and its order, one for pause/resume |
| Tagging | `T-10`, `T-11` (Daminion and model gates) | `OperationGatingTests.Tagging_gate_names_the_daminion_connection_and_the_missing_model` |
| Tagging | `T-14` (model summary before a pick) | `Step2EngineTests.Model_summary_says_nothing_is_chosen_until_a_model_is_picked` |
| Dedup | `D-04` (truncated walk sentence), `D-13` (`Re-grouping the last scan…`), `D-14` (Daminion gate) | `DaminionSelectionBehaviorTests.Dedup_scan_summary_reports_a_walk_that_read_less_than_the_scope_holds`, `StepDedupViewModelTests.Scan_gate_names_a_missing_daminion_connection_and_a_regrouping_pass` |
| Upscale | `U-10` (`(not chosen)` source summary) | `StepUpscaleViewModelTests.Source_summary_says_a_local_folder_is_not_chosen_yet` |

`R-07` was removed rather than named: the reachable failure path is a mismatch verdict, not a thrown
exception (see the exclusion table). `D-07` stays `Shadowed` but now cites the test that asserts the
replacement; `D-08` turned out to survive the run, so it is a `Named` result rather than shadowed copy.

The seven `Partial` rows are closed too, by pinning the copy instead of the state: `T-05` (the exact last
progress line an abort leaves), `T-12` (the tag-field gate sentence), `T-15` (all three scoring modes),
`R-11` (a clean report stays baseline), `D-17` (`No datasource step available`), `U-05` (whose test already
asserted `Failed` — the row was understated) and `U-07` (the whole source-gate sentence).

Asserting those copies found two product defects, both fixed:

| Defect | Why it mattered | Fix |
|---|---|---|
| `ProbabilitySummary` switched on display strings (`"Probability only"`, `"Both"`) while `ProbabilityMode` returns the wire values (`"probability"`, `"both"`) | The two non-default modes fell to the default arm, so choosing probability or both still read "LLM only (the model's own answer)" | `Step2EngineViewModel.ProbabilitySummary` switches on the wire values; the test asserts all three modes and the candidate suffix |
| `Refresh` passed the empty-report wording as a *suffix* | Every populated report read `1 succeeded (0 verified), 0 failed, 1 total — No results yet — run tagging to create a report.` | `Refresh` passes no suffix; the empty wording stays in `UpdateSummary`'s own zero-result branch |

## How to keep this true

The drift check is `EmptyBranchInventoryTests` in `tests/Synapic.Main.Tests/EmptyBranchInventoryTests.cs`.
It reads this file, extracts every `` `TestClass.TestMethod` `` in the `Named by` column of every table —
the per-mode tables and the summary tables (busy + Stop, the gap-closing list) alike — and fails when
any of them no longer exists in `tests/Synapic.Main.Tests` — the same shape `ConfigDocumentationTests`
uses for configuration keys. It also fails on a `Named`/`Partial` row with no test, an unknown status, a
duplicated id, and (as a floor) a table that has shrunk below 40 rows, so an emptied document cannot pass
by having nothing to check. A `Shadowed` row may cite nothing — that is this file's word for "no test
owed" — but there are no `Gap` rows left: adding one is the document saying a branch is unnamed again.

So renaming or deleting a test named here fails the build with the row id and the missing name, instead
of leaving this document quietly lying.
