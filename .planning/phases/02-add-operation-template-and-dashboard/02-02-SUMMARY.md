---
phase: 02-add-operation-template-and-dashboard
plan: 02
subsystem: ui
tags: [avalonia, xaml, data-source, dedup, strangler]

# Dependency graph
requires:
  - phase: 02-add-operation-template-and-dashboard
    provides: OperationLayout with the dataSourceRegion slot (plan 02-01)
provides:
  - DataSourceStrip — the one Region A source read-back (profile, scope, record count, Resync)
  - StepDedupViewModel reads/writes the shared Step1 source (no second source state)
  - PrefillDedupSource and dedup's duplicate source card deleted
affects: [02-04 region-order + shared-source pins, 03 retiring the dialogs and the legacy pages]

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "One source of truth through delegation: dedup's source properties read and write Step1's own state, with a private fallback only for hosts that have no Step1 (isolated unit tests)"
    - "The template receives the shared source from its host; Region A renders the instance, never a copy"

key-files:
  created:
    - src/Synapic.Main/Views/Operation/DataSourceStrip.axaml
    - src/Synapic.Main/Views/Operation/DataSourceStrip.axaml.cs
  modified:
    - src/Synapic.Main/Views/Operation/OperationLayout.axaml
    - src/Synapic.Main/Views/Operation/OperationLayout.axaml.cs
    - src/Synapic.Main/Views/MainLayout.axaml
    - src/Synapic.Main/Views/MainLayout.axaml.cs
    - src/Synapic.Main/ViewModels/WizardViewModel.cs
    - src/Synapic.Main/ViewModels/Steps/StepDedupViewModel.cs
    - src/Synapic.Main/Views/Wizard/StepDedup.axaml
    - src/Synapic.Main/Views/Wizard/StepDedup.axaml.cs

key-decisions:
  - "Dedup's DatasourceType/FolderPath delegate to Step 1 (read-through and write-through) instead of being copied at route entry — PrefillDedupSource existed only to keep the copy in sync, and deleting it is what makes the shared source real"
  - "The private fields behind those properties remain as the standalone fallback: the dedup unit tests construct StepDedupViewModel with no Step1 and must keep their behavior untouched"
  - "The strip is a read-back plus Resync (the design's own count action, §4.1); configuring the source happens once on the dashboard's Settings panel, because a second Connect/Browse affordance in Region A would duplicate the one the pinned Connect-form test asserts is unique"

patterns-established:
  - "Region A = ContentControl + DataTemplate + the host-handed shared instance; zero mode knowledge in the layout"

requirements-completed: []

# Metrics
duration: 22min
completed: 2026-10-09
---

# Phase 2 Plan 02: Shared DataSourceStrip + dedup's parallel source path deleted Summary

**Region A now renders one shared strip over the shell's single source instance, and dedup reads that same state — `PrefillDedupSource`, its two call sites and the duplicate source card are gone with the suite green and no test file touched**

## Performance

- **Duration:** 22 min
- **Tasks:** 2
- **Files created:** 2
- **Files modified:** 8

## Accomplishments

- [DataSourceStrip.axaml](src/Synapic.Main/Views/Operation/DataSourceStrip.axaml) is the one Region A implementation for all modes: the existing `SourceStatusStrip` profile (the two lights and their sentences), the Daminion scope line, the shared record count and the design's Resync button (`CountCommand`). It holds no state and stores no copy — every binding is direct onto the shared `Step1DatasourceViewModel`.
- [OperationLayout.axaml:24](src/Synapic.Main/Views/Operation/OperationLayout.axaml#L24) mounts it in `DataSourceHost` through a `DataTemplate` for the shared source, and `OperationLayout` gained a `SharedSource` styled property so the host hands the instance in. [MainLayout.axaml](src/Synapic.Main/Views/MainLayout.axaml) supplies it where the template is hosted.
- [StepDedupViewModel.cs](src/Synapic.Main/ViewModels/Steps/StepDedupViewModel.cs) now reads the shared source directly: `DatasourceType` and `FolderPath` are read-through/write-through properties over Step 1, `IsLocal`/`IsDaminion`/`CanScan()`/`Apply`'s gate all evaluate against it, and a named `OnSharedSourceChanged` handler re-raises every delegated member (and the scope summary) so a source changed anywhere is the source the scan would use.
- [WizardViewModel.cs](src/Synapic.Main/ViewModels/WizardViewModel.cs) lost `PrefillDedupSource()` and both call sites; the dedup route's Next and the dedup tab now simply enter the step — the scan reads Step 1 itself.
- [StepDedup.axaml](src/Synapic.Main/Views/Wizard/StepDedup.axaml) lost the "Source to scan" card (radios, folder box, Browse, scope read-back); its code-behind lost the now-dead folder picker. The page keeps the scan, the group review and Apply unchanged.

## Task Commits

1. **Tasks 1 + 2: strip + shared source + dedup deletion** — `7f97fbd1` (feat)

**Plan metadata:** `docs(02-02,02-03): complete shared-source and dashboard plans` (this commit)

## Verification

| Check | Result |
|-------|--------|
| `dotnet build Synapic.Net.sln --nologo -v minimal` | exit 0 — 0 Warning(s), 0 Error(s) |
| `dotnet test tests/Synapic.Main.Tests -c Release` (plan gate) | exit 0 — **431 passed / 0 failed / 0 skipped**, the same count as before the plan: the dedup route pins (`Dedup.FolderPath` carries Step 1's folder, `IsDaminion` mirrors the catalog source) still pass, now through delegation instead of a copy |
| `grep -rn "PrefillDedupSource" src/` | no matches |
| `grep -n "routeCard\|Source to scan" src/Synapic.Main/Views/Wizard/StepDedup.axaml` | absent — no duplicate source card |
| `grep -c "Path\|_source" DataSourceStrip.axaml.cs` | no stored copies; code-behind is `InitializeComponent` only |
| `git diff --name-only -- src/Synapic.Main/Services/` | empty |
| `git status --porcelain tests/` | 0 — no test file modified (D-06) |
| Region A strip on tag/dedup/upscale routes | verified by a temporary headless probe (deleted before commit): on each route the strip's DataContext is the same `Wizard.Step1` instance and its rendered count equals `Step1.CountText` |

## Decisions Made

- **Delegation, not synchronization.** Dedup keeps its property surface (so nothing else changes) but the properties read and write Step 1 when a Step 1 exists. That is what makes the deletion of `PrefillDedupSource` safe: there is nothing left to prefill, and a source changed *after* entering the route is still the source the scan reads — the old copy could go stale exactly that way.
- **The fallback fields stay.** `StepDedupViewModelTests` constructs the step without a Step 1 (`SourceRadios_RoundTrip…`, `ScanCommand_RequiresAFolderPath…`, `Rescan_DetachesThePreviousPreviews…`, `ServerHashPicker_IsDaminionOnly…`); the plan requires those tests to pass unmodified, so the private fields remain the standalone behavior and the tests pin it.
- **Region A shows the design's count action, not a second editor.** §4.1 lists the count with Resync; the type selector, Browse and Connect are the dashboard's job until Phase 3 moves the source panel into Region A. Adding a Connect button here would have broken `RouteSplitTests.Daminion_connect_form_lives_on_the_start_screen_only` (`Assert.Single` over `ConnectCommand`), which this plan may not edit.

## Deviations from Plan

1. **`OperationLayout.axaml.cs` and `MainLayout.axaml.cs` were touched** (not in the plan's `files_modified`). The plan says "mount it in OperationLayout's `DataSourceHost`", and the strip needs the shell's source instance to bind; a `{Binding Wizard.Step1}` attribute written on the `OperationLayout` element **resolves against that element's own DataContext** (the mode's step view model) and silently yields null. The layout therefore exposes `SharedSource` and MainLayout's existing `DataContextChanged` handler hands `vm.Wizard.Step1` over — the same imperative-wiring pattern that file already uses for the log. Verified both ways: with the attribute the probe read `SharedSource=null`; with the hand-over the strip is the same instance on all three routes. The hand-over itself landed in the 02-03 commit (its message says so): the 02-02 commit alone leaves Region A empty rather than wrong, which no test at that point observed — 431/431 either way.
2. **The strip is read-only + Resync** (see Decisions) — the plan's "edit/choose affordance" is satisfied by the Resync button and the profile's stated usability, with the edit form staying on the dashboard.
3. **The XAML mount uses an element-name binding inside the template** (`#OperationLayoutRoot.SharedSource`): `$parent[op:OperationLayout]` cannot be resolved at runtime (Avalonia's runtime type resolver does not know the XML prefix), which the first build+test pass proved.

## Issues Encountered

- The first mount attempt (`$parent[op:OperationLayout].SharedSource`) failed at runtime with 29 test failures ("Unable to resolve type op:OperationLayout"); replaced with the element-name binding, then the suite was green. The host-value investigation above was the second issue and is recorded under Deviations.

## Next Phase Readiness

- `routeCard`-style pins aside, the shared-source fact is verifiable: a source configured on the dashboard is the source Dedup and Upscale read, and Region A shows it identically (02-04 pins this for §10 criterion 3).
- The dedup page no longer owns any source state, so Phase 3 can delete the legacy pages without leaving a second source behind.

---
*Phase: 02-add-operation-template-and-dashboard*
*Completed: 2026-10-09*

## Self-Check: PASSED
