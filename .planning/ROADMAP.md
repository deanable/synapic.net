# Roadmap: Synapic.NET

## Milestones

- 🚧 **v1.0 UI Refactor** — Phases 1–3 (Phase 3 executed; §10 criterion 6 open — not shipped; to be closed by v1.2)
- ✅ **v1.1 Windows Indexer Service** — Phases 4–8 (shipped 2026-10-23, 31/31 requirements)
- 🚧 **v1.2 Release Integrity** — Phases 9–12 (planning: MSI channel, live Daminion, v1.0 closure)

## Overview

v1.0 reorganizes the desktop UI around the approved design in `docs/ui-design.md`: the app
starts on a 4-panel dashboard (Settings, Tag, Dedup, Upscale) and every mode renders one shared
operation layout (Data source → Parameters → Output/Report). It implements
`docs/ui-refactor-plan.md` in three strangler phases — extract the duplicated run machinery
behind an `IOperationViewModel` contract, build the shared template + dashboard, then collapse
the legacy navigation. Phases 1 and 2 are complete; Phase 3 is executed but its gate left two
criteria open, so the milestone has not shipped.

v1.2 closes what v1.1 shipped without proving, and finally ships v1.0. It makes the MSI a supported
channel (rebuilt with the indexer payload, install/upgrade/uninstall verified), proves the Daminion
path against a real catalog instead of a fake seam, restates §10 criterion 6 as a claim that can
actually be checked and proves it, and then walks the outstanding v1.0 UAT scenarios against an
installed build so the UI refactor can be called done. No new product capability.

v1.1 added the unattended half of the product: a Windows Service that consumes the same engine
without a desktop. It lifted the engine out of the Avalonia project into `Synapic.Core` (phase 4),
built the service host and configuration (5), work detection and a durable job queue (6), the
tagging pipeline with write-back and resilience (7), and packaging plus an unattended acceptance
run (8). All five phases shipped and were archived to `milestones/v1.1-ROADMAP.md` on 2026-10-23.

**Phase Numbering:**

- Integer phases (1, 2, 3): Planned milestone work
- Decimal phases (2.1, 2.2): Urgent insertions (marked with INSERTED)

## Phases

<details>
<summary>✅ v1.1 Windows Indexer Service (Phases 4–8) — SHIPPED 2026-10-23</summary>

- [x] Phase 4: Extract headless engine core (4/4 plans) — completed 2026-10-22
- [x] Phase 5: Indexer service host and configuration (4/4 plans) — completed 2026-10-22
- [x] Phase 6: Work detection and durable job queue (4/4 plans) — completed 2026-10-22
- [x] Phase 7: Tagging pipeline and write-back (4/4 plans) — completed 2026-10-22
- [x] Phase 8: Packaging, install and unattended acceptance (3/3 plans) — completed 2026-10-23

</details>

Full detail: [milestones/v1.1-ROADMAP.md](milestones/v1.1-ROADMAP.md) ·
requirements: [milestones/v1.1-REQUIREMENTS.md](milestones/v1.1-REQUIREMENTS.md) ·
entry: [MILESTONES.md](MILESTONES.md)

**v1.2 Release Integrity (Planning)**

- [x] **Phase 9: MSI and bundle as a supported channel** - Rebuilt with the indexer payload; silent install, in-place reinstall, major upgrade, uninstall and service parity with Inno verified (5/5 plans, completed 2026-10-23)
- [ ] **Phase 10: Live Daminion round trip** - Tag, check in, read back and survive a real outage against the provided server
- [ ] **Phase 11: Close the UI-refactor gate** - Restate §10 criterion 6 as a finite claim, prove it with an inventory-backed test set, re-run 03-05
- [ ] **Phase 12: UI acceptance and release** - Execute the six pending UAT scenarios on the installed build, then archive and tag v1.0

### v1.0 remaining phases

- [x] **Phase 1: Extract run state and operation contract** - Pull the copy-pasted run machinery into a shared base; no visible change (4/4 plans — gate sweep green: all five success criteria ✓)
- [x] **Phase 2: Add operation template and dashboard** - One shared three-region layout + 4-panel dashboard the app boots to (5/5 plans — gate sweep green: all five success criteria ✓)
- [ ] **Phase 3: Collapse navigation and retire settings dialogs** - Delete the legacy wizard chrome; inline parameters; busy-state fix (5/5 plans executed; §10 criterion 6 open — criterion 8 closed by the 2026-10-13 addendum; closure is Phase 11)

**Settings view (§5)** — delivered during Phase 3, adopted by plan `03-04`, verified by `03-05`; two
§5 rows recorded ✗ (a health-details surface, the wide section-nav layout). Detail below.

## 🚧 v1.2 Release Integrity (Planning)

**Milestone Goal:** Close the three gaps v1.1 left unproven — the MSI channel, the live Daminion path, and
v1.0's own gate — and ship v1.0. Source: `docs/windows-indexer.md`, `docs/ui-design.md` §10, `03-UAT.md`.
Requirements: `.planning/REQUIREMENTS.md`. Scope fence: **no new product capability**.

### Phase 9: MSI and bundle as a supported channel

**Goal:** Make the MSI channel real. Rebuild `Synapic-win-x64.msi` and its Burn bundle from a fresh `win-x64`
publish that includes `Synapic.Indexer.exe`, verify silent install, upgrade and uninstall plus service parity
with the Inno path on this machine, and document the channel's role so nobody can install a package older
than the UI again — the root cause `03-UAT.md` recorded for "no dashboard on launch".

**Requirements**: MSI-01, MSI-02, MSI-03, MSI-04, MSI-05
**Depends on**: Phase 8 (payload and installer integration)
**Success Criteria** (what must be TRUE):

  1. `build/package-windows-msi.ps1` exits 0 against a clean payload and the MSI contains `Synapic.Indexer.exe`, `Synapic.Core.dll` and `synapic-inference.exe`; deleting one fails the build by name
  2. `msiexec /i … /qn` exits 0, the installed app launches on the dashboard, and its shortcut resolves to the installed executable
  3. The installed service is `NT AUTHORITY\LocalService`, auto-start, recovery restart/60000 ×3 — done by the package or by the one documented command the acceptance runs
  4. A second install at the same product version replaces in place (one Programs entry, one service); uninstall gives `sc query` 1060 and leaves `%ProgramData%\Synapic` readable
  5. `docs/packaging.md` states the MSI's role, commands and parity with Inno; a check fails if the two payloads drift

**Plans:** 5/5 complete — completed 2026-10-23

| Plan | What it did | Summary |
|------|-------------|---------|
| 09-01 | One payload definition, one guard | [09-01-SUMMARY.md](phases/09-msi-and-bundle-as-a-supported-channel/09-01-SUMMARY.md) |
| 09-02 | The MSI registers the indexer itself | [09-02-SUMMARY.md](phases/09-msi-and-bundle-as-a-supported-channel/09-02-SUMMARY.md) |
| 09-03 | One command, against a checked package | [09-03-SUMMARY.md](phases/09-msi-and-bundle-as-a-supported-channel/09-03-SUMMARY.md) |
| 09-04 | Install, dashboard, reinstall, upgrade, uninstall | [09-04-SUMMARY.md](phases/09-msi-and-bundle-as-a-supported-channel/09-04-SUMMARY.md) |
| 09-05 | The channel, documented | [09-05-SUMMARY.md](phases/09-msi-and-bundle-as-a-supported-channel/09-05-SUMMARY.md) |

**Outcome:** all five success criteria met on this machine, with three stated limits — registration rollback
not induced, the bundle compiled but not installed (the runtime was already present), and the release
workflow's MSI leg still guarded by `continue-on-error` because no runner has produced it yet. The phase
found a payload drift the v1.1 MSI could not have survived: because both installers glob `artifacts/win-x64`,
the MSI would have installed the 302 MB Inno setup exe into `Program Files`.

### Phase 10: Live Daminion round trip

**Goal:** Prove the catalog path against a real Daminion server instead of a fake seam: connect and report
the scope, tag items whose selected fields are empty, check the metadata back in, read it back from the
catalog, re-run to prove no duplicate keywords, and pause/resume through a real outage — committing the run
as the acceptance transcript.

**Requirements**: DAM-01, DAM-02, DAM-03, DAM-04
**Depends on**: Phase 7 (write-back and resilience), Phase 8 (acceptance harness)
**Success Criteria** (what must be TRUE):

  1. A credential-gated run connects to the provided server and reports server, catalog scope and item counts with no interactive prompt
  2. Items tagged in that scope read back from the catalog with exactly the written keywords, category and description; a second run adds no duplicate keywords
  3. Stopping the server mid-batch pauses the run with nothing dropped or permanently failed; restarting it lets the queue complete, and the log names the pause and the resume
  4. With credentials absent the test skips with a stated reason rather than passing silently, and the procedure plus its transcript are committed

**Plans:** TBD

### Phase 11: Close the UI-refactor gate

**Goal:** Make §10 criterion 6 provable, then prove it. Restate the criterion as a finite claim (every
reachable empty-result and report branch is inventoried and named by a test), add the missing view-model
behavioral evidence (Tag cancellation, fetch failure), reconcile criterion 8's already-closed 2026-10-13
record where the gate can see it, and re-run the 03-05 gate so Phase 3 can be called complete.

**Requirements**: GATE-01, GATE-02, GATE-03, GATE-04
**Depends on**: nothing (touches v1.0 code and its evidence only)
**Success Criteria** (what must be TRUE):

  1. `docs/ui-design.md` §10 criterion 6 reads as a finite, checkable claim with its rationale recorded beside it
  2. Every inventory row names a test that exists; a drift check fails when a row names a missing test
  3. Tag cancellation and fetch-failure presentation have view-model tests, and the §10 matrix shows criterion 6 ✓ and criterion 8 ✓
  4. The 03-05 gate re-runs clean — build 0/0, full suite green with no test weakened, layout audit 4 sizes × 4 routes — and Phase 3 is recorded complete

**Plans:** TBD

### Phase 12: UI acceptance and release

**Goal:** Walk the six pending `03-UAT.md` scenarios against the installed build and record what was seen,
then ship v1.0: archive phases 1–3 to `.planning/milestones/`, add the MILESTONES entry, tag `v1.0`, and
advance PROJECT.md and STATE.md.

**Requirements**: UAT-01, UAT-02, UAT-03
**Depends on**: Phase 9 (installed build), Phase 11 (gate green)
**Success Criteria** (what must be TRUE):

  1. All seven `03-UAT.md` rows carry a recorded result — the six pending scenarios executed, not inferred
  2. The dashboard-on-launch complaint is closed with the installed build as its evidence
  3. v1.0 has an archive, a MILESTONES entry and tag `v1.0`, and PROJECT.md/STATE.md name the next milestone

**Plans:** TBD

## 🚧 v1.0 UI Refactor (In Progress)

### Phase 1: Extract run state and operation contract

**Goal:** Extract the duplicated run machinery with zero visible change: introduce `RunStateViewModel`
(owns ProgressPercent/ProgressText/EtaText/CurrentFile/IsRunning/IsPaused, Start/Pause/Resume/Stop
commands, CTS lifecycle, the UI-marshalled 2000-cap `AppendLog`, and the shared `ProcessProgress`
ETA mapper) and have Step3ProcessViewModel, StepUpscaleViewModel and the dedup scan adopt it; collapse
the four verbatim `AppendLog` copies into one `RunLog` helper; add `IOperationViewModel` (Key, Title,
Description, HelpTopic, ParametersSummary, Run, IsRunEnabled, RunDisabledReason, Report) with thin
adapters over the three existing step view models. Service layer (WorkflowRunner/IWorkflowItemHandler)
untouched. Source: `docs/ui-refactor-plan.md` §3, §4 Phase 1.

**Requirements**: TBD
**Depends on**: Nothing (first phase)
**Success Criteria** (what must be TRUE):

  1. `dotnet build Synapic.Net.sln` exits 0 with 0 warnings and 0 errors
  2. `dotnet test Synapic.Net.sln -c Release` passes with zero failures (no test weakened or skipped)
  3. `git diff --stat` for the phase touches only files under `src/Synapic.Main/ViewModels/` and `src/Synapic.Main/Services/` — no `.axaml` view file changed
  4. `RunStateViewModel` exists in `src/Synapic.Main/ViewModels/Operations/` and Step3 + StepUpscale + dedup scan inherit or compose it
  5. Exactly one implementation of the `while (LogLines.Count > 2000)` cap remains in ViewModels/

**Plans:** 4/4 plans executed — phase complete (gate sweep 01-04 green: all five success criteria ✓)

Plans:

**Wave 1**
- [x] `01-01` — Create RunLog + RunStateViewModel; Step3 and StepUpscale adopt the base

**Wave 2** *(blocked on Wave 1 completion)*
- [x] `01-02` — Dedup adopts the base; MainWindowViewModel log cap delegates to RunLog
- [x] `01-03` — IOperationViewModel + three thin adapters + contract test

**Wave 3** *(blocked on Wave 2 completion)*
- [x] `01-04` — Phase gate sweep (build/test/scope/duplication gates + decision coverage)

**Cross-cutting constraints:**

- D-06: zero diffs under src/Synapic.Main/Services/ and zero .axaml diffs

### Phase 2: Add operation template and dashboard

**Goal:** Build the shared operation template and the dashboard: `OperationLayout.axaml` with the three
regions (Region A shared `DataSourceStrip` bound to one shell-owned source; Region B Parameters via
per-mode DataTemplates; Region C Output = shared `RunStateBar` + per-mode Report template);
`DashboardView` with the four panels (Settings, Tag, Dedup, Upscale) replacing the start screen, with
`ShellViewModel.Current == null` meaning dashboard and the app cold-starting there; delete the dedup
duplicate source card and `PrefillDedupSource()`; rewrite the pinned navigation tests to the new
`ui-design.md` §10 pins. Source: `docs/ui-design.md` §2, §3, §4, §6; `docs/ui-refactor-plan.md` §4 Phase 2.

**Requirements**: TBD
**Depends on:** Phase 1
**Success Criteria** (what must be TRUE):

  1. Cold start lands on the dashboard with exactly four panels (Settings, Tag, Dedup, Upscale), each opening its view
  2. A headless layout test asserts the same three regions in order `DataSource → Parameters → Output` on tagging, dedup and upscale routes
  3. Source configured inside Tag is shown identically in Dedup and Upscale (single shared instance)
  4. `UiLayoutAuditTests` passes at 900×600, 1024×700, 1280×800, 1600×900 for dashboard + all three routes, including the stacked variant
  5. `dotnet build` 0 warnings/errors and the full test suite green (rewritten pins only, no coverage removed)

**Plans:** 5/5 plans executed — phase complete (gate sweep 02-05 green: all five success criteria ✓)

Plans:

**Wave 1**
- [x] `02-01` — OperationLayout (three regions) + shared RunStateBar, hosted for the three routes

**Wave 2** *(blocked on Wave 1 completion)*
- [x] `02-02` — DataSourceStrip in Region A; delete dedup's parallel source path (PrefillDedupSource)
- [x] `02-03` — DashboardView (4 panels) + ShellViewModel.Current; cold start on dashboard

**Wave 3** *(blocked on Wave 2 completion)*
- [x] `02-04` — Rewrite pinned navigation tests to §10 criteria 1–3; extend layout audit (criterion 5) — suite green at 441 passed / 0 failed

**Wave 4** *(blocked on Wave 3 completion)*
- [x] `02-05` — Phase gate sweep (criteria 1, 2, 3, 5, 9 + decision coverage) — build 0/0, suite 441+5+6 / 0 failed, audit 19/19 at four sizes, coverage 7/7

### Phase 3: Collapse navigation and retire settings dialogs

**Goal:** Delete the legacy navigation chrome and consolidate settings: remove the numbered sidebar,
the Back/Next/StartOver action bar, the start-screen chooser and the step DataTemplates from
MainLayout; retire `EngineSettingsDialog`/`DedupSettingsDialog`/`UpscaleSettingsDialog` — parameter
panels live only in Region B; slim `WizardViewModel` into (or replace by) `OperationShellViewModel`;
`MainWindowViewModel` loses its route switches (RouteTitle/CreateSettingsDialog derive from `Current`);
dedup Apply gains `IsApplying` + indeterminate bar + enabled Stop (closes `UI-REVIEW.md` carried-forward
finding #1). Source: `docs/ui-design.md` §5, §6, §9, §10; `docs/ui-refactor-plan.md` §4 Phase 3.

**Requirements**: TBD
**Depends on:** Phase 2
**Success Criteria** (what must be TRUE):

  1. No operation settings dialog class remains in the tree; every operation parameter is reachable inline in Region B
  2. No sidebar, Back/Next/StartOver, or `HomeRoute`/`IsDedupRoute`/`IsUpscaleRoute` booleans remain in MainLayout/MainWindowViewModel/WizardViewModel
  3. Dedup Apply shows a busy state with Stop for the whole operation duration (UI-REVIEW finding #1 closed)
  4. All nine `ui-design.md` §10 acceptance criteria pass item-by-item
  5. `dotnet build` 0 warnings/errors, full suite green, layout audit green at 4 window sizes × 4 routes

**Plans:** 5/5 plans executed — 03-05 gate recorded; criterion 7 passes, criterion 6 remains △ and Phase 3 is not complete

> **Record correction (2026-10-13 addendum, surfaced here 2026-10-23):** §10 criterion 8 is **✓** — all 37
> preservation claims are supported or explicitly remapped (realistic/SNR → quality/balanced/fast and
> JPEG/WEBP quality). The "12 partial, 2 unsupported" figure elsewhere in this document is the pre-addendum
> state. Criterion 6's remaining blocker is its own wording — "exhaustive" enumeration is not finitely
> verifiable — which Phase 11 restates as a checkable claim. See `03-05-SUMMARY.md`.

Plans:

**Wave 1**
- [x] `03-01` — Retire the three settings dialogs; params inline in Region B; rewrite dialog pins

**Wave 2** *(blocked on Wave 1 completion)*
- [x] `03-02` — Delete sidebar/Back-Next/step chain; ShellViewModel.Current is the only navigation — build 0/0, suite 460/460 at that gate (resumed 03-05 now confirms 463), criterion-7 fact added, 32 facts migrated
- [x] `03-03` — Dedup Apply busy state (IsApplying + Stop) closes UI-REVIEW finding #1

**Wave 3** *(blocked on Wave 2 completion)*
- [x] `03-04` — Adopt the app-wide settings view (§5): record its must-haves and its evidence — 8 §5 pins + 22 audit facts green, §5 swept item by item; two rows recorded ✗ (a health-details surface, the wide section-nav layout)

**Wave 4** *(blocked on Wave 3 completion)*
- [x] `03-05` — Final gate rerun: build 0/0, solution suite 6 + 5 + 463, focused sweep 143/143, audit 22/22, F1 matrix 1/1; criterion 7 passes, criteria 6 and 8 remain ✗, so Phase 3 remains open — see 03-05-SUMMARY.md

**Settings view (§5)** *(delivered during Phase 3 by no plan of its own — adopted and recorded by `03-04`, verified by `03-05`)*

- [x] **Settings view (§5)** — the dashboard's Settings panel is the real app-wide settings view (inference
  server: status, Start/Stop/Build, auto-launch, per-platform variants, fallback warning; appearance: theme;
  logging: level, open log folder, diagnostics toggle; defaults: device, tagging confidence, max items;
  about: version, links), replacing Phase 2's placeholder (which had hosted the source form and the model
  picker — both moved to their one home, Region A and Tagging's Parameters). Delivered as untracked files
  that 03-01 left behind and that `MainWindowViewModel` then depended on, so they landed in 03-02's commit
  (`d79917c`): `ViewModels/SettingsViewModel.cs`, `Views/Dashboard/SettingsPanel.axaml(.cs)`,
  `tests/Synapic.Main.Tests/SettingsViewTests.cs`, `ConfigService.UiSettings` defaults,
  `SynapicLog.SetMinimumLevel`/`CurrentMinimumLevel`, and the persisted theme applied at startup.
  Pins: 8 facts in `SettingsViewTests` plus the layout audit walking the dashboard (settings panel included)
  at four sizes.
  **Status: delivered and suite-green; adopted by `03-04` on 2026-10-09** — the plan states §5's must-haves
  and 03-04-SUMMARY.md records the evidence (five sections present and in order, nothing configured in two
  places, per-section reach, persistence). 03-05 verifies that record rather than sweeping unplanned work as
  an extra item. Two §5 rows are recorded ✗ in the record: a health-details surface and §5's wide
  section-nav layout do not exist.

## Progress

| Phase | Milestone | Plans Complete | Status | Completed |
|-------|-----------|----------------|--------|-----------|
| 1. Extract run state and operation contract | v1.0 | 4/4 | Complete | 2026-10-08 |
| 2. Add operation template and dashboard | v1.0 | 5/5 | Complete | 2026-10-09 |
| 3. Collapse navigation and retire settings dialogs | v1.0 | 5/5 | Executed — criterion 6 open | — |
| 4. Extract headless engine core | v1.1 | 4/4 | Complete | 2026-10-22 |
| 5. Indexer service host and configuration | v1.1 | 4/4 | Complete | 2026-10-22 |
| 6. Work detection and durable job queue | v1.1 | 4/4 | Complete | 2026-10-22 |
| 7. Tagging pipeline and write-back | v1.1 | 4/4 | Complete | 2026-10-22 |
| 8. Packaging, install and unattended acceptance | v1.1 | 3/3 | Complete | 2026-10-23 |
| 9. MSI and bundle as a supported channel | v1.2 | — | Not started | — |
| 10. Live Daminion round trip | v1.2 | — | Not started | — |
| 11. Close the UI-refactor gate | v1.2 | — | Not started | — |
| 12. UI acceptance and release | v1.2 | — | Not started | — |

**v1.0 remaining work:** close §10 criterion 6 (and finish the UAT), then re-run the 03-05 gate — both
scheduled as Phases 11 and 12 of v1.2. Criterion 8 is already ✓ per the 2026-10-13 addendum; the older
"12 partial, 2 unsupported" figure in this document predates it. See
`.planning/phases/03-collapse-navigation-and-retire-settings-dialogs/03-05-SUMMARY.md`.
