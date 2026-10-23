---
gsd_state_version: 1.0
milestone: v1.2
milestone_name: Release Integrity
status: executing
last_updated: "2026-10-23T19:30:00.000Z"
last_activity: 2026-10-23
progress:
  total_phases: 4
  completed_phases: 3
  total_plans: 11
  completed_plans: 10
  percent: 91
---

# Project State

## Project Reference

See: [PROJECT.md](PROJECT.md) (updated 2026-10-23 — v1.2 started)

**Core value:** Pick a source and get correct, reviewable tags written back — without fighting the UI,
and without needing to be at the desk for routine tagging.

**Current focus:** Milestone v1.2 Release Integrity — close v1.1's unproven claims (MSI channel, live
Daminion) and finally ship v1.0. Requirements: [REQUIREMENTS.md](REQUIREMENTS.md); phases 9–12.

## Current Position

Phase: 11 of 12 — Close the UI-refactor gate — **complete** (GATE-01..04)
Plan: 1 of 1 executed (11-01 gate re-verification)
Status: criterion 6 restated as the finite inventory in `docs/ui-empty-branches.md` (56 rows: 55 named,
1 shadowed, 0 partial, 0 gap) with the `EmptyBranchInventoryTests` drift check over every `Named by`
citation; the eleven gap rows and seven partial rows closed by naming each in a test, which found and
fixed two product defects (`ProbabilitySummary` switching on display strings; `Refresh` appending the
empty-report wording to populated reports); the §10 matrix records criterion 6 ✓ and 8 ✓ and the 03-05
verdict is PASSED. Full evidence, including the 2026-10-23 build and suite numbers, in
`.planning/phases/03-collapse-navigation-and-retire-settings-dialogs/03-05-SUMMARY.md` (Phase 11
section) and `.planning/phases/11-close-the-ui-refactor-gate/11-01-SUMMARY.md`.
Next: Phase 12 — UAT-01 and UAT-02 are complete (2026-10-24: all seven `03-UAT.md` scenarios executed
against the installed build by `artifacts/phase-12/uat-sweep.ps1`, observations recorded; the
dashboard-on-launch complaint closed on the record). Remaining: UAT-03 — ship v1.0 (archive phases 1–3,
MILESTONES entry, tag `v1.0`, PROJECT/STATE advanced).
Last activity: 2026-10-24 — Phase 12 UAT executed: MSI dry run from the current tree (guard dropped on
the release workflow's MSI leg — the residual is retired), silent elevated install exit 0, service
RUNNING as LocalService with recovery actions, seven scenarios passed with quoted observations.

## Deferred Items

Items acknowledged and deferred at the v1.1 milestone close on 2026-10-23:

| Category | Item | Status |
|----------|------|--------|
| uat_gap | phase 03 — `03-UAT.md` | **resolved 2026-10-24 (v1.2 Phase 12, UAT-01):** all seven scenarios executed against the installed build with recorded observations; the original dashboard gap closed with its evidence |

This item belonged to **v1.0 / Phase 3**. It was acknowledged rather than resolved at the v1.1 close;
v1.2 Phase 12 (UAT-01/UAT-02) has now resolved it — see
`.planning/phases/03-collapse-navigation-and-retire-settings-dialogs/03-UAT.md` (results) and
`.planning/phases/12-ui-acceptance-and-release/12-ACCEPTANCE.md` (transcript).

## Milestone v1.2 at a glance

| Phase | Name | Requirements | Plans |
|-------|------|--------------|-------|
| 9 | MSI and bundle as a supported channel | MSI-01..05 | 5/5 — 5/5 ✓ (silent install 0, service LocalService/Auto/restart×3, upgrade over a second ProductCode, uninstall 1060 with ProgramData kept, installed dashboard PASS) |
| 10 | Live Daminion round trip | DAM-01..04 | 4/4 — 4/4 ✓ (DAM-01 scope reported on both catalogs, DAM-02 exact-once round trip on items 26-50, DAM-03 real firewall outage paused then resumed six rows to completion, DAM-04 transcript + procedure) |
| 11 | Close the UI-refactor gate | GATE-01..04 | 1/1 — gate re-verified 2026-10-23: criterion 6 restated as the drift-checked inventory, matrix records 6 ✓ and 8 ✓, 03-05 verdict PASSED |
| 12 | UI acceptance and release | UAT-01..03 | 1/1 — UAT-01 ✓ UAT-02 ✓ (seven scenarios executed on the installed build, observations recorded, dashboard complaint closed); UAT-03 (ship) remains |

Dependency shape: 9, 10 and 11 are independent; 12 needs 9 (installed build) and 11 (gate green).
The Daminion target is a real server the user provides; credentials stay in `SYNAPIC_TEST_DAMINION_*`
variables or the saved Windows credential, never in the repository.

## Milestone v1.1 (shipped 2026-10-23)

| Phase | Name | Requirements | Plans | Gate |
|-------|------|--------------|-------|------|
| 4 | Extract headless engine core | CORE-01..04 | 4/4 | 5/5 ✓ — solution 756 cases, 0 tests deleted, no Avalonia/Main in Core's deps |
| 5 | Indexer service host and configuration | HOST-01..07 | 4/4 | 5/5 ✓ — `sc.exe` round trip, ready 97.9 s, stop 4.87 s, no orphan, uninstall clean |
| 6 | Work detection and durable job queue | SCAN-01..07 | 4/4 | 5/5 ✓ — scan/dedup/recovery/`--status` on a console run (923 cases) |
| 7 | Tagging pipeline and write-back | TAG-01..08 | 4/4 | 5/5 ✓ — real mid-batch kill observed, all six rows completed |
| 8 | Packaging, install and unattended acceptance | OPS-01..05 | 3/3 | 5/5 ✓ — install → tag → stop → crash-recover → uninstall on a real machine |

Evidence lives in each phase's `NN-MM-SUMMARY.md` and, for v1.1, in the milestone archive.

## Carried debt from v1.0

Phase 3 (collapse-navigation-and-retire-settings-dialogs): **closed by v1.2 Phase 11** (2026-10-23).
All five plans executed, and the gate that had stood open since 2026-10-09 on §10 criterion 6's
wording is now PASSED — the criterion was restated as the finite inventory in
`docs/ui-empty-branches.md` (D-12-03), every row names a behavioral test, and the drift check keeps
the citations resolvable. What still stands between here and shipping v1.0 is Phase 12: the six
pending `03-UAT.md` scenarios and the ship itself. See
`.planning/phases/03-collapse-navigation-and-retire-settings-dialogs/03-05-SUMMARY.md`.

Also carried: the compiled help corpus still names the old wizard steps (`HelpTopics.ForStepIndex` maps
0–5); renaming those topics is a docs pass, not a phase.

## Known limitations of the v1.1 delivery

Stated as not-exercised, never as passed:

- The **MSI channel** was not built or exercised (WiX absent on the gate machine); only the Inno path was.
  **Closed in v1.2 Phase 9** — rebuilt with the indexer payload and verified install → reinstall →
  upgrade → uninstall, with the transcripts in `artifacts/phase-9/`. ~~Residual: the bundle compiled but
  was not installed, registration rollback was not induced, and the release workflow's MSI leg is still
  `continue-on-error` because no runner has produced it.~~ **The release-workflow guard was dropped
  2026-10-24** after a local dry run of `build/package-windows-msi.ps1` from a clean payload produced
  both artifacts (MSI 234.5 MB, bundle 292.8 MB, 87 payload files verified). Still not exercised:
  bundle install on a clean machine and registration rollback.
- **Daminion** fault paths ran against fake network seams, not a live catalog. **Closed in v1.2 Phase 10**
  — a live catalog round trip and a real firewall outage ran; what is still only modelled is the server
  process stopping (the outage is this machine losing the route, which is what the client can see).
- **Parity** used the desktop's kernel with the real model, not the GUI (`Synapic.Main.exe` has no
  headless processing entry).

- A **fresh install ends stopped** until `indexer-config.json` exists (finish page and docs say so).
- The **parallel solution test run** is unstable here (one differing Avalonia UI test per run, each
  passing alone); `-m:1` is the recorded command.

- `overwriteExistingTags:true` merges rather than replacing keywords — documented, pinned by a test.

## Performance Metrics

**v1.1:** 5 phases and 19 plans in two calendar days (2026-10-22 → 2026-10-23). Per-plan task timing was
not recorded for this milestone.
**v1.0 baseline:** 14 plans, average 25 min, 5.4 hours total.

## Accumulated Context

### Roadmap Evolution

- 2026-10-08: v1.0 planned (Phases 1–3) from `docs/ui-refactor-plan.md`.
- 2026-10-09: Phase 3 gained unplanned work (the §5 settings view), adopted by plan 03-04 and verified by 03-05.
- 2026-10-22: **Milestone v1.1 added** from `docs/windows-indexer.md`. Phase numbering continues at 4.
- 2026-10-22: **Phases 4–7 executed and gated** — Core extracted, service host standing, detection and
  queue durable, pipeline with write-back and resilience green.

- 2026-10-23: **Phase 8 executed and v1.1 closed.** The acceptance run found two defects no unit test
  could (LocalSystem instead of LocalService; a swallowed install-start failure), both fixed and
  re-verified. Live-model parity recorded with identical tags on both paths.

- 2026-10-23: **Milestone v1.1 archived.** Roadmap and requirements moved to `milestones/`; ROADMAP.md
  collapsed v1.1 into a `<details>` block; `REQUIREMENTS.md` removed for the next milestone. Phase 3
  remains open and is now the oldest outstanding work in the project.
- 2026-10-23: **Milestone v1.2 Release Integrity planned** (phases 9–12, 16 requirements). Three findings
  shaped it: WiX 7 is installed now and `artifacts/msi` holds Oct-4/Oct-14 packages that predate the
  indexer — the exact stale-installer root cause `03-UAT.md` recorded for "no dashboard on launch"; a live
  Daminion server is reachable, so the fault table can be proven for real; and Phase 3's blocker is
  criterion 6's wording, not missing tests — criterion 8 was already closed by the 2026-10-13 addendum
  while ROADMAP/STATE still quoted the older "12 partial" figure.

- 2026-10-23: **Phase 10 executed and closed.** The live catalog run proved the write path and found
  three defects no seam test could (verifier read payload names, object-shaped multi-values, a
  non-idempotent catalog add that doubled keywords on a re-run). The first server went offline mid-phase;
  DAM-03 was proven against a second live catalog, where the attempt exposed two more holes: an outage
  helper that blocked only IPv4 while the host answered on a link-local IPv6 address, and a saved search
  that existed under the requested id but held no records.

- 2026-10-23: **Phase 9 executed and gated.** The MSI now carries the indexer and registers its service
  through the indexer's own `--install`/`--uninstall`. The phase found a payload drift that would have
  shipped a 302 MB copy of the Inno setup exe inside the MSI; both installers now share one payload
  definition, enforced by `build/check-installer-payloads.py` and by a File-table read-back of the
  compiled package. Verified on this machine: install, dashboard, in-place reinstall, upgrade over a
  second ProductCode, and uninstall.

### Decisions

**v1.2 decisions locked at planning:**

- **D-12-01: The MSI becomes a supported channel, not a side channel.** The Inno EXE remains the default
  download, but the MSI and its Burn bundle are rebuilt with the indexer payload, verified through silent
  install / in-place upgrade / uninstall, and documented with their exact commands and parity. A package
  older than the app is the recorded cause of the v1.0 UAT failure, so "it builds" is not enough.
- **D-12-02: Daminion is verified against a real server.** The user provides one; the run is opt-in and
  credential-gated (`SYNAPIC_TEST_DAMINION_URL`/`_USERNAME`/`_PASSWORD` or the saved credential), and skips
  with a reason when absent. Credentials are never written to the repository.
- **D-12-03: §10 criterion 6 is restated, not abandoned.** "Exhaustive future-proof enumeration" cannot be
  proven; the replacement claim — every reachable empty-result/report branch is inventoried and named by a
  behavioral test, enforced by a drift check — is checkable and stronger than the informal evidence it
  replaces. Criterion 8 needs no code work: it is ✓ as of the 2026-10-13 addendum and only the record needs
  reconciling.

The v1.1 decision table (D-11-01…08, D-11-23, D-11-28/29, D-11-31) with outcomes lives in
[PROJECT.md](PROJECT.md#key-decisions); the full rationale for each is in
[milestones/v1.1-ROADMAP.md](milestones/v1.1-ROADMAP.md#milestone-summary). Two are marked ⚠ Revisit:
DPAPI at LocalMachine scope (any local administrator can decrypt) and the unbuilt MSI channel.

Decisions for v1.0 are recorded in the Phase 1–3 summaries.

### Open questions

- **After v1.2:** background dedup/upscale on the indexer queue (BG-01/02), the operator surface
  (UI-01/DIST-01), or installer signing. Not scoped; see PROJECT.md → Candidates After v1.2.
- **MSI service registration:** whether the package registers the indexer itself or ships the one
  documented elevated `--install` command is Phase 9's first task; D-11-31 chose the latter for v1.1, and
  MSI-03 allows either as long as the acceptance runs and verifies what ships.
