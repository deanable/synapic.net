# Milestones

Release history for Synapic.NET under the GSD milestone workflow. Detailed archives live in
`.planning/milestones/`; phase-level evidence lives in `.planning/phases/`.

Earlier application releases (v0.1.0, v0.2.0, v0.2.1, v0.3.0) predate this record and exist only as
git tags.

---
---

## v1.4 — Service Console application

**Shipped:** 2026-10-24
**Phases:** 14 (1 phase, 1 plan)
**Requirements:** 2/2 (SVC-06 the split, SVC-07 the verified save)
**Record:** [REQUIREMENTS.md](REQUIREMENTS.md) / [ROADMAP.md](ROADMAP.md) (current tree); evidence in [phases/14-service-console/](phases/14-service-console/)

### Delivered

The service's management UI became its own application —
`Synapic.ServiceConsole.exe` ("Synapic Service") — shipped by both installers
with its own Start-menu shortcut; the main app carries no service-management
surface. The watch-folder save became a verified round trip: the elevated
`--apply-config` writes a result file, the console awaits the process and
reports applied/restarted, applied/next-start, the refusal reasons, or
not-started — closing the fire-and-forget hole behind the "watch folder does
not persist" report.

---

## v1.3 — Service Console

**Shipped:** 2026-10-24
**Phases:** 13 (1 phase, 1 plan)
**Requirements:** 5/5 (SVC-01..05)
**Archive:** [REQUIREMENTS.md](REQUIREMENTS.md) / [ROADMAP.md](ROADMAP.md) (current tree); evidence in [phases/13-service-tab/](phases/13-service-tab/)

### Delivered

The Settings view's Service tab: the background indexer managed from the app —
service install/Start/Stop/Uninstall as single UAC launches of the indexer's
own verbs, watch folders saved through the validated `--apply-config` round
trip, and the durable queue live (counts, failures, audit activity) through the
new `Synapic.Indexer.Contract` shared library. UI-01, delivered.
*(Its surface moved to the console application in v1.4, one milestone later;
the archive records it in the form it shipped.)*

## v1.2 — Release Integrity (and the v1.0 ship)

**Shipped:** 2026-10-24
**Phases:** 9–12 (4 phases, 11 plans)
**Requirements:** 16/16 (MSI-01..05, DAM-01..04, GATE-01..04, UAT-01..03)
**Archive:** [milestones/v1.2-ROADMAP.md](milestones/v1.2-ROADMAP.md), [milestones/v1.2-REQUIREMENTS.md](milestones/v1.2-REQUIREMENTS.md)
**Tag:** `v1.0` (the milestone's purpose was shipping it; v1.1 had been tagged at its own close)

### Delivered

The MSI channel built, verified end to end and unguarded in the release
workflow; the Daminion fault table proven against real catalogs (a real outage
paused and resumed a run); v1.0's gate closed behind the restated, drift-checked
empty-branch inventory; all seven UAT scenarios executed on the installed build
with recorded observations; **v1.0 shipped** — archived, tagged, released
(Inno + MSI + bundle, macOS dmg, sidecars; the release run's own CHM gate
caught two non-ASCII bytes and the re-run is the one that published).

### Stats

| Metric | Value |
|--------|-------|
| Gate evidence (phase 11) | build 0/0; suite `-m:1` 1011 passed / 0 failed / 6 credential-gated skips; layout audit 20/20 |
| UAT (phase 12) | 7/7 scenarios on the installed build, observations quoted (`12-ACCEPTANCE.md`) |
| Timeline | 2026-10-23 → 2026-10-24 |

---

## v1.1 — Windows Indexer Service

**Shipped:** 2026-10-23
**Phases:** 4–8 (5 phases, 19 plans)
**Requirements:** 31/31 (CORE-01..04, HOST-01..07, SCAN-01..07, TAG-01..08, OPS-01..05)
**Archive:** [milestones/v1.1-ROADMAP.md](milestones/v1.1-ROADMAP.md), [milestones/v1.1-REQUIREMENTS.md](milestones/v1.1-REQUIREMENTS.md)
**Tag:** `v1.1`
**Base commit:** `6533b8db` (last commit before v1.1 execution began)

### Stats

| Metric | Value |
|--------|-------|
| Phases | 5 (4, 5, 6, 7, 8) |
| Plans | 19 (4 + 4 + 4 + 4 + 3) |
| Requirements | 31 |
| New/edited paths | 154 (22 staged moves, 25 untracked entries covering 137 files, 129 modified files) |
| New code | `src/Synapic.Indexer` + `tests/Synapic.Indexer.Tests`: 84 C# files, 9 611 lines |
| Moved code | `Synapic.Core` + `Synapic.Core.Tests`: 8 263 lines, most relocated out of `Synapic.Main` |
| Suite at the gate | 981 passed, 4 skipped, 0 failed (`-m:1`), Release build 0/0 |
| Timeline | 2026-10-22 → 2026-10-23 (two days, five phases) |

### Delivered

`Synapic.Indexer.exe` — a Windows Service that tags images on an interval, on filesystem events or
from a Daminion untagged-scope query, driven by one durable SQLite queue, writing tags back to files
or the catalog through the same `Synapic.Core` engine the desktop app runs. It installs from the
Windows installer as `NT AUTHORITY\LocalService` with restart-on-failure recovery, logs to the Event
Log and `%ProgramData%\Synapic\Logs`, stops inside its 10-second budget with no orphaned sidecar, and
recovers every `InFlight` job after a crash or reboot.

### Key accomplishments

1. **The engine is headless and shared.** `Synapic.Core` (net10.0, no Avalonia, no `Synapic.Main`
   reference) holds the inference client, sidecar host, metadata writer, Daminion client and tagging
   kernel; `Synapic.Main` and `Synapic.Indexer` both build on it, so parity is structural rather than a
   promise. `Synapic.Core.Tests` proves headlessness by compiling without a desktop test host.
2. **An installable service with two-source configuration.** Registry defaults plus a ProgramData JSON
   override, schema-versioned and validated at startup so a bad value exits 2 naming the key. Install,
   uninstall and console mode are one control surface; the elevated `sc.exe` round trip ran ready in
   97.9 s and stopped in 4.87 s.
3. **Work detection that never double-processes.** Interval scans, a debounced `FileSystemWatcher` with
   an exclusive-read probe, and Daminion untagged queries all feed one SQLite queue with five explicit
   states, idempotent enqueueing, canonicalized path keys and startup recovery of `InFlight` rows.
4. **One failure table, observed not seeded.** The §8 failure rows (sidecar crash → restart with
   backoff, locked file → retry after 60 s, catalog outage → pause 5 min, corrupt image → permanent
   failure) live in one classifier; the real mid-batch kill of `synapic-inference.exe` was observed in
   `RealSidecarKillTests` and all six rows completed with the tags on disk.
5. **The acceptance run found what tests could not.** The installer registered the service as
   LocalSystem instead of the documented LocalService, and swallowed a failed start. Both were fixed and
   re-verified; the second install came up RUNNING with the right account, three dropped fixtures were
   tagged unattended in 23.2 s, and a `taskkill /F` with 18 `InFlight` rows recovered all of them.
6. **Documentation written against the code, and kept true by a test.** `docs/indexer-operations.md`
   covers install, both configuration sources, the full key table, credentials, operating commands and
   the five failure modes; `ConfigDocumentationTests` reflects over `IndexerConfig` and fails if any key
   path is undocumented.

### Known gaps at close

None against v1.1's own requirements. Two verification rows are weaker than the requirement text and
are recorded as not-exercised rather than passed: the **MSI channel** (WiX absent on the gate machine;
only the Inno path was built and exercised) and a **live Daminion catalog** (the fault paths ran against
fake network seams).

### Deferred items at close

One open artifact was acknowledged and deferred at this close — `.planning/phases/03-.../03-UAT.md`
(6 pending scenarios). It belongs to **v1.0 / Phase 3**, which is still open, not to v1.1. See
`.planning/STATE.md` → Deferred Items.

### Carried debt (not v1.1's)

Phase 3 remains incomplete: §10 criteria 6 and 8 are still open, so v1.0 is not shipped. The compiled
help corpus still names the old wizard steps.

### Archive

- Roadmap: [milestones/v1.1-ROADMAP.md](milestones/v1.1-ROADMAP.md)
- Requirements: [milestones/v1.1-REQUIREMENTS.md](milestones/v1.1-REQUIREMENTS.md)

## v1.0 — UI Refactor (dashboard-first, dialogs retired)

**Shipped:** 2026-10-24
**Phases:** 1–3 (3 phases, 14 plans)
**Requirements:** §10 criteria 1–9 + Phase-3 roadmap success criteria 1–5 — all ✓ (no REQ-ids; v1.0's requirement set is archived in full at the links below)
**Archive:** [milestones/v1.0-ROADMAP.md](milestones/v1.0-ROADMAP.md), [milestones/v1.0-REQUIREMENTS.md](milestones/v1.0-REQUIREMENTS.md)
**Tag:** `v1.0`
**Base commit:** `f511036d` (last commit before the UI-refactor work began)

### Stats

| Metric | Value |
|--------|-------|
| Phases | 3 (1, 2, 3) |
| Plans | 14 (4 + 5 + 5), plus the §5 settings view adopted by `03-04` |
| Gate evidence (2026-10-23 re-run) | clean Release build 0/0; full suite `-m:1` 1011 passed / 0 failed / 6 credential-gated skips; layout audit 20/20 |
| UAT (2026-10-24, installed build) | 7/7 scenarios passed with recorded observations (`03-UAT.md`, transcript `phases/12-…/12-ACCEPTANCE.md`) |
| Timeline | 2026-10-08 → 2026-10-24 (plans executed in two days; the gate stood open on criterion 6's wording until the 2026-10-23 restatement) |

### Delivered

The dashboard-first UI: cold start on the Settings view with every operation route in the sidebar and
its parameters on the Settings view's tabs — no operation-settings dialog anywhere. One shared source
across routes; a run bar with busy state and a working Stop for every long-running action (dedup
Apply included); per-route output with report, export, retry and Daminion-write verification. Every
empty-result and report branch the shipped UI can reach is inventoried in
`docs/ui-empty-branches.md` with the behavioral test that names it, kept honest by
`EmptyBranchInventoryTests`, which fails the suite when any citation rots.

### Known gaps at close

None against v1.0's own criteria. The original UAT major finding ("no dashboard on launch" — a stale
MSI older than the UI) is closed on the record: the installed build, launched from the Start-menu
shortcut built from this tree, opens on the dashboard (`03-UAT.md` gap closure, 2026-10-24). Deferred
items live in v1.2's REQUIREMENTS (background dedup/upscale, the in-app indexer status surface,
indexer self-update, the help corpus still naming wizard steps, unsigned installers).

---