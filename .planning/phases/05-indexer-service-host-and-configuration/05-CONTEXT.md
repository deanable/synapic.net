# Phase 5: Indexer service host and configuration - Context

**Gathered:** 2026-10-22
**Status:** Ready for planning
**Source:** `docs/windows-indexer.md` §5, §6 (startup half), §7, §8.1

<domain>
## Phase Boundary

Stand up `src/Synapic.Indexer` as a real Windows Service that starts, reads its configuration, launches and
manages the sidecar, logs to the Event Log and a file, and stops cleanly. It does **not** detect or process any
images — no scan, no watcher, no queue, no tagging. When this phase ends, the service is installed, starts,
reports ready, and stops; work arrives in phase 6.

</domain>

<decisions>
## Implementation Decisions

### Project and target framework
- **D-11-09:** `src/Synapic.Indexer` is a `net10.0-windows` console-style executable (overriding
  Directory.Build.props' `net10.0` locally) so `Microsoft.Extensions.Hosting.WindowsServices` and DPAPI are
  first-class. It references `Synapic.Core` and `Synapic.Shared`. `OutputType=Exe`, `RuntimeIdentifier=win-x64`
  is set by the packaging script, not the project.
- **D-11-10:** The service is a `BackgroundService` (`IndexerWorkerService`); `Program.Main` parses control
  arguments before building the host: `--install`, `--uninstall`, `--console`, `--set-daminion-password`,
  `--status` (read-only queue summary, wired in phase 6). No arguments = normal service start used by the SCM.
  Unknown arguments print usage and exit non-zero — a mistyped flag must never silently become "run as service".

### Configuration (spec §5)
- **D-11-05 (locked):** Two sources, one model. `HKLM\SOFTWARE\Synapic\Indexer` holds machine defaults (values
  written by the installer or an administrator); `%ProgramData%\Synapic\indexer-config.json` overrides it when
  the file exists. `IndexerConfigStore` accepts both paths as constructor overrides so tests never touch HKLM or
  ProgramData. Reading HKLM requires no elevation for a service account.
- **Config schema is the spec's, version 1:** `service` (autoStart, logLevel, maxConcurrentItems,
  idleSidecarTimeoutMinutes), `schedule` (mode, intervalMinutes, watchPaths, watchRecursive), `engine`
  (device, modelId, confidenceThreshold, probabilityMode, probabilityThreshold, userPrompt), `datasource`
  (type, daminion {serverUrl, catalogId, username, encryptedPassword, scope, untaggedFields}), `tagging`
  (writeCategory, writeKeywords, writeDescription, overwriteExistingTags). No extra knobs until a requirement
  needs one.
- **Validation is fail-fast and specific:** `IndexerConfigValidator` returns a list of `(path, message)` errors;
  startup logs every one at Fatal and exits 1. Rules: `version == 1`; mode ∈ {Interval, Watch, IntervalAndWatch};
  interval 1–1440; watch mode requires ≥1 existing absolute path; `service` concurrency 1–32; idle timeout
  0–1440 (0 = never stop); `engine.device` ∈ {cpu, cuda, mps} (normalized); modelId non-empty; thresholds 0–1;
  probabilityMode ∈ {both, threshold}; at least one `tagging.write*` true; `datasource.type` ∈ {local, daminion};
  daminion requires serverUrl/username/encryptedPassword and untaggedFields non-empty; local requires ≥1 watch
  path or interval mode.
- **D-11-07 (locked):** `--set-daminion-password` prompts for the password (no echo), encrypts it with DPAPI
  **LocalMachine** scope, and writes `datasource.daminion.encryptedPassword` into the ProgramData JSON while
  preserving every other field. The service decrypts with LocalMachine at startup. The password never appears
  in a log, an argument list, or the registry.

### Host and logging
- **D-11-11:** `AppSettings`-style fluent Serilog built once in `Program`: Event Log sink (source
  `SynapicIndexer`, restricted to Warning+ to avoid flooding the Application log) and a daily rolling file sink
  at `%ProgramData%\Synapic\Logs\indexer-YYYYMMDD.log`, `retainedFileCountLimit: 30`, level from config. The
  file sink gets Information+; both share the same enrichers. `SynapicLog` remains the in-process facade, with
  its additional-sink hook from phase 4.
- **D-11-08 (locked):** The Event Log source is created by `--install` (and by the installer in phase 8), never
  by the running service. Startup checks `EventLog.SourceExists` and logs a Warning (file-only) if absent,
  instead of throwing.
- **D-11-12:** `HostOptions.ShutdownTimeout = TimeSpan.FromSeconds(10)`. The worker cancels its token, waits for
  the sidecar stop with a 5-second budget, then returns. `Program` uses `RunAsync` with a linked token and
  records exit code 0 for a requested stop.

### Sidecar lifecycle
- **D-11-13:** `SidecarSupervisor` owns the `IInferenceSidecar` instance. Path resolution order:
  `synapic-inference.exe` beside `Synapic.Indexer.exe` → the user sidecar root the desktop uses. Missing exe is
  a Fatal startup error naming the searched paths. The executable path is not a config key in v1.1 — a service
  should not be pointable at an arbitrary binary by JSON.
  Launch passes `--port=0` and the device; readiness is `GetHealthAsync` until `status == "ready"` within 120 s.
- **D-11-14:** Orphan safety: on startup the supervisor reads the previous PID record under
  `%ProgramData%\Synapic\state\sidecar.pid`, and terminates that process **only** after confirming its image
  name is `synapic-inference.exe` and it is not the current child. The existing port-file protocol stays the
  source of truth for the port; no new IPC is invented.

</decisions>

<canonical_refs>
## Canonical References

**Downstream agents MUST read these before planning or implementing.**

### Specification
- `docs/windows-indexer.md` §5.1–5.2 — registry path, JSON override, exact schema
- `docs/windows-indexer.md` §6 — startup sequence (read config → init state → spawn sidecar → poll health)
- `docs/windows-indexer.md` §7 — service account, `sc.exe create/failure/start`
- `docs/windows-indexer.md` §8.1 — Event Log source `SynapicIndexer`, file path, 30-day retention
- `docs/windows-indexer.md` §9 — acceptance criteria 1 (registration), 4 (clean shutdown)

### Code
- `src/Synapic.Main/Services/InferenceSidecarService.cs` — the launch/health/port-file behavior being reused
  (L210–320 discovery, L438–600 start, L621–740 port read)
- `src/Synapic.Core/Services/SynapicLog.cs` — `Initialize` and the additional-sink hook (phase 4)
- `src/Synapic.Main/Services/EngineSettingsStore.cs` — the registry-with-override pattern to copy for tests
- `src/Synapic.Main/App.axaml.cs` — DI/lifetime patterns the host mirrors
- `build/package-windows.ps1`, `build/package-windows-msi.ps1`, `build/wix/Synapic.Installer.wixproj` —
  where the payload lands in phase 8

</canonical_refs>

<code_context>
## Existing Code Insights

### Reusable Assets
- `IInferenceSidecar` is complete: StartAsync/StopAsync/GetHealthAsync/SetDeviceAsync + status events.
- `SynapicLog` + `For(context)` is the logging facade every Core service already calls; the host initializes it.
- `EngineSettingsStore` proves the "real path by default, override in tests" pattern for registry reads.

### Established Patterns
- Configuration in this codebase is plain POCO + explicit store + defaults, no `IOptions` binder. Follow that.
- Windows-only code paths return no-op/null on other OSes (`EngineSettingsStore`, `ComputeDeviceProbe`). The
  indexer is Windows-only by TFM, so it does not need that dance internally, but tests must stay runnable on the
  Windows dev box used by this repo.
- `TreatWarningsAsErrors=true`: nullable annotations and unused usings are build failures.

### Integration Points
- Phase 6 injects the scheduler/watcher into this host through `IHostedService`s.
- Phase 7 injects the batch executor; the supervisor exposes `EnsureReadyAsync`/`EnsureStoppedAsync` for it and
  the idle timer asks the executor "any work?" (interface defined here, implemented there).
- Installer (phase 8) calls the same `--install` path rather than duplicating `sc.exe` logic.

</code_context>

<deferred>
## Deferred Ideas

- A tray app or desktop control surface for the service (v2/UI-01)
- Remote configuration or a REST admin endpoint (out of scope)
- Self-update (DIST-01)
- Certificate-based or gMSA credential storage — DPAPI LocalMachine is the v1.1 decision; a future milestone can
  add a pluggable credential provider without changing the config schema version
- Multiple catalogs/servers in one config (single datasource in v1.1)

</deferred>

---

*Phase: 05-indexer-service-host-and-configuration*
*Context gathered: 2026-10-22*
