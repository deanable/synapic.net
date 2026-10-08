# Phase 2: Add operation template and dashboard - Context

**Gathered:** 2026-10-08
**Status:** Ready for planning

<domain>
## Phase Boundary

Build the two shared surfaces the design revolves around: the **operation template**
(`OperationLayout.axaml` with three regions — Data source → Parameters → Output/Report) and the
**dashboard** (`DashboardView` with four panels — Settings, Tag, Dedup, Upscale) that the app
cold-starts on. All three modes render through the template in this phase; the legacy sidebar,
Back/Next and settings dialogs are still present but no longer the primary path — they are removed
in Phase 3. Source: `docs/ui-design.md` §2 (layout), §3 (regions), §4 (modes), §6 (navigation),
§10 (acceptance); `docs/ui-refactor-plan.md` §4 Phase 2.

</domain>

<decisions>
## Implementation Decisions

### The template
- **D-01:** `Views/Operation/OperationLayout.axaml` has exactly three regions and zero mode
  knowledge: Region A (Data source), Region B (Parameters — per-mode `DataTemplate`), Region C
  (Output = shared `RunStateBar` + per-mode Report `DataTemplate`). Per-mode templates live beside
  it (`Views/Operations/{Tag,Dedup,Upscale}/…` or equivalent — layout only, view models untouched
  beyond wiring).
- **D-02:** Region A is ONE shared `DataSourceStrip` bound to the single shell-owned source
  instance (`ui-design.md` D4). Configuring the source in Tag shows identically in Dedup and
  Upscale. Dedup's duplicate source card and `PrefillDedupSource()` are deleted in this phase.
- **D-03:** `ShellViewModel.Current == null` means dashboard; non-null means "operation open".
  The app cold-starts on `DashboardView` (four panels: Settings, Tag, Dedup, Upscale). Existing
  step/wizard mechanics keep working behind the template until Phase 3 collapses them.
- **D-04:** The existing parameter panels (`DedupSettingsPanel`, `UpscaleSettingsPanel`, tagging
  engine panel) move into Region B **unchanged** — they already bind one VM; after this phase it
  is one VM, one view (dialogs still exist until Phase 3).
- **D-05:** Shared `RunStateBar.axaml` in Region C renders the `RunStateViewModel` state extracted
  in Phase 1 (progress, ETA, current file, log, primary action).

### Tests
- **D-06:** Rewrite the three navigation-pinned test files (`RouteSplitTests`,
  `MainLayoutShellTests`, `WorkflowOrderTests`) to pin the NEW invariants from
  `docs/ui-design.md` §10 criteria 1–3 (and 5, 9 where they apply): dashboard-first cold start,
  three-region order `DataSource → Parameters → Output` on all three modes, single shared source.
  Rewriting pins is expected; **removing coverage is not** — each old assertion maps to a new one.
- **D-07:** Extend `UiLayoutAuditTests` to cover the dashboard and the operation template in its
  stacked (narrow) variant across the four audited window sizes (900×600, 1024×700, 1280×800,
  1600×900). Gate: full suite green, build 0 warnings/0 errors.

### Claude's Discretion
- DataTemplate organization (ResourceDictionary vs. per-view), dashboard panel copy/layout details
  beyond the four panels, how `Current` coexists with the legacy route booleans during the
  strangler window (remove booleans is Phase 3).

</decisions>

<canonical_refs>
## Canonical References

**Downstream agents MUST read these before planning or implementing.**

### Design contract (the authority for this phase)
- `docs/ui-design.md` §2 — overall layout: dashboard → operation, panel anatomy
- `docs/ui-design.md` §3 — the three regions A/B/C, one-accent-per-region, 2/4/8/12/16 spacing
- `docs/ui-design.md` §4 — per-mode Parameters + Report content (Tag/Dedup/Upscale)
- `docs/ui-design.md` §6 — navigation model: dashboard, open operation, back to dashboard
- `docs/ui-design.md` §10 — acceptance criteria 1, 2, 3, 5, 9 are this phase's pins
- `docs/ui-refactor-plan.md` §3 — region contract vs. `IOperationViewModel` (Phase 1 output)
- `docs/ui-refactor-plan.md` §4 Phase 2 — steps, risks, gate

### Mock-ups
- `docs/mock-up/UI Flow - Dash.svg` — dashboard
- `docs/mock-up/UI Flow - Operation.svg` — operation layout

### Code (Phase 1 output and wiring points)
- `src/Synapic.Main/ViewModels/Operations/` — `IOperationViewModel`, `RunStateViewModel`,
  `ShellViewModel` (created in Phase 1 / this phase as needed)
- `src/Synapic.Main/Views/MainLayout.axaml` — shell to re-point at the template
- `src/Synapic.Main/ViewModels/WizardViewModel.cs` and
  `src/Synapic.Main/ViewModels/MainWindowViewModel.cs` — consumers wiring `Current`
- Tests to rewrite: `RouteSplitTests`, `MainLayoutShellTests`, `WorkflowOrderTests`,
  `UiLayoutAuditTests` (locate under test project tree)

</canonical_refs>

<code_context>
## Existing Code Insights

### Reusable Assets
- `IOperationViewModel` + `RunStateViewModel` from Phase 1 — the template binds to these, not to
  concrete modes
- Existing settings panels — drop into Region B unchanged (D-04)

### Established Patterns
- Avalonia XAML with theme resource brushes (`SynapicAccentBrush` #2563EB, `SynapicSuccessBrush`
  #16A34A, `SynapicDangerBrush` #DC2626, `SynapicWarningBrush` #EA580C), `Classes="card"` styling,
  Consolas for log text — the template must reuse these, not introduce a second visual language
- `UiLayoutAuditTests` is the headless layout proof — extend, don't bypass

### Integration Points
- `ShellViewModel.Current` becomes the single navigation source; `MainWindowViewModel` route
  booleans still exist (Phase 3 removes them) but must not fork the new path

</code_context>

<deferred>
## Deferred Ideas

- Deleting sidebar, Back/Next/StartOver, chooser cards, step DataTemplates, settings dialogs —
  Phase 3
- Slimming `WizardViewModel` into `OperationShellViewModel` — Phase 3
- Dedup Apply busy state (`IsApplying` + Stop) — Phase 3 (`UI-REVIEW.md` #1)

</deferred>

---

*Phase: 02-add-operation-template-and-dashboard*
*Context gathered: 2026-10-08*
