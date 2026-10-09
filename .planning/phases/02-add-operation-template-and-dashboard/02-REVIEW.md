---
phase: 02-add-operation-template-and-dashboard
review_date: 2026-10-09
depth: standard
status: fixed
diff_base: 19c4dff7^   # first phase-2 commit's parent
files_reviewed: 20            # 16 source (main pass) + the 4 rewritten test files (addendum)
findings:
  critical: 1
  warning: 3                 # WR-1 + PA-1, PA-2 (addendum)
  info: 10                   # IN-1..IN-6 + PA-3..PA-6 (addendum)
  total: 14
---

# Phase 2 Code Review — operation template, shared source, dashboard

## Summary

Reviewed the phase's 16 changed source files (15 production + StepDedup code-behind pair; test files excluded from findings) at standard depth: per-file reading of the diffs, then targeted runtime probes of the real compiled visual tree for anything a diff cannot show.

**One Critical and one Warning were found and fixed in this pass; six Info-level notes are recorded and left as-is.** The Critical was invisible to the phase's own gates: the region assertions count region *markers*, and the layout audit flags clipped or overlapping controls — neither looks at the *text* a region renders, so Region B printing a raw view-model type name passed every gate.

**The addendum at the end audits the four test files the phase rewrote, which this pass excluded by scope rule; it found and fixed two name/assertion contradictions and four smaller gaps.**

## Scope

```
src/Synapic.Main/ViewModels/MainWindowViewModel.cs
src/Synapic.Main/ViewModels/Operations/ShellViewModel.cs            (new)
src/Synapic.Main/ViewModels/Steps/StepDedupViewModel.cs
src/Synapic.Main/ViewModels/WizardViewModel.cs
src/Synapic.Main/Views/Dashboard/DashboardView.axaml(.cs)           (new)
src/Synapic.Main/Views/MainLayout.axaml(.cs)
src/Synapic.Main/Views/Operation/DataSourceStrip.axaml(.cs)         (new)
src/Synapic.Main/Views/Operation/OperationLayout.axaml(.cs)         (new)
src/Synapic.Main/Views/Operation/RunStateBar.axaml(.cs)             (new)
src/Synapic.Main/Views/Wizard/StepDedup.axaml(.cs)
```

Out of scope by scope rule: `src/Synapic.Main/Services/` (phase gate proved it untouched) and the planning artifacts. The four rewritten test files are covered by the addendum at the end of this document, so nothing in the phase is left unreviewed.

## Critical

### CR-1 — Unmatched content rendered the view-model type name as visible UI text — FIXED

**Where:** [OperationLayout.axaml](src/Synapic.Main/Views/Operation/OperationLayout.axaml) Region B (`parametersRegion`) and Region C's run-bar host.

**What:** Both hosts are `ContentControl`s bound `Content="{Binding}"` with a *partial* template list — Region B has templates for dedup and upscale only, the run-bar host for the three run steps only. Avalonia's `ContentPresenter` falls back to a `TextBlock` holding the content's `ToString()` when nothing matches, so every tagging step showed `Synapic.Main.ViewModels.Steps.Step2EngineViewModel` (and Step1/Step3/Step4 equivalents) as literal text under the "Parameters" and "Output" headings, and dedup/upscale showed the same thing on the Datasource step. Tagging's Region B is *documented* as intentionally empty until Phase 3 ([02-01-SUMMARY](.planning/phases/02-add-operation-template-and-dashboard/02-01-SUMMARY.md) — "Tagging's Region B stays empty until Phase 3"), so the render contradicted the recorded intent.

**Evidence (headless probe over the real `MainWindow`, before the fix):**

```
===== tagging step 2 =====
TEXT: Parameters
TEXT: The settings this operation runs with.
TEXT: Synapic.Main.ViewModels.Steps.Step2EngineViewModel     ← the defect
TEXT: Output
TEXT: Synapic.Main.ViewModels.Steps.Step2EngineViewModel     ← and again
```

**Why the gates missed it:** criterion 2 counts the three region borders; `UiLayoutAuditTests` looks for unusual bounds, not text. A type name is neither clipped nor overlapping.

**Fix:** a final catch-all template in both hosts that renders an empty `Panel` (it must be last, so the real templates still win), with the reason in a comment. `DataTemplate` matching is inheritance-aware, confirmed empirically rather than assumed: before = 6 stray type-name TextBlocks across 7 probed steps, after = 0, with dedup/upscale still resolving to `DedupSettingsPanel`/`UpscaleSettingsPanel` and the run steps to `RunStateBar`.

**Regression test:** [WorkflowOrderTests.cs](tests/Synapic.Main.Tests/WorkflowOrderTests.cs) — `No_step_renders_a_view_model_type_name_as_text` walks all seven steps of the three routes and fails on any rendered text starting `Synapic.`. A future step view model that lands without a template now fails a test instead of shipping.

## Warning

### WR-1 — `ShellViewModel`'s contract claimed consumers it does not have — FIXED

**Where:** [ShellViewModel.cs](src/Synapic.Main/ViewModels/Operations/ShellViewModel.cs).

**What:** The class described `Current` as "the only answer to which operation is open — the dashboard's panels, the existing route commands and the help scope all read the same value". The route machine *writes* it and the navigation pins *read* it, but: the dashboard panels bind `StartTaggingRouteCommand`/`StartDedupRouteCommand`/`StartUpscaleRouteCommand` directly (never `Shell.Open`), the help scope reads `ContextHelpTopic`/`Wizard.CurrentStepIndex`, and `Open`, `Home`, `Operations` and `Operation(key)` have **no** caller in `src/` or `tests/` (grep verified). So the "one navigation authority" invariant is enforced by tests alone, and the doc would teach a reader the opposite.

**Impact:** a maintenance hazard, not a runtime defect: someone trusting the doc could delete `Route` (the real machine) or add a second navigation path believing `Shell` already rules.

**Fix:** corrected the class and `Open` docs to state exactly who writes (the route methods), who reads (the pins; help reads something else), and that `Open`/`Home` are Phase 3's surface with nothing binding them yet. `RenderState`-style behaviour is unchanged — no test or binding moved.

## Info

| ID | Finding | Note / disposition |
|----|---------|--------------------|
| IN-1 | `ShellViewModel.Open/Home/Operations/Operation(key)` unused by any view or test | Kept: they are the intended Phase 3 entry points; now documented as such. |
| IN-2 | `SetCurrent("typo")` silently yields the dashboard, and `Open("typo")` silently no-ops — a key typo would leave `Route` and `Current` disagreeing with no signal | The keys are literals in two places (`MainWindowViewModel` route methods, `CreateAdapter`-style keys). Consider failing loudly (or asserting the key set once) when Phase 3 makes `Shell` the entry point. |
| IN-3 | `RunStateBar` Start binds `IsVisible="{Binding !IsRunning}"` **and** `IsEnabled="{Binding IsIdle}"` — `IsIdle` is `!IsRunning`, so the same fact is bound twice | Harmless today; the `IsEnabled` binding is the redundant one. Left as-is to avoid churn in a pinned view. |
| IN-4 | `RunStateBar` Start button carries the generic label "Start" for tagging, dedup and upscaling | Deliberate until Phase 3 moves the per-mode CTA copy onto the operation contract. |
| IN-5 | `MainLayout.axaml.cs` unsubscribes the log handler from the *new* DataContext only, so swapping in a different `MainWindowViewModel` leaves the old subscription attached | Pre-existing (not introduced by this phase); no production path swaps the DataContext after load, and the window calls `DetachLog()` on close. Worth a `-=` on the previous `_vm` if Phase 3 re-hosts the layout. |
| IN-6 | After the dedup source card was deleted, `StepDedupViewModel.FolderPath`/`DatasourceType` have no binding of their own — the UI edits `Step1` (Region A / the dashboard Settings panel) and dedup reads through | Intended (D-02). The private fallback fields are reachable only from tests; noted so nobody "restores" the second source card. |

## Positive observations (checked, no action)

- **The shared-source mirror is complete.** `DatasourceType` and `FolderPath` read/write through `Step1` and re-notify on `_step1.PropertyChanged`; the scan itself uses the shared values (`CanScan`, file enumeration, `ScopeIdForLog`, `_step1.ConnectedClient`), so "the source configured once is the source every mode runs on" holds beyond the display.
- **Run wiring is correct per mode:** dedup's inherited `StartCommand` runs the scan (`StartCoreAsync => ScanAsync`), tagging's runs the batch with the shared client, upscaling's builds its selection from the shared source.
- **`Route` and `Shell.Current` cannot drift** on any current path: all four `Route = …` assignments sit next to their `SetCurrent`, and no other assignment exists.
- **The dashboard reads only real state:** `SourceCountText` re-raises on `CountText` changes and shows "no source configured yet" rather than inventing a number; the Resync button is genuinely gated (`CanCount => !IsCounting && HasUsableSource`), matching the strip's comment.

## Verification

| Check | Command | Result |
|-------|---------|--------|
| Build (warnings are errors in this repo) | `dotnet build Synapic.Net.sln --nologo -v minimal` | exit 0, **0 Warning(s) 0 Error(s)** |
| Full suite | `dotnet test Synapic.Net.sln -c Release --nologo` | exit 0 — Shared **6/6**, Integration **5/5**, Main **442/442** (was 441; +1 regression fact), 0 failed, 0 skipped |
| Affected test families after the fix | `--filter "…RouteSplitTests|…UiLayoutAuditTests|…WorkflowOrderTests"` | exit 0 — **41/41** |
| Stray type names (before/after) | headless probe over the real window, seven steps | **6 → 0**; dedup/upscale panels and run bars still resolve |
| Probe file removed | `tests/Synapic.Main.Tests/TempProbeTests.cs` deleted | yes — no probe code shipped |

## Review method note

The phase's file scope was computed from the git diff (`19c4dff7^..HEAD`, planning artifacts and tests excluded), the depth from config (standard). Because the two hosts' defect was invisible to both the diff and the existing gates, the review additionally drove the compiled UI headlessly and dumped the rendered text and region children, then used the same harness to prove the fix before deleting it.---

# Addendum (2026-10-09) — pin audit of the four rewritten test files

The main pass above excluded the four test files the phase rewrote (02-04), so nothing had checked whether the rewritten *pins* still say what they verify. This addendum closes that gap: every fact the rewrite created, renamed or edited — 25 names across [RouteSplitTests.cs](tests/Synapic.Main.Tests/RouteSplitTests.cs), [WorkflowOrderTests.cs](tests/Synapic.Main.Tests/WorkflowOrderTests.cs), [MainLayoutShellTests.cs](tests/Synapic.Main.Tests/MainLayoutShellTests.cs) and [UiLayoutAuditTests.cs](tests/Synapic.Main.Tests/UiLayoutAuditTests.cs) — was read name-first, then compared against what its assertions can actually fail on, with the views and view models those assertions reach into.

**Two Warning-level name/assertion contradictions and four smaller gaps were found; all six are fixed.** No fact was added or deleted, so the case counts are unchanged (Main **442/442**).

## PA-1 — `Home_returns_to_the_dashboard_and_the_route_resumes_where_it_was` claimed a resume its own assertions disprove — FIXED

**Where:** [RouteSplitTests.cs](tests/Synapic.Main.Tests/RouteSplitTests.cs); renamed from `Home_returns_to_the_chooser_and_the_route_resumes_where_it_was` by 02-04, which carried the false clause through the rename.

**What:** the fact opens *dedup*, advances to the dedup step, returns Home, then enters a **different** route (tagging) and asserts `CurrentStepIndex == 0` — a fresh start. Nothing resumes: `WizardViewModel.EnterTaggingRoute`/`EnterDedupRoute`/`EnterUpscaleRoute` each set `CurrentStep = Step1`, and `MainWindowViewModel.GoHome` only sets `Route` and `Shell.Current`. The fact's own body comment contradicted its name ("Picking the other route later starts it from the Datasource step"), and re-entering the *same* route resumes no more than switching does — the step always returns to the source.

**Impact:** a pin that promises resume semantics reads as proof that mid-flow position survives a trip to the dashboard — which Phase 3 would then have to preserve, or silently break.

**Fix:** renamed to `Home_returns_to_the_dashboard_and_another_route_opens_at_the_source`, with the body comment stating what actually survives a trip to the dashboard (the configured source and settings, not the step position). The same false claim lived in production — `GoHome`'s doc said "route state is kept, so returning resumes" — and was corrected there too ([MainWindowViewModel.cs](src/Synapic.Main/ViewModels/MainWindowViewModel.cs)).

## PA-2 — `Dashboard_entry_is_free_and_only_the_run_is_gated` asserted a route gate its name denies — FIXED

**Where:** [RouteSplitTests.cs](tests/Synapic.Main.Tests/RouteSplitTests.cs) — the D6 replacement fact itself.

**What:** the fact asserted `Assert.False(vm.CanStartRoute)` (no source) and `Assert.True(vm.CanStartRoute)` (source) — the **retired** card gate. `CanStartRoute`'s own doc called it "the start screen's gate: the three workflow cards stay disabled until the source panel has a usable source", and ui-design D6 is exactly what removed it ("entering a mode is free; only *actions* are gated … Replaces `CanStartRoute` card gating"). Grep confirms no view binds it any more (no `.axaml` reference; only the property, its doc, `docs/csharp-reference.md`, `docs/ui-design.md` and these two assertions). So a fact named "only the run is gated" was the last thing in the tree asserting a second, route-level gate — one that no longer exists on screen.

**Why it survived the rewrite:** the D6 re-point checked the *cards* (now enabled) and the *run* gate, and carried the old predicate's assertion along with the strip-light checks it happened to sit between.

**Fix:** both assertions dropped from the fact; every D6 guarantee it does pin (panels enterable, run gated with `RunDisabledReason`, then run open with no reason) is unchanged. The fact's doc now records why `CanStartRoute` is asserted nowhere, and the property's stale doc says it is retired, unbindable, and kept only for Phase 3 to delete deliberately.

## PA-3 — `Operation_entry_returns_to_the_chooser_from_any_step` tested exactly one step — FIXED

**Where:** [WorkflowOrderTests.cs](tests/Synapic.Main.Tests/WorkflowOrderTests.cs). The file was rewritten by 02-04 but this fact predates it, which is why the rewrite's rename pass skipped it.

**What:** "from any step" — the body entered tagging, jumped to step 3, went Home, and asserted home visibility once. "Chooser" also names the screen the redesign replaced: the assertions are `IsHomeVisible`/`IsWizardVisible`/`IsNavHomeActive`, i.e. the dashboard.

**Fix:** renamed `Operation_entry_returns_to_the_dashboard_from_any_step` and made the name true — it walks all four tagging steps, asserts it really reached the step it names, and asserts the way back from each, with failure messages that name the step.

## PA-4 — the audit's four "Tagging step N" cases never verified which step they audited — FIXED

**Where:** [UiLayoutAuditTests.cs](tests/Synapic.Main.Tests/UiLayoutAuditTests.cs) — `Tagging_steps_have_no_overlapping_or_clipped_controls`.

**What:** the theory's four cases are named for the four steps and each calls that step's `GoToStepNCommand`, but the only thing asserted about *where* the audit ran is `_panelsAudited > 10`. A gate that silently refuses the move — `GoToStep2` does exactly that on an invalid source, setting `ValidationError` and returning — would leave all four cases auditing the source step and reporting green: four cases named for four screens, all measuring one.

**Fix:** each case now asserts `CurrentStepIndex` equals the step it names, before the audit runs.

## PA-5, PA-6 (Info)

| ID | Finding | Note / disposition |
|----|---------|--------------------|
| PA-5 | `Dashboard_shows_exactly_four_panels_named_Settings_Tag_Dedup_Upscale`'s doc claimed "each one opens its view" | Only the three operation panels open a mode; the Settings panel renders its content in place and has no open command (§5's settings view is Phase 3). Doc corrected to say so. |
| PA-6 | `Region_A_strip_shows_the_same_source_and_count_on_every_route` deconstructed its route name and never used it | A failure said nothing about which route broke. Added a per-route `EffectivelyVisible` assertion carrying the route in its message — which is also the "shows … on every route" half of the name. |

## Checked, no action

- **`Dashboard_lays_the_source_panel_out_above_the_operation_panels` / `Dashboard_panels_and_route_visibility_are_bound_in_the_real_window`** — plural names; the second was measured through two of the three cards and now checks the upscale card as well. The layout fact keeps measuring the row through one card, which is sound because `Dashboard_panels_read_in_order_and_audit_clean` pins all three panels' shared row and left-to-right order at two sizes.
- **`All_three_modes_render_the_same_three_regions_in_order`, `RunStateBar_renders_the_run_state_it_binds`, `No_step_renders_a_view_model_type_name_as_text`** — names match their assertions. `RunStateBar` is bound to the same `Step3ProcessViewModel` the template hands it, so the fact exercises the real binding surface rather than a stand-in.
- **The facts the rewrite deliberately left alone** (sidebar entry and active marks, tabs, settings dialogs) — unchanged so Phase 3 starts from a known list. Facts outside the four rewritten files that still say "chooser" (e.g. `UpscaleRouteTests.Home_returns_to_the_chooser_…`) keep the pre-rename vocabulary and belong to Phase 3's wording sweep.

## Addendum verification

| Check | Command | Result |
|-------|---------|--------|
| Build (warnings are errors) | `dotnet build Synapic.Net.sln --nologo -v minimal` | exit 0 — **0 Warning(s) 0 Error(s)** |
| The four rewritten files | `dotnet test tests/Synapic.Main.Tests/… --filter "…RouteSplitTests\|…WorkflowOrderTests\|…UiLayoutAuditTests\|…MainLayoutShellTests"` | exit 0 — **44/44** |
| Full suite | `dotnet test Synapic.Net.sln -c Release --nologo` | exit 0 — Shared **6/6**, Integration **5/5**, Main **442/442**, 0 failed, 0 skipped |
| No fact added or deleted | case count before/after | Main **442 → 442**; the fixes are assertion- and name-level only |

---

*Reviewed: 2026-10-09 — Phase 2 (02-add-operation-template-and-dashboard)*
