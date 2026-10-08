# Phase 3: Collapse navigation and retire settings dialogs - Context

**Gathered:** 2026-10-08
**Status:** Ready for planning

<domain>
## Phase Boundary

Remove the legacy navigation and settings chrome the strangler phases left behind, so the design in
`docs/ui-design.md` is the ONLY path: no numbered sidebar, no Back/Next/StartOver action bar, no
start-screen chooser, no operation settings dialogs (parameters live inline in Region B), no
route-boolean switches in the view models. Also close the open `UI-REVIEW.md` carried-forward
finding #1: dedup Apply gets a busy state. Gate = all nine `ui-design.md` §10 acceptance criteria
plus build/test/audit. Source: `docs/ui-design.md` §5, §6, §9, §10; `docs/ui-refactor-plan.md` §4
Phase 3.

</domain>

<decisions>
## Implementation Decisions

### Deletions (the phase's main verb)
- **D-01:** From `MainLayout.axaml`: numbered sidebar, Back/Next/StartOver action bar, start-screen
  chooser cards, step `DataTemplate`s, per-route sidebar visibility rules. `HomeRoute`,
  `IsDedupRoute`, `IsUpscaleRoute` and the `Enter*Route()` mutual-exclusion methods are deleted
  from `MainWindowViewModel` and `WizardViewModel` — `ShellViewModel.Current` is the sole
  navigation state (`null` = dashboard).
- **D-02:** Retire `EngineSettingsDialog`, `DedupSettingsDialog`, `UpscaleSettingsDialog`
  (`src/Synapic.Main/Views/Settings/`). Every operation parameter is reachable inline in
  Region B (panels already moved there in Phase 2). No settings dialog class remains in the tree.

### View-model consolidation
- **D-03:** `WizardViewModel` is slimmed into / replaced by `OperationShellViewModel`;
  `MainWindowViewModel` loses its route switch statements — `RouteTitle` and settings-dialog
  creation derive from `ShellViewModel.Current` (dialogs being gone, "CreateSettingsDialog"
  collapses to opening the operation with Region B focused).

### The open audit finding
- **D-04:** Dedup `Apply` gains `IsApplying` + an indeterminate busy indicator for the whole
  operation duration and keeps `Stop` enabled during it — closing `UI-REVIEW.md`
  carried-forward finding #1. This is observable behavior, not cosmetic.

### Gate (objective)
- **D-05:** All nine acceptance criteria in `docs/ui-design.md` §10 pass item-by-item;
  `UiLayoutAuditTests` green at 4 window sizes × 4 routes (dashboard + 3 modes, stacked variant
  included); `dotnet build` 0 warnings/0 errors; full test suite green with no assertions
  removed except the pinned-navigation rewrites already completed in Phase 2.

### Claude's Discretion
- Order of deletions, whether `WizardViewModel` is renamed vs. replaced, how removed tests are
  accounted for (they must map to template pins — zero net coverage loss), interim commits.

</decisions>

<canonical_refs>
## Canonical References

**Downstream agents MUST read these before planning or implementing.**

### Design contract (the authority for this phase)
- `docs/ui-design.md` §5 — what dies: dialogs, sidebar, Back/Next, chooser
- `docs/ui-design.md` §6 — navigation after collapse: dashboard ⇄ operation via `Current`
- `docs/ui-design.md` §9 — migration/strangler end-state rules
- `docs/ui-design.md` §10 — ALL nine acceptance criteria (this phase's gate)
- `docs/ui-refactor-plan.md` §4 Phase 3 — steps, risks, gate; §2 D3/D4 (navigation + shell drift
  evidence)

### The open finding
- `UI-REVIEW.md` — carried-forward finding #1 (dedup Apply has no busy state) must be closed

### Code (Phase 2 output + deletion targets)
- `src/Synapic.Main/Views/MainLayout.axaml` — sidebar/action-bar/chooser/step templates die here
- `src/Synapic.Main/ViewModels/MainWindowViewModel.cs`, `src/Synapic.Main/ViewModels/WizardViewModel.cs`
  — route switches and booleans die here
- `src/Synapic.Main/Views/Settings/` — the three dialogs retire
- `src/Synapic.Main/Views/Operation/` (or `Views/Operations/`) — template + Region B panels (Phase 2)

</canonical_refs>

<code_context>
## Existing Code Insights

### Reusable Assets
- `ShellViewModel.Current` + `IOperationViewModel` + `RunStateViewModel` (Phase 1/2) — the
  replacement for everything deleted
- Region B panels — already one-VM-one-view; deletion of dialogs does not touch bindings

### Established Patterns
- Theme brushes and `Classes="card"` styling are the visual language; the deleted chrome must not
  leave orphaned styles behind (dead `Styles`/`ControlTheme` entries get removed too)
- `MainLayoutShellTests` / `RouteSplitTests` pins written in Phase 2 define what survives; any
  test asserting deleted widgets must have an equivalent template pin first

### Integration Points
- Dedup Apply flows through `StepDedupViewModel` → services; the busy state must wrap the real
  `ApplyAsync` duration (cancel included), not a timer

</code_context>

<deferred>
## Deferred Ideas

- Visual polish beyond the design doc, new modes, service-layer changes — out of scope
- `docs/ui-refactor-plan.md` §6 effort note: Phase 3 includes test-rewrite debt accumulated by
  deletions — budget it here, not as a follow-up

</deferred>

---

*Phase: 03-collapse-navigation-and-retire-settings-dialogs*
*Context gathered: 2026-10-08*
