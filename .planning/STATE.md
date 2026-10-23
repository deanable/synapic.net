---
gsd_state_version: 1.0
milestone: v1.1
milestone_name: Windows Indexer Service
status: milestone_complete
stopped_at: Milestone v1.1 archived on 2026-10-23 — 5/5 phases, 19/19 plans, 31/31 requirements
last_updated: "2026-10-23"
last_activity: 2026-10-23
progress:
  total_phases: 5
  completed_phases: 5
  total_plans: 19
  completed_plans: 19
  percent: 100
---

# Project State

## Project Reference

See: [PROJECT.md](PROJECT.md) (updated 2026-10-23, after the v1.1 close)

**Core value:** Pick a source and get correct, reviewable tags written back — without fighting the UI,
and without needing to be at the desk for routine tagging.

**Current focus:** Planning the next milestone (`/gsd:new-milestone`). v1.1 is shipped and archived;
v1.0 is still open on Phase 3's two remaining criteria.

## Current Position

**Milestone v1.1 — complete and archived.** Phases 4–8, 19/19 plans, 31/31 requirements.

- Archive: [milestones/v1.1-ROADMAP.md](milestones/v1.1-ROADMAP.md) ·
  [milestones/v1.1-REQUIREMENTS.md](milestones/v1.1-REQUIREMENTS.md)
- Release history: [MILESTONES.md](MILESTONES.md)
- Suite at the gate: **981 passed, 4 skipped, 0 failed** (`dotnet test Synapic.Net.sln -c Release -m:1`),
  Release build **0/0**

**Milestone v1.0 — in progress, Phase 3 open.** Phases 1 and 2 complete; Phase 3 executed all five
plans but its gate left `docs/ui-design.md` §10 criteria 6 and 8 open (see Carried debt).

## Deferred Items

Items acknowledged and deferred at the v1.1 milestone close on 2026-10-23:

| Category | Item | Status |
|----------|------|--------|
| uat_gap | phase 03 — `03-UAT.md` | testing (6 pending scenarios) |

This item belongs to **v1.0 / Phase 3**, which remains open; it was acknowledged rather than resolved
because resolving it is v1.0 work, not part of closing v1.1. It is tracked in
`.planning/phases/03-collapse-navigation-and-retire-settings-dialogs/03-UAT.md` and in Carried debt below.

## Milestone v1.1 at a glance

| Phase | Name | Requirements | Plans | Gate |
|-------|------|--------------|-------|------|
| 4 | Extract headless engine core | CORE-01..04 | 4/4 | 5/5 ✓ — solution 756 cases, 0 tests deleted, no Avalonia/Main in Core's deps |
| 5 | Indexer service host and configuration | HOST-01..07 | 4/4 | 5/5 ✓ — `sc.exe` round trip, ready 97.9 s, stop 4.87 s, no orphan, uninstall clean |
| 6 | Work detection and durable job queue | SCAN-01..07 | 4/4 | 5/5 ✓ — scan/dedup/recovery/`--status` on a console run (923 cases) |
| 7 | Tagging pipeline and write-back | TAG-01..08 | 4/4 | 5/5 ✓ — real mid-batch kill observed, all six rows completed |
| 8 | Packaging, install and unattended acceptance | OPS-01..05 | 3/3 | 5/5 ✓ — install → tag → stop → crash-recover → uninstall on a real machine |

Evidence lives in each phase's `NN-MM-SUMMARY.md` and, for v1.1, in the milestone archive.

## Carried debt from v1.0

Phase 3 (collapse-navigation-and-retire-settings-dialogs) is **not complete**: all five plans executed,
but the 03-05 gate left §10 criteria 6 (empty-state branches lack behavioral evidence — zero-result
Daminion and all-failed Upscale) and 8 (the 37-row preservation map still has 12 partial and 2
unsupported entries) open. This debt is **not** absorbed into v1.1 and must be closed before v1.0 is
called done. Do not mark Phase 3 complete on the strength of a green suite. See
`.planning/phases/03-collapse-navigation-and-retire-settings-dialogs/03-05-SUMMARY.md`.

Also carried: the compiled help corpus still names the old wizard steps (`HelpTopics.ForStepIndex` maps
0–5); renaming those topics is a docs pass, not a phase.

## Known limitations of the v1.1 delivery

Stated as not-exercised, never as passed:

- The **MSI channel** was not built or exercised (WiX absent on the gate machine); only the Inno path was.
- **Daminion** fault paths ran against fake network seams, not a live catalog.
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

### Decisions

The v1.1 decision table (D-11-01…08, D-11-23, D-11-28/29, D-11-31) with outcomes lives in
[PROJECT.md](PROJECT.md#key-decisions); the full rationale for each is in
[milestones/v1.1-ROADMAP.md](milestones/v1.1-ROADMAP.md#milestone-summary). Two are marked ⚠ Revisit:
DPAPI at LocalMachine scope (any local administrator can decrypt) and the unbuilt MSI channel.

Decisions for v1.0 are recorded in the Phase 1–3 summaries.

### Open questions

- **What is the next milestone?** Not yet scoped. Candidates in PROJECT.md → Next Milestone Goals:
  close v1.0's two criteria, verify the MSI channel and a live Daminion catalog, v1.2 background
  dedup/upscale, or the indexer operator surface (UI-01/DIST-01).
