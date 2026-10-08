# UI Refactor Plan — Reusing the Operation Template

**Date:** 2026-10-08
**Implements:** [`ui-design.md`](ui-design.md) (SSOT) — specifically §2.2 "one shared operation layout"
**Verdict:** **Yes, refactor — scoped to the presentation layer.** Not a rewrite.
**Status:** Proposal awaiting approval to schedule as phases

---

## 1. Why this plan exists

`ui-design.md` mandates *one layout, three modes*: every mode renders
`Data source → Parameters → Output/Report`. Today each mode is a bespoke page
with its own copy-pasted run machinery and its own navigation branch, so the
design **cannot be built without first standardizing the modes behind a
contract**. This document evaluates the drift, recommends the contract, and
sequences the work so the app stays shippable after every step.

---

## 2. Evaluation: the codebase has drifted — evidence

### 2.1 What is healthy (do not touch)

| Layer | State |
|---|---|
| Run kernel | **Converged.** `WorkflowRunner` + `WorkflowDefinition` + `IWorkflowItemHandler` ([WorkflowRunner.cs](../src/Synapic.Main/Services/Processing/WorkflowRunner.cs)) is shared by tagging (`ProcessingOrchestrator` wraps it), upscale (`WorkflowRunner().RunAsync`), dedup scan (`RunItemsAsync`), results retry, and source counting. |
| Services/DI/tests | Interfaces exist where they should (`IInferenceSidecar`, `IDedupService`, `IMetadataWriter`…); 422+ headless tests green; `UiLayoutAuditTests` gates geometry. |
| Settings binding | The "one view model, two views" pattern (dialog hosts the page's own VM) already works — evidence the codebase reached for reuse ad hoc, three times. |

### 2.2 What has drifted

**D1 — Run-state block copy-pasted per mode.**
[Step3ProcessViewModel.cs](../src/Synapic.Main/ViewModels/Steps/Step3ProcessViewModel.cs) (226 lines) and
[StepUpscaleViewModel.cs](../src/Synapic.Main/ViewModels/Steps/StepUpscaleViewModel.cs) (336 lines) share
near-verbatim: `ProgressPercent / ProgressText / EtaText / CurrentFile /
IsRunning / IsIdle`, the `OnIsRunningChanged` command-refresh, the
`StartAsync` CTS lifecycle, an **identical `Progress<ProcessProgress>` mapper
with the ETA format string**, and an **identical `AppendLog`** (UI-thread
marshal → timestamp → cap 2000) — that log helper exists verbatim twice more
in `MainWindowViewModel` and `SidecarVariantViewModel`. Dedup forks the
pattern again with different names (`IsScanning`, `ScanSummary`, no progress
bar state), and has **no `IsApplying` busy state** for Apply — the standing
`UI-REVIEW.md` carried-forward finding #1.

**D2 — Three sources of truth for the data source.** Tagging reads `Step1`
directly; upscale re-derives `SourceSummary`/`SourceReady` from it; **dedup
keeps its own `DatasourceType`/`FolderPath` radio + browse** and
`WizardViewModel.PrefillDedupSource()` copies the choice over. Three read
strategies for what `ui-design.md` D4 defines as one shared strip.

**D3 — Three coexisting navigation models.** (a) linear wizard
(`Next`/`Back`, `CurrentStepIndex` 0–5 switched in ~8 places);
(b) route split (`IsDedupRoute`/`IsUpscaleRoute` — two booleans manually kept
mutually exclusive in three `Enter*Route()` methods); (c) numbered sidebar +
start-screen chooser. Both `WizardViewModel` (490 lines) and
`MainWindowViewModel` (1055 lines) switch on route/step for
`NextButtonText`, `CurrentStepTitle`, `RouteTitle`, `CreateSettingsDialog`,
`NavHintText`, six `IsXxxActive` flags…

**D4 — The shell hardcodes every route.** [MainLayout.axaml](../src/Synapic.Main/Views/MainLayout.axaml) (412 lines):
3 route cards, per-route `IsVisible` sidebar entries, 6 step `DataTemplate`s,
Back/Next/Start-Over action bar. Adding a mode today means editing the shell,
the wizard, and both view models.

**D5 — Settings exist four times per mode.** dialog + embedded panel +
page read-back summary (`SettingsSummary`/`ScanSettingsSummary`/`ModelSummary`)
+ hint copy — the same content in up to four places.

**D6 — Tests pin the drift.** `MainLayoutShellTests` asserts
"header / sidebar / action bar / footer / log"; `RouteSplitTests` pins chooser
gating; `WorkflowOrderTests` pins the sidebar sequence. Healthy — it means a
refactor will fail loudly — but these must be *rewritten to the new pins in
the same change*, never loosened.

### 2.3 Verdict

The **service layer already did the convergence**; only the presentation
layer forked. Refactor now: the template work is blocked on it, drift grows
with every feature round, and the test harness makes the risk manageable.
Doing nothing means `ui-design.md`'s shared layout would be implemented three
times, once per mode — the exact drift this design exists to end.

---

## 3. The contract: interface + shared base

Interface for **what the layout needs from a mode** (small, layout-shaped);
base class for **machinery modes should not re-implement** (run state, log).

### 3.1 `IOperationViewModel` — what a mode must provide

```csharp
namespace Synapic.Main.ViewModels.Operations;

/// One tile on the dashboard and one instance of the shared operation
/// layout (ui-design.md §2.2). Implemented by Tag, Dedup and Upscale.
public interface IOperationViewModel : INotifyPropertyChanged
{
    // ── Identity (dashboard + shell) ──
    string Key { get; }            // "tagging" | "dedup" | "upscale" — routing, persistence
    string Title { get; }          // "Tag" / "Dedup" / "Upscale"
    string Description { get; }    // dashboard card copy (ui-design.md §3)
    string HelpTopic { get; }      // F1 scope while this mode is on screen

    // ── Region B — Parameters ──
    string ParametersSummary { get; }   // read-back line shown above the run
    // The parameter controls themselves are reached through DataTemplates
    // keyed on the concrete VM — the layout never knows the controls.

    // ── Region C — Output / Report ──
    IRunStateViewModel Run { get; }     // shared base class, see 3.2
    bool IsRunEnabled { get; }          // the mode's gate (source ready, tag field…)
    string? RunDisabledReason { get; }  // gate copy, shown in the blocked region (D6)

    // ── Report ── (per-mode, template-selected)
    // Tag: results grid VM · Dedup: groups + apply · Upscale: output list
    object Report { get; }
}
```

**Region A (data source) is deliberately absent** — it is one shared
instance owned by the shell (`ui-design.md` D4), bound once by the template.
Modes consume it through `SourceReady`-style gates instead of owning a copy;
Dedup's duplicate source card and `PrefillDedupSource()` are deleted.

### 3.2 `RunStateViewModel` — the extracted copy-paste

Abstract base (replaces the block counted in D1):

```csharp
public abstract partial class RunStateViewModel : ViewModelBase, IRunStateViewModel
{
    // Owns, once: ProgressPercent, ProgressText, EtaText, CurrentFile,
    // IsRunning, IsPaused, IsIdle, LogLines, Start/Pause/Resume/Stop commands,
    // the CancellationTokenSource lifecycle, the UI-marshalled AppendLog
    // (2000-line cap), and the shared ProcessProgress mapper.

    protected abstract WorkflowDefinition Workflow { get; }
    protected abstract IWorkflowItemHandler CreateHandler(DatasourceSelection source);
    protected virtual bool SupportsPause => false;   // tagging: true

    // New, fixing UI-REVIEW carried-forward #1:
    public bool IsApplying { get; protected set; }   // busy state for post-run phases (dedup Apply)
}
```

Tagging keeps `ProcessingOrchestrator` semantics via an override (its run
path is orchestrator-based); upscale and dedup's scan drop straight onto the
base. Dedup's **Apply** gains `IsApplying` + indeterminate bar + enabled Stop
here, closing the standing audit finding.

### 3.3 Shell

```csharp
public sealed partial class ShellViewModel : ViewModelBase
{
    public IReadOnlyList<IOperationViewModel> Operations { get; } // Tag, Dedup, Upscale
    public SourceViewModel Source { get; }        // Region A — one instance (D4)
    public IOperationViewModel? Current { get; set; }  // null ⇒ dashboard (D1)
    public bool SettingsVisible { get; set; }           // the 5th dashboard tile
}
```

`null`-means-dashboard replaces `HomeRoute` + `IsDedupRoute` + `IsUpscaleRoute`
+ `CurrentStepIndex` entirely: no booleans to keep exclusive, no 6-way
switches. `RouteTitle`, `CreateSettingsDialog` and the `IsXxxActive` flags
collapse into `Current`.

### 3.4 The template view

`OperationLayout.axaml` — one UserControl, three regions, no mode knowledge:

```
┌─ Region A: DataSourceStrip  {Binding Source} ────────────────────┐
├─ Region B: Parameters  ContentControl {Binding Current.Parameters …}   ← DataTemplate per mode
├─ Region C: Output       header = RunStateBar {Binding Current.Run}      ← one shared control
│                         body   = Report      {Binding Current.Report}   ← DataTemplate per mode
└──────────────────────────────────────────────────────────────────┘
```

`RunStateBar.axaml` is written once (progress, ETA, Start/Pause/Stop,
busy states, gate reason line) and binds to `IRunStateViewModel`. The three
existing parameter panels (`DedupSettingsPanel`, `UpscaleSettingsPanel`, the
engine settings content) move into Region B unchanged — they are already
"one VM, two views"; after this they are one VM, **one** view.

---

## 4. Sequencing (strangler — shippable after each phase)

### Phase 1 — Extract (no visible change)
1. `RunStateViewModel` base; Step3, Upscale, Dedup-scan adopt it. Public
   member names preserved so existing bindings compile unchanged.
2. One `RunLog` helper; the four `AppendLog` copies collapse into it.
3. `IOperationViewModel` + thin adapters over the three existing step VMs
   (composition, not rewrite). Dedup keeps its source card for now.
   **Gate:** full `dotnet test` green, `dotnet build` 0 warnings, zero view
   diffs (`git diff --stat` shows only ViewModels/Services).

### Phase 2 — Template + dashboard
4. `OperationLayout.axaml` + `RunStateBar.axaml`; existing pages host inside
   the regions; shared `DataSourceStrip` replaces per-mode source reading
   (Dedup card and `PrefillDedupSource()` deleted here).
5. `DashboardView` (4 panels) replaces the start screen; `ShellViewModel.Current`
   defaults to `null` — **the app now starts on the dashboard**.
6. Tests: `RouteSplitTests`, `MainLayoutShellTests`, `WorkflowOrderTests` are
   rewritten in the same PR to pin `ui-design.md` §10 criteria 1–3 (dashboard
   first; `DataSource → Parameters → Output` on all three routes; shared
   source). `UiLayoutAuditTests` extends to the dashboard + stacked variant.
   **Gate:** ui-design.md §10 items 1, 2, 3, 5, 9.

### Phase 3 — Collapse
7. Settings dialogs retire; parameter panels live only in Region B.
8. Sidebar, action bar, Back/Next/StartOver, step DataTemplates deleted;
   `WizardViewModel` shrinks to (or is replaced by) `OperationShellViewModel`;
   `MainWindowViewModel` loses its route switches.
9. Dedup Apply gains `IsApplying` + Stop (closes `UI-REVIEW.md` finding #1).
   **Gate:** all of `ui-design.md` §10 (9 criteria), full suite, layout audit
   at 4 window sizes × 4 routes.

Do not reorder: Phase 1 is pure extraction (safe), Phase 2 changes behavior
under green tests, Phase 3 deletes only what Phase 2 proved dead.

---

## 5. Risk register

| Risk | Mitigation |
|---|---|
| Behavior drift while extracting run state | Phase 1 keeps public names; `StepUpscaleViewModelTests`, `PauseTests`, `WorkflowRunnerTests` run untouched against the base |
| Test rewrites hide regressions | New pins are written from `ui-design.md` §10 verbatim; old coverage is replaced 1:1, never deleted; layout audit keeps its overlap/bounds assertions |
| Dedup's bespoke scan fights the base | Scan adopts `RunStateViewModel` state only; `DedupScanHandler`/`DedupService` untouched |
| Big-bang temptation | Three phases, each a mergeable PR with a green gate; app runs after every phase |
| Duplicated state during transition | Adapters in Phase 1 are throwaway and deleted in Phase 2 — never a third copy |

## 6. Effort

| Phase | Approx. |
|---|---|
| 1 — Extract | 1–2 days |
| 2 — Template + dashboard | 3–5 days |
| 3 — Collapse + audit fix | 2–3 days |

## 7. Traceability to `ui-design.md`

| Design requirement | Delivered by |
|---|---|
| §2.1 dashboard, D1/D2 start screen | Phase 2.5 |
| §2.2 shared three-region layout | Phase 2.4 (`OperationLayout`) |
| §4.1 shared data source, D4 | Phase 2.4 (strip) + D2 fix (delete dedup source card) |
| §4.3 busy/stop/empty states; `UI-REVIEW.md` #1 | `RunStateViewModel.IsApplying`, Phase 3.9 |
| §6.2 routes `dashboard ⇄ mode ⇄ settings` | `ShellViewModel.Current` (Phase 2), Phase 3.8 |
| §10 acceptance criteria 1–7 | Phase gates 2 and 3 |
