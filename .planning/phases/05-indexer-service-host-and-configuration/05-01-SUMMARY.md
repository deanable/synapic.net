---
phase: 05-indexer-service-host-and-configuration
plan: 01
subsystem: indexer
tags: [configuration, registry, json-override, DPAPI, validation, HOST-02, HOST-03]

# Dependency graph
requires:
  - phase: 04-extract-headless-engine-core
    provides: Synapic.Core (device whitelist reused by the validator)
provides:
  - src/Synapic.Indexer — net10.0-windows executable project
  - tests/Synapic.Indexer.Tests — headless test project (no Avalonia, no service host)
  - IndexerConfig (spec §5.2 schema, version 1) + IndexerConfigStore + IndexerConfigValidator
  - DpapiCredentialProtector (LocalMachine scope)
affects: [05-02, 05-03, 05-04, Phase 6 (scan settings), Phase 7 (tagging settings)]

# Tech tracking
tech-stack:
  added: [Microsoft.Extensions.Hosting 10.0.12, Microsoft.Extensions.Hosting.WindowsServices 10.0.12, System.Diagnostics.EventLog 10.0.12, Serilog.Sinks.EventLog 4.0.0, System.Security.Cryptography.ProtectedData 8.0.0]
  patterns:
    - "Two sources, one vocabulary: registry values are named after the JSON paths, so an administrator sets service.logLevel or schedule.intervalMinutes without learning a second schema"
    - "Malformed input is data, not an exception: Load returns (config, sources, errors, warnings) and the caller reports all of it at once"

key-files:
  modified:
    - Synapic.Net.sln
  created:
    - src/Synapic.Indexer/Synapic.Indexer.csproj
    - src/Synapic.Indexer/Configuration/IndexerConfig.cs
    - src/Synapic.Indexer/Configuration/IndexerConfigStore.cs
    - src/Synapic.Indexer/Configuration/IndexerConfigValidator.cs
    - src/Synapic.Indexer/Configuration/DpapiCredentialProtector.cs
    - tests/Synapic.Indexer.Tests/IndexerConfigStoreTests.cs
    - tests/Synapic.Indexer.Tests/IndexerConfigValidatorTests.cs

key-decisions:
  - "Unknown JSON/registry keys are warnings, not errors: SaveDaminionPassword deliberately preserves members a future build adds, and a field that survives a save must not then block startup. A typo still shows up in the log by name"
  - "The validator's watch-path rules are local-only: a Daminion run polls the catalog, so requiring local folders from it would reject a correct configuration"
  - "Registry reads fall back to HKCU only when a test override path is supplied; production reads HKLM and never writes it"

patterns-established:
  - "Validation returns (path, message) pairs and the host prints every one — one restart per typo is not a debugging experience"

requirements-completed: [HOST-02, HOST-03]

# Metrics
duration: ~50min
completed: 2026-10-22
---

# Phase 5 Plan 01: Indexer project, configuration and validation

**The service now has a home, a typed configuration read from registry-then-JSON with every bad value named, and a DPAPI LocalMachine password path — 39 indexer tests green**

## Performance

- **Duration:** ~50 min
- **Tasks:** 3
- **Files changed:** 2 projects added to the solution, 5 source files, 2 test files

## Accomplishments

- **`Synapic.Indexer` exists as a real Windows executable.** [Synapic.Indexer.csproj](src/Synapic.Indexer/Synapic.Indexer.csproj) targets `net10.0-windows`, is an `Exe`, and references `Synapic.Core` + `Synapic.Shared` — no Avalonia anywhere on the path. `Synapic.Indexer.Tests` mirrors the TFM so DPAPI and registry behavior can be tested for real.
- **The schema is the spec's, key for key.** [IndexerConfig.cs](src/Synapic.Indexer/Configuration/IndexerConfig.cs) carries `version`, `service`, `schedule`, `engine`, `datasource.daminion` and `tagging` with §5.2's defaults (interval 15, concurrency 4, idle 30, `IntervalAndWatch`, cpu, thresholds 0.3/0.5, writes true, overwrite false).
- **Precedence is per value, not per file.** [IndexerConfigStore.cs](src/Synapic.Indexer/Configuration/IndexerConfigStore.cs) flattens the registry and the JSON tree into the same dotted-path dictionary, applies registry first and then the file, so overriding `service.maxConcurrentItems` leaves a registry `service.logLevel` in force. Malformed JSON becomes an error naming the file; a wrong type names the setting; unknown keys become warnings.
- **The password is stored the way the decision says.** [DpapiCredentialProtector.cs](src/Synapic.Indexer/Configuration/DpapiCredentialProtector.cs) uses LocalMachine scope, returns a reason instead of throwing, and nothing it does reaches a log call. `SaveDaminionPassword` rewrites only `datasource.daminion.encryptedPassword` through a `JsonNode`, so a field this build does not know survives.
- **Validation is fail-fast and complete.** [IndexerConfigValidator.cs](src/Synapic.Indexer/Configuration/IndexerConfigValidator.cs) enforces version 1, mode/interval, local watch paths (absolute, present, required for watch modes), concurrency 1–32, idle 0–1440, the device whitelist (via Core's normalizer), a non-empty model, both thresholds, probability mode, at least one write flag, the datasource type and every Daminion connection field — each with a dotted path.

## Verification

- `dotnet build Synapic.Net.sln --nologo -v minimal` → **0 warnings, 0 errors**
- `dotnet test tests/Synapic.Indexer.Tests` → **39 passed, 0 failed**
- `grep -n "LocalMachine" src/Synapic.Indexer/Configuration/DpapiCredentialProtector.cs` → the scope constant
- `grep -rn "Console.Write\|SynapicLog" src/Synapic.Indexer/Configuration` → no password value reaches either

## Decisions Made

- Unknown keys are warnings (see above): the alternative contradicts `SaveDaminionPassword`'s preservation promise.
- Watch paths are validated only for a local datasource; a Daminion-only service has no local folders to watch.
- The validator reuses `InferenceSidecarService.NormalizeDevice` rather than duplicating the device whitelist, so a device Core would accept cannot be rejected here.
