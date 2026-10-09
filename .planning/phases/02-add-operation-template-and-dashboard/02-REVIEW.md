---
phase: 02-add-operation-template-and-dashboard
review_date: 2026-10-09
depth: standard
status: fixed
diff_base: 19c4dff7^   # first phase-2 commit's parent
files_reviewed: 16
findings:
  critical: 1
  warning: 1
  info: 6
  total: 8
---

# Phase 2 Code Review — operation template, shared source, dashboard

## Summary

Reviewed the phase's 16 changed source files (15 production + StepDedup code-behind pair; test files excluded from findings) at standard depth: per-file reading of the diffs, then targeted runtime probes of the real compiled visual tree for anything a diff cannot show.

**One Critical and one Warning were found and fixed in this pass; six Info-level notes are recorded and left as-is.** The Critical was invisible to the phase's own gates: the region assertions count region *markers*, and the layout audit flags clipped or overlapping controls — neither looks at the *text* a region renders, so Region B printing a raw view-model type name passed every gate.

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

Out of scope by scope rule: `src/Synapic.Main/Services/` (phase gate proved it untouched), the four rewritten test files, and the planning artifacts.

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

The phase's file scope was computed from the git diff (`19c4dff7^..HEAD`, planning artifacts and tests excluded), the depth from config (standard). Because the two hosts' defect was invisible to both the diff and the existing gates, the review additionally drove the compiled UI headlessly and dumped the rendered text and region children, then used the same harness to prove the fix before deleting it.

---
*Reviewed: 2026-10-09 — Phase 2 (02-add-operation-template-and-dashboard)*
