# Synapic.NET

## What This Is

Synapic is a desktop app that tags images with a local vision-language model and writes the
results to image files or a Daminion catalog. It is two processes: an Avalonia/C# host (UI,
orchestration, metadata writing) and a bundled Python sidecar (transformers/torch inference)
talking HTTP/JSON over localhost. This repository is the .NET migration of the original
Python/CustomTkinter app.

Since v1.1 it is also a third process: `Synapic.Indexer.exe`, a Windows Service that runs the same
engine with no desktop — scanning folders and catalogs on a schedule, tagging through one durable
queue, and writing back while nobody is at the desk.

## Core Value

Pick a source and get correct, reviewable results written back — without the user ever fighting the
UI, and without anyone needing to be at the desk for the routine tagging to happen.

## Current Milestone: v1.2 Release Integrity

**Goal:** Close the three gaps v1.1 left unproven — the MSI channel, the live Daminion path, and v1.0's own
gate — and finally ship v1.0. No new product capability.

**Target features:**
- MSI and Burn bundle rebuilt with the indexer payload and verified end to end: silent install, in-place
  upgrade, uninstall, service parity with the Inno path, documented role (phase 9)
- A live Daminion round trip on the provided server: connect, tag an untagged scope, check in, read back,
  no duplicate keywords on re-run, and a real outage that pauses and resumes (phase 10)
- §10 criterion 6 restated as a finitely checkable claim, backed by an inventory whose every row names an
  existing test, plus the missing Tag cancellation/fetch-failure evidence and a clean 03-05 re-run (phase 11)
- The six pending v1.0 UAT scenarios executed on the installed build, then v1.0 archived and tagged (phase 12)

Requirements: [REQUIREMENTS.md](REQUIREMENTS.md) (16 requirements, MSI/DAM/GATE/UAT).

## Current State

**Shipped:** v1.1 Windows Indexer Service (2026-10-23) — 5 phases, 19 plans, 31/31 requirements.
See [MILESTONES.md](MILESTONES.md) and [milestones/v1.1-ROADMAP.md](milestones/v1.1-ROADMAP.md).

The engine now lives in `Synapic.Core` (net10.0, Avalonia-free): inference client, sidecar host,
metadata writer, Daminion client and the tagging kernel. `Synapic.Main` (desktop) and
`Synapic.Indexer` (service) both consume it, so indexer/desktop parity is structural. The service
installs as `NT AUTHORITY\LocalService` with restart-on-failure recovery, reads
`HKLM\SOFTWARE\Synapic\Indexer` plus a `%ProgramData%\Synapic\indexer-config.json` override, drives a
SQLite job queue from interval scans, a debounced filesystem watcher or Daminion untagged queries,
and stops inside its 10-second budget with no orphaned sidecar.

**Test baseline at the v1.1 gate:** 981 passed, 4 skipped, 0 failed (`dotnet test Synapic.Net.sln -c
Release -m:1`); Release build 0/0. The default parallel run is unstable on this machine — one differing
Avalonia UI test per run, each passing alone — so gates record the serial run.

**Open:** milestone v1.0 (UI refactor) is not shipped. Phase 3 executed all five plans, but its gate
left `docs/ui-design.md` §10 criteria 6 and 8 open. That debt stays against Phase 3 and is not
absorbed into v1.1.

## Candidates After v1.2

Not scoped. The evidence suggests, in order:

- **Background operations (BG-01/BG-02).** Dedup and upscale on the indexer's scheduler and queue — the
  seams were built for it in Phases 6–7, and nothing of it exists yet.
- **Operator surface.** UI-01 (indexer status/history in the app) and DIST-01 (indexer self-update).
- **Installer signing.** Needs a certificate and a key-management decision the project has not made.

## Requirements

### Validated

- ✓ **Windows Indexer Service** — v1.1. `Synapic.Indexer.exe` tags unattended on a schedule, on
  filesystem events or from a Daminion untagged query, writes back to files or the catalog, installs
  and uninstalls from the Windows installer, recovers `InFlight` work after a crash or reboot, and
  matches the desktop kernel's tags on the same fixture. 31/31 requirements; archive at
  [milestones/v1.1-REQUIREMENTS.md](milestones/v1.1-REQUIREMENTS.md).
- ✓ **§5 app-wide settings view** — v1.0 Phase 3 (delivered by `03-04`, verified by `03-05`), with two
  §5 rows recorded ✗ (health details, wide section-nav layout).

### Active

- **v1.0 UI refactor** per `docs/ui-design.md` (SSOT) — dashboard-first, one shared operation layout.
  Phases 1 and 2 complete; **Phase 3 open on §10 criterion 6**, to be closed by v1.2 Phases 11–12.
  - Criterion 6: its "exhaustive" wording is not finitely verifiable; restating it as an inventory plus
    named-test claim is v1.2 phase 11.
  - Criterion 8: **✓** — closed by the 2026-10-13 addendum (37/37 supported or explicitly remapped). The
    "12 partial, 2 unsupported" figure in older planning text predates that addendum.
- **v1.2 Release Integrity** — MSI channel (MSI-01..05), live Daminion (DAM-01..04), gate closure
  (GATE-01..04) and v1.0 release (UAT-01..03).
- **Help corpus naming:** the compiled help still names the old wizard steps
  (`HelpTopics.ForStepIndex` maps 0–5). A docs pass, not a phase.

### Out of Scope

- **Background dedup / upscale (BG-01/02)** — deliberately fenced out of v1.1 ("tagging only"); planned
  for a later milestone rather than abandoned.
- **Remote or central management of the indexer** — the spec describes a local service; no management API.
- **Multi-tenant / per-user queueing** — one machine-wide service account owns the queue.
- **Linux/macOS service hosts** — the service targets Windows 10/11 and Server 2019+; the desktop app
  stays cross-platform.
- **Replacing the Python sidecar or its models** — the sidecar is reused unchanged by both hosts.

## Constraints

- C#/.NET 10, Avalonia 11.1, CommunityToolkit.Mvvm; Python 3.11 sidecar unchanged
- All existing functionality preserved (docs/ui-redesign-proposal.md §4 checklist)
- Headless tests must stay green; layout audit gates every UI change
- `Synapic.Core` is Avalonia-free: a service process must never load the desktop UI stack
- Indexer configuration is schema-versioned and validated at startup; service logs go to the Windows
  Event Log and a rolling file under `%ProgramData%\Synapic\Logs`
- The service must stop gracefully within 10 seconds and leave no orphaned `synapic-inference.exe`
- The indexer must not fork the tagging logic: every inference and metadata call goes through
  `Synapic.Core`, which is what makes parity structural rather than aspirational
- Acceptance evidence is recorded in the gate summary, not reported verbally; a row that was not
  exercised is stated as not exercised

## Key Decisions

| Decision | Rationale | Outcome |
|----------|-----------|---------|
| D-11-01 `Synapic.Core` is a new headless library, not a reference to `Synapic.Main` | A LocalService process must not load the Avalonia stack; duplication would guarantee parity drift | ✓ Good — both hosts build on it, Core's deps list no Avalonia/Main |
| D-11-02 The indexer reuses `ProcessingOrchestrator`, not just the sidecar client | Parity is a property of not forking the download → `/tag` → write path | ✓ Good — live-model parity in the 08-03 run |
| D-11-03 `ISidecarDeviceProvider` replaces the `Session` coupling | Lets a non-desktop host drive the sidecar without UI state | ✓ Good |
| D-11-04 `SynapicLog` moves to Core as the Serilog facade it already is | Both hosts initialize the same facade; the service keeps its own file and Event Log sinks | ✓ Good |
| D-11-05 Configuration is schema-versioned and validated once at startup | A service that silently guesses is worse than one that refuses to start | ✓ Good — exit 2 naming the offending key |
| D-11-06 SQLite queue is the only source of truth for work; startup recovery of `InFlight` | Crash/reboot correctness cannot depend on in-memory state | ✓ Good — 18 `InFlight` rows recovered after `taskkill /F` |
| D-11-07 Daminion credentials use DPAPI at LocalMachine scope | The service account cannot use per-user DPAPI | ⚠ Revisit — any local administrator can decrypt; accepted and documented |
| D-11-08 The Event Log source is created at install time | Creating a source needs administrator rights the service account may not have | ✓ Good |
| D-11-23 `overwriteExistingTags:false` skips fully tagged files and merges only empty fields; keywords are never deleted | Preserves user work while still filling gaps | ⚠ Revisit — `true` means "write the model's values", not "replace the keyword set" |
| D-11-28/29 Packaging publishes the indexer after the app and fails by name on a missing payload file | A silent partial payload produces an installer that installs a broken service | ✓ Good |
| D-11-31 Both installer channels carry the same payload; the Inno EXE registers the service | One implementation of create → recovery → start, reused rather than re-written in the script | ⚠ Revisit — the MSI channel was never built or exercised |

## Context

Shipped v1.1 with roughly 9 600 new lines in `Synapic.Indexer` + its test project and 8 300 lines in
`Synapic.Core` + `Synapic.Core.Tests` (most of them moved out of `Synapic.Main`). Stack unchanged:
.NET 10, Avalonia 11.1, Serilog, Refit, NetVips, Microsoft.Data.Sqlite, Microsoft.Extensions.Hosting.

The milestone's own acceptance run was the most productive test in the project: it found the service
registered as LocalSystem (against the documented LocalService) and a failed start invisible to the
installer — neither reachable by a unit test. Packaging and install now have their own failure modes
recorded in `docs/indexer-operations.md`, and a drift test keeps that document true to
`IndexerConfig`.

Known limitations, stated rather than smoothed over: the MSI channel is unbuilt; Daminion runs against
fake network seams; the desktop GUI was not driven for parity (no headless entry point); a fresh
install ends stopped until `indexer-config.json` exists; the live-kill test is opt-in because it loads
the model twice; and the parallel solution test run is unstable in this environment.

## Links

- `docs/ui-design.md` — single source of truth for UI/UX decisions
- `docs/ui-refactor-plan.md` — evaluation + phase gates the v1.0 roadmap implements
- `docs/windows-indexer.md` — component specification for the Windows Indexer Service
- `docs/indexer-operations.md` — install, configuration reference, logs, failure modes
- `docs/packaging.md` — payload layout, publish order, both installer channels
- `README.md` — migration spec; `plan.md` — original migration phases (historical)
- `.planning/MILESTONES.md` — release history; `.planning/milestones/` — milestone archives

## Milestone History

<details>
<summary>v1.1 Windows Indexer Service (phases 4–8) — shipped 2026-10-23</summary>

**Goal:** Ship `Synapic.Indexer.exe` — an unattended Windows Service that tags images on a
schedule and on filesystem events, writes the tags back to files or a Daminion catalog, and
recovers from crashes and reboots — reusing the same engine the desktop app runs.

**Target features:** the headless `Synapic.Core` engine library; the Windows Service host with
registry + JSON configuration and Serilog Event Log/rolling-file logging with a 10-second shutdown;
work detection (interval scans, debounced `FileSystemWatcher`, Daminion untagged queries) feeding one
durable SQLite queue; the tagging pipeline with field selection, overwrite policy, per-item retry and
permanent-failure states; and deployment — installer integration, service registration with recovery
actions, and an unattended end-to-end acceptance run.

**Scope fence:** tagging only; background dedup and upscaling were deferred to a later milestone.

Full record: [milestones/v1.1-ROADMAP.md](milestones/v1.1-ROADMAP.md).

</details>

---

*Last updated: 2026-10-23 — milestone v1.2 Release Integrity started.*
