---
phase: 05-indexer-service-host-and-configuration
plan: 02
subsystem: indexer
tags: [host, serilog, event-log, windows-service, shutdown, HOST-01, HOST-05, HOST-06]

# Dependency graph
requires:
  - phase: 05-indexer-service-host-and-configuration
    provides: 05-01 (configuration, validation)
  - phase: 04-extract-headless-engine-core
    provides: SynapicLog with its additional-sink hook
provides:
  - Program with fail-fast startup, Windows service and console lifetimes
  - IndexerPaths (ProgramData layout) and IndexerLogging (one file + Event Log at Warning+)
  - IndexerWorkerService with a bounded stop
  - SynapicLog.Initialize(includeDefaultFileSink: false) for hosts that own their file sink
affects: [05-03, 05-04, every later phase's logging]

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "One log pipeline: the facade writes through the host's sinks instead of adding its own file, so a support log is one file"
    - "The level in configuration filters the service's file: here the log IS the support trail, unlike the desktop where the level only filters the UI pane"

key-files:
  modified:
    - src/Synapic.Core/Services/SynapicLog.cs
    - src/Synapic.Indexer/Program.cs
  created:
    - src/Synapic.Indexer/Hosting/IndexerPaths.cs
    - src/Synapic.Indexer/Hosting/IndexerLogging.cs
    - src/Synapic.Indexer/Hosting/IndexerWorkerService.cs
    - tests/Synapic.Indexer.Tests/IndexerLoggingTests.cs
    - tests/Synapic.Indexer.Tests/ShutdownBudgetTests.cs

key-decisions:
  - "Core gained includeDefaultFileSink rather than the indexer writing to synapic.log: the spec names indexer-YYYYMMDD.log, and two files for one process split the evidence"
  - "EventLog.SourceExists is guarded: the framework throws SecurityException when it may not read the Security/State logs, which is exactly the service account's situation. 'Cannot tell' reads as 'not registered'"

patterns-established:
  - "Logging is configured once in Program, before anything that can fail, so a configuration error is itself logged"

requirements-completed: [HOST-01, HOST-05, HOST-06]

# Metrics
duration: ~40min
completed: 2026-10-22
---

# Phase 5 Plan 02: The host — configuration, logging, lifetime, bounded stop

**The indexer now starts like a service: configuration validated before any host exists, one rolling log at `%ProgramData%\Synapic\Logs\indexer-YYYYMMDD.log`, Event Log at Warning+, and a worker that returns inside the 10 s budget**

## Performance

- **Duration:** ~40 min
- **Tasks:** 3
- **Files changed:** 2 modified (one in Core), 3 created, 2 test files

## Accomplishments

- **Startup order is the safety property.** [Program.cs](src/Synapic.Indexer/Program.cs) reads both sources, initializes logging, logs every ignored key as a warning, and only then rejects an invalid configuration — printing each `(path, message)` to stderr and the log and exiting **2** without building a host.
- **One log, in the spec's name.** [IndexerLogging.cs](src/Synapic.Indexer/Hosting/IndexerLogging.cs) writes `indexer-YYYYMMDD.log` daily with 30 files retained and adds the Event Log sink at Warning+ only when the source exists. `SynapicLog` was extended with `includeDefaultFileSink: false`; the desktop's default is unchanged, and the service no longer produces a second `synapic.log`.
- **The service level actually filters.** `service.logLevel` drives the file sink. Without this, the facade's UI switch would have been the only consumer and the setting would have done nothing for the file.
- **Lifetimes.** `AddWindowsService(ServiceName = "SynapicIndexer")` when `--console` is absent, the console lifetime otherwise; `HostOptions.ShutdownTimeout = 10 s`; Microsoft logging providers cleared so Serilog is the only pipeline.
- **The worker starts and stops.** [IndexerWorkerService.cs](src/Synapic.Indexer/Hosting/IndexerWorkerService.cs) logs the effective settings, waits for the stop signal, logs `Indexer service stopped.` and returns.

## Verification

- `dotnet build Synapic.Net.sln --nologo -v minimal` → **0 warnings, 0 errors**
- `dotnet test tests/Synapic.Indexer.Tests` → **44 passed, 0 failed** at this gate (39 from 05-01 + 5 here)
- Console run against `%ProgramData%\Synapic\indexer-config.json` → one file `indexer-20261022.log`, no `synapic.log`; the stale-sidecar guard then stopped the run (fixed in 05-03)
- `ShutdownBudgetTests` → a real `Host` with the worker stops in well under 10 s and the log carries both the started and stopped lines

## Issues Encountered

- **`EventLog.SourceExists` throws `SecurityException` on an ordinary account** ("some or all event logs could not be searched: Security, State"). A service account hits the same wall, so the guard now treats it as absent and the file sink carries on.
- **`service.logLevel` initially filtered only the UI switch.** Caught by the level test; the file sink now uses the parsed level.

## Decisions Made

- Core's facade learned `includeDefaultFileSink` instead of the service adopting `synapic.log`; the spec's file name wins and one process keeps one log.
- The Event Log stays Warning+ (D-11-11): the Application log is shared, and an Information line per batch would be noise.
