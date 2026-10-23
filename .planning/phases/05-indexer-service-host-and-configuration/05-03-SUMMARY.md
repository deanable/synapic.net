---
phase: 05-indexer-service-host-and-configuration
plan: 03
subsystem: indexer
tags: [sidecar, readiness, idle-timeout, orphan-cleanup, HOST-04, HOST-06]

# Dependency graph
requires:
  - phase: 05-indexer-service-host-and-configuration
    provides: 05-02 (host, logging, worker)
  - phase: 04-extract-headless-engine-core
    provides: IInferenceSidecar, SidecarLaunchOptions (ExecutablePathOverride)
provides:
  - SidecarSupervisor (EnsureReadyAsync / EnsureStoppedAsync, IsReady)
  - ISidecarActivity + NoWorkActivity seam for phase 6/7
  - SidecarExecutable locator with the searched-path list for the Fatal message
  - IInferenceSidecar.ProcessId (defaulted) and SidecarLaunchOptions.EnforceSourceStaleness
affects: [Phase 6 (feeds ISidecarActivity), Phase 7 (calls EnsureReadyAsync before a batch)]

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "Readiness is a health answer, not a process handle: EnsureReadyAsync returns only after /health says ready"
    - "A destructive cleanup needs two independent facts: the image name matches and the pid is not the current child"

key-files:
  modified:
    - src/Synapic.Core/Services/InferenceSidecarService.cs
    - src/Synapic.Core/Services/SidecarLaunchOptions.cs
    - src/Synapic.Indexer/Program.cs
    - src/Synapic.Indexer/Hosting/IndexerWorkerService.cs
  created:
    - src/Synapic.Indexer/Hosting/ISidecarSupervisor.cs
    - src/Synapic.Indexer/Hosting/SidecarExecutable.cs
    - src/Synapic.Indexer/Hosting/SidecarSupervisor.cs
    - tests/Synapic.Indexer.Tests/SidecarSupervisorTests.cs

key-decisions:
  - "The supervisor owns policy, Core owns the process: launching, the port-file protocol and shutdown stay in InferenceSidecarService"
  - "The pid record is the child's real id from IInferenceSidecar.ProcessId, not a parse of the port file"
  - "EnforceSourceStaleness defaults to true so the desktop keeps its stale-build block; the service turns it off because it ships its binary and has no rebuild action to offer"

patterns-established:
  - "Idle stop is a pure predicate (ShouldStopForIdle) plus a loop, so the rule is testable without waiting for a real timeout"

requirements-completed: [HOST-04, HOST-06]

# Metrics
duration: ~45min
completed: 2026-10-22
---

# Phase 5 Plan 03: The service owns the sidecar

**The service launches the bundled sidecar, waits for `/health` ready, stops it after the configured idle time, kills only a verified orphan, and stops it within 5 s on shutdown — 53 indexer tests green**

## Performance

- **Duration:** ~45 min
- **Tasks:** 2
- **Files changed:** 4 modified (2 in Core), 3 created, 1 test file

## Accomplishments

- **Discovery has no configuration key.** [SidecarExecutable.cs](src/Synapic.Indexer/Hosting/SidecarExecutable.cs) looks beside `Synapic.Indexer.exe`, then the desktop's per-user sidecar root, then a dev checkout's artifacts; a miss produces a Fatal naming every path tried (T-05-06).
- **Readiness is explicit.** [SidecarSupervisor.cs](src/Synapic.Indexer/Hosting/SidecarSupervisor.cs) starts the sidecar, polls `GetHealthAsync` until `status == "ready"` (120 s budget after Core's own port-file wait), and only then writes the pid record, logs `Sidecar ready on port …` and reports ready.
- **Idle stop is configurable and honest.** `service.idleSidecarTimeoutMinutes` (0 = never) drives a loop over `ISidecarActivity`; pending work or recent activity keeps the sidecar alive, and the next `EnsureReadyAsync` restarts a stopped one.
- **Orphan cleanup checks before it kills.** The recorded pid is terminated only when `ProcessName` is `synapic-inference` **and** it is not this run's child; a mismatch is logged and the record discarded (T-05-07).
- **Shutdown is bounded.** `EnsureStoppedAsync` cancels the idle loop, stops the sidecar with a 5 s budget and deletes the pid record; the worker passes a 5 s token from its `finally`, inside the host's 10 s.

## Verification

- `dotnet build Synapic.Net.sln --nologo -v minimal` → **0 warnings, 0 errors**
- `dotnet test tests/Synapic.Indexer.Tests` → **53 passed, 0 failed** (05-01/02's 44 + 9 here)
- Real console run: sidecar located at `…\Synapic\sidecars\win-x64\synapic-inference.exe`, `/health ready after 1 poll(s)`, `Sidecar ready on port 64204`, pid record written, and killing the host left no `synapic-inference` process
- Real service run: ready on port 50709 and a stop in 4.87 s (recorded in 05-04)

## Issues Encountered

- **Core's stale-build guard refused to launch the service's sidecar.** The desktop blocks an exe older than the sidecar source, which is right in a dev checkout and impossible for a service to act on. `SidecarLaunchOptions.EnforceSourceStaleness` (default true) lets the service opt out; the console run then reported ready.
- **The first pid record survived a hard kill.** That is the scenario D-11-14 exists for; the next start reads it, finds the process gone, and discards the record without touching anything.

## Decisions Made

- The supervisor never launches a process itself: `IInferenceSidecar` already owns the `--port=0` protocol, and a second launcher would be a second source of truth.
- The orphan rule is two facts (name + pid), unit-tested as a pure function rather than by spawning processes in tests.
