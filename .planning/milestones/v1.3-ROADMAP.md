# Roadmap: Synapic.NET

**Milestone:** v1.3 Service Console · **Defined:** 2026-10-24 · Requirements: [REQUIREMENTS.md](REQUIREMENTS.md)

Previous milestones are archived under [milestones/](milestones/) — v1.0 UI Refactor
and v1.2 Release Integrity shipped 2026-10-24 (`v1.0` tag), v1.1 Windows Indexer
shipped 2026-10-23 (`v1.1` tag). Release history: [MILESTONES.md](MILESTONES.md).

## Active milestone: v1.3 Service Console

### Phase 13: The Service tab

**Goal:** Manage the background indexer from the app (UI-01): service
installation and lifecycle, watch-folder management, and a live queue view —
the existing watch → queue → tag → write-into-file pipeline with its GUI face.

**Requirements**: SVC-01..05
**Depends on**: nothing (builds on the shipped v1.1 service and v1.0 UI)

**Success Criteria** (what must be TRUE):

  1. The Settings view's eighth tab carries the three Service sections, covered by settings search and the compiled help (`check-help.py` green, `HelpScopeTests` green)
  2. Lifecycle actions are single UAC-elevated launches of the indexer's own verbs with correct gating; status is read unelevated
  3. Watch folders save through one `--apply-config` round trip that validates before saving, preserves unknown members, and restarts a running service
  4. The queue view reads through `Synapic.Indexer.Contract`, and the contract↔schema drift is pinned by a test against a service-written database
  5. Every reachable missing-something branch is inventoried and named (S-03…S-08); build 0/0 and the full suite green

**Plans:** 1/1 executed (2026-10-24)

| Plan | What it did | Summary |
|------|-------------|---------|
| 13-01 | The Service tab, the contract library, and the `--apply-config` verb | [13-01-SUMMARY.md](phases/13-service-tab/13-01-SUMMARY.md) |

**Outcome:** see the plan summary; the phase's evidence is the test suite
(`ApplyConfigCommandTests`, `ContractQueueReaderTests`,
`IndexerServiceViewModelTests`, the pinning suites) plus the live smoke on a
machine with the service installed and running.

## Progress

| Phase | Milestone | Plans Complete | Status | Completed |
|-------|-----------|----------------|--------|-----------|
| 13. The Service tab | v1.3 | 1/1 | Complete | 2026-10-24 |

**v1.3 remaining work:** none against SVC-01..05. Deferred items live in
[REQUIREMENTS.md](REQUIREMENTS.md); the next milestone is chosen from
PROJECT.md → Candidates.
