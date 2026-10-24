# Roadmap: Synapic.NET

**Milestone:** v1.4 Service Console application · **Defined:** 2026-10-24 · Requirements: [REQUIREMENTS.md](REQUIREMENTS.md)

Earlier milestones are archived under [milestones/](milestones/) — v1.0/v1.2 shipped
2026-10-24 (`v1.0` tag), v1.1 shipped 2026-10-23 (`v1.1` tag), v1.3 (the service
tab) shipped 2026-10-24 and its surface moved to the console app in this milestone.
Release history: [MILESTONES.md](MILESTONES.md).

## Active milestone: v1.4 Service Console application

### Phase 14: The split and the verified save

**Goal:** The service gets its own UI — `Synapic.ServiceConsole.exe` — and the
watch-folder save becomes a round trip whose outcome the user sees.

**Requirements**: SVC-06, SVC-07
**Depends on**: v1.3 (the surface to move)

**Success Criteria** (what must be TRUE):

  1. `Synapic.ServiceConsole` builds as its own app (net10.0, contract reference only — no engine, no sidecar, no tagging surface) and publishes into the shared payload; both installers carry it, each with its own Start-menu shortcut
  2. The main application is back to seven Settings tabs with no service-management code and no Contract reference; every pinning suite is green again at seven
  3. `--apply-config` writes a result file on every exit path; the console awaits the elevated process and reports applied/restarted, applied/next-start, the refusal messages (edit kept dirty), or not-started
  4. The empty-branch inventory follows the move (S-03…S-10 now name console tests; the drift check resolves citations across all test projects) and stays green
  5. Build 0/0, full suite green, help check green, and the live round trip on the dev machine verified: a save through the elevated verb changes the config file and reports itself

**Plans:** 1/1 executed (2026-10-24)

| Plan | What it did | Summary |
|------|-------------|---------|
| 14-01 | The console app, the split, the verified save | [14-01-SUMMARY.md](phases/14-service-console/14-01-SUMMARY.md) |

## Progress

| Phase | Milestone | Plans Complete | Status | Completed |
|-------|-----------|----------------|--------|-----------|
| 14. The split and the verified save | v1.4 | 1/1 | Complete | 2026-10-24 |

**v1.4 remaining work:** none against SVC-06/07. The next milestone is chosen
from PROJECT.md → Candidates (a shared inference sidecar is now on that list).
