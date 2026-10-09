---
phase: 02-add-operation-template-and-dashboard
plan: 03
subsystem: ui
tags: [avalonia, xaml, dashboard, shell-navigation, strangler]

# Dependency graph
requires:
  - phase: 01-extract-run-state-and-operation-contract
    provides: IOperationViewModel + the three adapters (Tag/Dedup/Upscale)
  - phase: 02-add-operation-template-and-dashboard
    provides: OperationLayout hosted on all three routes (plan 02-01), shared source in Region A (plan 02-02)
provides:
  - DashboardView — the app's starting screen with exactly four panels (Settings, Tag, Dedup, Upscale)
  - ShellViewModel.Current (null = dashboard) kept in sync by the route machine
  - HomeRoute now renders the dashboard instead of the numbered chooser
affects: [02-04 dashboard/region pins + layout audit, 03 collapsing the sidebar and routing Settings]

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "One writer for navigation state: every route command calls Shell.SetCurrent, and Shell.Open/Home go through those same commands"
    - "New screen reuses the audited geometry it replaces (settings content full width, three equal operation panels) instead of inventing a layout the audit has never walked"

key-files:
  created:
    - src/Synapic.Main/Views/Dashboard/DashboardView.axaml
    - src/Synapic.Main/Views/Dashboard/DashboardView.axaml.cs
    - src/Synapic.Main/ViewModels/Operations/ShellViewModel.cs
  modified:
    - src/Synapic.Main/ViewModels/MainWindowViewModel.cs
    - src/Synapic.Main/Views/MainLayout.axaml
    - src/Synapic.Main/Views/MainLayout.axaml.cs

key-decisions:
  - "The dashboard's Settings panel hosts the settings content that exists today (the shared source panel and the model picker) — §5's sectioned settings view is Phase 3, and the pinned facts require the engine picker and the one Connect form to remain visible on the start screen"
  - "Dashboard panels are always enabled (ui-design D6): entering a mode is free and only a run is gated, which supersedes CanStartRoute card gating and therefore invalidates exactly one old pin"
  - "The operation panels reuse the routeCard class and the route commands, so the existing route-card pins stay true and the invalidated set stays at one fact"
  - "ShellViewModel.Current is written only by Shell.SetCurrent, which the route commands call — one writer, no second navigation model during the strangler window"

patterns-established:
  - "Dashboard panel = card + route command + live status line read from shared state (never invented data)"

requirements-completed: []

# Metrics
duration: 55min
completed: 2026-10-09
---

# Phase 2 Plan 03: DashboardView + ShellViewModel.Current Summary

**The app cold-starts on a four-panel dashboard (Settings, Tag, Dedup, Upscale) with `Shell.Current` null, every panel opening its operation through the existing route commands, and the numbered chooser gone from the home content**

## Performance

- **Duration:** 55 min (including the Region A host-binding fix that 02-02 depended on)
- **Tasks:** 2
- **Files created:** 3
- **Files modified:** 3

## Accomplishments

- [DashboardView.axaml](src/Synapic.Main/Views/Dashboard/DashboardView.axaml) renders exactly four panels — `SettingsPanel`, `TagPanel`, `DedupPanel`, `UpscalePanel` (all `Classes="… dashboardPanel"`), bound to the shell's own view model. The Settings panel carries the settings content that exists today (the one source panel and the model picker) plus the inference server's live state; the three operation panels each open their mode with the same command the sidebar uses and show the shared record count, or the neutral `no source configured yet` hint.
- [ShellViewModel.cs](src/Synapic.Main/ViewModels/Operations/ShellViewModel.cs) introduces `Current` (null = dashboard) over the three Phase 1 adapters, with `Open(key)`/`Home()` routing through the existing commands and `SetCurrent(key)` as the single writer.
- [MainWindowViewModel.cs](src/Synapic.Main/ViewModels/MainWindowViewModel.cs) constructs the three adapters once, owns `Shell`, and calls `Shell.SetCurrent("tag"/"dedup"/"upscale")` from the three route commands and `SetCurrent(null)` from `GoHome` — so the dashboard, the sidebar, F1/help and the legacy route booleans can never disagree about which operation is open.
- [MainLayout.axaml](src/Synapic.Main/Views/MainLayout.axaml) replaced the whole home chooser (the numbered setup card and the three route cards) with `<dash:DashboardView />`; `routeCard` no longer appears in the shell file.

## Task Commits

1. **Tasks 1 + 2: ShellViewModel + dashboard + home replacement** — `feat(02-03)` commit

**Plan metadata:** `docs(02-02,02-03): complete shared-source and dashboard plans` (this commit)

## Verification

| Check | Result |
|-------|--------|
| `dotnet build Synapic.Net.sln -c Release --no-incremental` | exit 0 — 0 Warning(s), 0 Error(s) |
| `dotnet test Synapic.Net.sln -c Release` | Shared 6/6 ✓, Integration 5/5 ✓, **Main 430 passed / 1 failed / 0 skipped** — the one failure is the predicted, plan-invalidated pin (below) |
| Cold start (`Shell.Current == null`, `IsHomeVisible`, four panels) | verified by a temporary headless probe (deleted before commit): exactly four `dashboardPanel` elements named `SettingsPanel`/`TagPanel`/`DedupPanel`/`UpscalePanel`, all effectively visible at boot |
| Each panel opens its operation / `Current` tracks it | same probe: each route card's command sets `Route` **and** `Shell.Current` to the matching adapter instance (`Assert.Same`); `Shell.Home()` clears it; `Shell.Open("nope")` is ignored |
| §10 criterion 3 (shared source) | same probe: on the tag, dedup and upscale routes Region A's strip has the same `Wizard.Step1` DataContext and renders `Step1.CountText`; dedup's `FolderPath`/`IsLocal` read that same source |
| Layout audit (`UiLayoutAuditTests`, home + tagging steps 1–4 + dedup + upscale + the three dialogs, 900×600 → 1600×900) | green (part of the 430 passing) — the dashboard renders and clips nothing at any audited size |
| `grep -c "routeCard" src/Synapic.Main/Views/MainLayout.axaml` | 0 — the chooser cards are gone from the shell |
| `git diff --name-only -- src/Synapic.Main/Services/` · `git status --porcelain tests/` | both empty |
| Colour literals in the new views | 0 hex colours (theme brushes only) |

### The one invalidated pin (expected, recorded for 02-04)

`RouteSplitTests.Start_screen_gates_the_workflow_cards_on_a_usable_source` fails on `Assert.All(cards, card => Assert.False(card.IsEnabled))`: it pins the **old** `CanStartRoute` gating, which ui-design D6 explicitly replaces ("entering a mode is free; only actions are gated") and which plan 02-04 Task 1 maps to a dashboard fact. No other test fails: 441 of 442 assertions across the solution pass, including every shared-source, route-visibility, settings-dialog and layout-audit fact.

## Decisions Made

- **Settings is a panel with content, not a route.** ui-design §5's sectioned settings view and a `SettingsRoute` are Phase 3 work (they would also add a route every pinned test would have to learn). Until then the dashboard's Settings panel holds the settings content the app actually has — the source panel and the model picker — which keeps the two pinned facts that require them to be visible on the start screen (`Assert.Single(EngineModelPicker)`, `Assert.Single(ConnectCommand)`) true rather than invalidated.
- **Panels are always enabled.** D6 is the design's authority and this plan's own criterion is "each opening its view". The consequence — one old pin — is listed above and was anticipated by the plan ("only the start-screen facts that this plan invalidates may fail").
- **The operation panels keep the `routeCard` class and the route commands.** This preserves `RouteSplitTests.Start_screen_cards_and_route_visibility…`, `UpscaleRouteTests.Third_card_and_upscaling_tab…` and the two-buttons-per-route counts in `MainLayoutShellTests`, so the invalidated set stays at exactly one fact instead of three or four.
- **`Shell.Current` has one writer.** `Open`/`Home` delegate to the route commands, and those call `SetCurrent`; nothing else writes `Current`. That is what makes it a single source of truth rather than a parallel model that can drift.

## Deviations from Plan

1. **The dashboard's wide form is not the 2×2 grid.** The Settings panel is full width (it hosts the source form until Phase 3) and the three operation panels sit in the equal-columns row the chooser used — a geometry the layout audit already walks at 900×600 … 1600×900. Avalonia has no XAML breakpoint, so the full 2×2 arrangement and its switch point are 02-04's audit extension, the same standoff 02-01 recorded for the operation regions.
2. **`MainLayout.axaml.cs` was touched** (not in `files_modified`) to hand the shared source instance to `OperationLayout` — see 02-02's deviation 1 for why a binding on that element cannot resolve `Wizard.Step1`.
3. **`GoHome`'s log line and the home copy now say "dashboard"** instead of "start screen"/chooser; the design renames the screen, and no pin depended on the old wording.
4. **`MainWindowViewModel` gained `SourceCountText`** so the panels' status line can say `no source configured yet` instead of showing an empty count; it is derived from `Step1.CountText` and re-raised on change (no new state).

## Issues Encountered

- Diagnosing why `SharedSource="{Binding Wizard.Step1}"` silently produced null took the bulk of the wave's time: an Avalonia binding written on an element resolves against **that element's** DataContext, which `OperationLayout` sets to the mode's step view model, so the path `Wizard.Step1` can never resolve there. The fix (host hand-over) is recorded in 02-02's deviations and pinned by the temporary probe (attribute → `null`; hand-over → same instance on all three routes).

## Next Phase Readiness

- 02-04 has everything it needs to pin §10 criteria 1–3: `dashboardPanel` names and classes on the four panels (panel titles are Settings/Tagging/Deduplication/Upscaling — the panel *names* are the four design names), the `dataSourceRegion`/`parametersRegion`/`outputRegion` markers, and a `DataSourceStrip` whose DataContext is the shared source on every route.
- The dashboard is also the rewrite target for the `Start_screen_*` pins; the mapping table in 02-04-SUMMARY.md should record `Start_screen_gates_the_workflow_cards_on_a_usable_source` → a D6 dashboard fact.

---
*Phase: 02-add-operation-template-and-dashboard*
*Completed: 2026-10-09*

## Self-Check: PASSED
