# Milestones

Release history for Synapic.NET under the GSD milestone workflow. Detailed archives live in
`.planning/milestones/`; phase-level evidence lives in `.planning/phases/`.

Earlier application releases (v0.1.0, v0.2.0, v0.2.1, v0.3.0) predate this record and exist only as
git tags.

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
