# Phase 4: Extract headless engine core - Context

**Gathered:** 2026-10-22
**Status:** Ready for planning
**Source:** `docs/windows-indexer.md` §3 (shared engine core) + direct code read of `src/Synapic.Main/Services`

<domain>
## Phase Boundary

Create `src/Synapic.Core` and move the engine into it so a non-Avalonia process can run the same tagging
pipeline. In scope: the inference transport, the sidecar host, the Serilog facade, the metadata writer, the
Daminion client and its Refit interface/models, and the tagging kernel. Out of scope: any behavior change to
tagging/metadata/Daminion semantics, the UI, dedup, packaging, and the indexer project itself (phase 5).

This phase exists because the spec's "shared engine core" is currently **not shared**: every service lives in
`src/Synapic.Main`, which is the Avalonia executable. `Synapic.Indexer` must not reference it.

</domain>

<decisions>
## Implementation Decisions

### Library shape
- **D-11-01 (locked):** `src/Synapic.Core` is a `net10.0` class library referencing `Synapic.Shared` and no
  Avalonia package, no `Synapic.Main`. Namespaces become `Synapic.Core.Services`,
  `Synapic.Core.Services.Daminion`, `Synapic.Core.Services.Processing`. The namespace rename is deliberate:
  the compiler then enumerates every use site, instead of the move hiding behind `Synapic.Main.Services`.
- **Move list (Core):** `InferenceApiClient`, `IInferenceSidecar` + `InferenceSidecarService` +
  `SidecarStatus`/event args, `SynapicLog` (+ `UiLogSink`, `UiLogEvent`), `IMetadataWriter` +
  `MetadataWriterService` + `TagResult`/`MetadataPatch*`, `DaminionApiClient` + `DaminionModels` +
  `IDaminionApi` + the three Daminion exceptions, `WorkflowRunner` + `WorkflowDefinition` +
  `IWorkflowItemHandler` + `WorkflowItemOutcome`/`WorkflowRunSummary` + `CatalogRetrievalDiagnostics`,
  `ProcessingOrchestrator` + `ProcessItemResult`/`ScoringResultDto`/`ProcessProgress`/`TagFieldSelection`/
  `DatasourceSelection`/`ProcessWorkItem`, `PauseToken`/`PauseTokenSource`, `ProcessStatus`.
- **Stay list (Main):** `Session`, all ViewModels/Views, `ConfigService` (app `config.json` with UI settings),
  `AppSettingsRegistryStore`, `EngineSettingsStore`, `DaminionConnectionStore` (HKCU + DPAPI, desktop-facing),
  `SidecarDownloadService` (download/build UI flows), `HelpService`, `TelemetryService`, `UpdateCheckService`,
  `CrashReporterService`, `ProcessJob`, and the whole dedup subsystem (out of v1.1 scope). `DedupScanHandler`
  stays in Main and implements Core's `IWorkflowItemHandler` — that direction of reference is fine.

### The one coupling that must break
- **D-11-03 (locked):** `InferenceSidecarService` reads `Session.Engine.Device` through `Session?` (see
  `InferenceSidecarService.cs` L107–126). Core introduces `ISidecarDeviceProvider { string? DeviceSelection { get; } }`
  plus a `SidecarLaunchOptions` record carrying an optional executable path override and user sidecar root.
  `Synapic.Main` supplies `SessionSidecarDeviceProvider(Session)`; the default provider is a static one, so
  existing tests that construct `new InferenceSidecarService(httpClient)` keep compiling.
- `SetDeviceAsync(string?)` stays the runtime override and is unchanged — it pushes `PUT /config`, it does not
  write the provider.

### Logging
- **D-11-04 (locked):** `SynapicLog` moves as-is (it is Serilog + a static facade, no Avalonia) and gains an
  optional `initializeSinks` hook (`Action<LoggerConfiguration>?`) so a host can add sinks without forking the
  facade. The desktop keeps its LocalApplicationData file + UI sink; the indexer will add Event Log + its own
  directory. The service never writes the desktop log file.

### Explicitly not decided here
- Whether Core eventually absorbs dedup — no; v1.1 is tagging only and dedup stays in Main.
- Whether `Synapic.Shared` becomes the contracts-only project for Core — it already is (wire DTOs only); it is
  not renamed.

</decisions>

<canonical_refs>
## Canonical References

**Downstream agents MUST read these before planning or implementing.**

### Specification
- `docs/windows-indexer.md` §3.1 — module table and the "shared engine core" claim this phase makes true
- `docs/windows-indexer.md` §1 — the service must run with no desktop session
- `docs/windows-indexer.md` §9.3 — parity requirement (identical model parameters produce identical tags)

### Code (the move itself)
- `src/Synapic.Main/Services/InferenceSidecarService.cs` — 1007 lines; the `Session` coupling is L107–126;
  port-file protocol L519–600; variant/path discovery L210–320
- `src/Synapic.Main/Services/SynapicLog.cs` — static facade, `Initialize` L47, `For/Info/...` L220+
- `src/Synapic.Main/Services/MetadataWriterService.cs` — `IMetadataWriter`, `MetadataPatch`, staging/validation
- `src/Synapic.Main/Services/Daminion/DaminionApiClient.cs` — Refit client, ~2000 lines, paged fetch +
  `UpdateItemMetadataAsync`
- `src/Synapic.Main/Services/Processing/ProcessingOrchestrator.cs` — `RunAsync`/`RunItemsAsync` and the
  `TaggingItemHandler` (download → `/tag` → write metadata)
- `src/Synapic.Main/Services/Processing/WorkflowRunner.cs` — `FetchItemsAsync`, `FetchEntireScopeAsync`,
  concurrency kernel, progress/ETA
- `src/Synapic.Main/App.axaml.cs` L136–167 — DI registrations that must keep working
- `Directory.Build.props` — `net10.0`, `TreatWarningsAsErrors=true`

</canonical_refs>

<code_context>
## Existing Code Insights

### Reusable Assets
- `ProcessingOrchestrator.RunAsync(ds, template, progress, log, ct, results, pause, tagFields)` is already the
  whole per-item pipeline including Daminion write-back — the indexer will call this, not a copy of it.
- `IMetadataWriter.WriteAsync(path, TagResult, ct)` and `MetadataPatch.FromTags` are the write-back seam.
- `IInferenceSidecar` is a complete seam (status, events, start/stop, tag/upscale/models/health, device push).
- The sidecar port protocol is already implemented and matches the spec (`--port=0`, `SYNAPIC_PORT_FILE`,
  `%TEMP%\synapic_port_{pid}.txt`, liveness check, stale sweep). Do not rewrite it.

### Established Patterns
- Test naming is `XTests` + `[Fact]` with assertion sentences; `tests/Synapic.Main.Tests` is Avalonia.Headless
  because it hosts the UI. Core tests must not need the headless harness.
- `TreatWarningsAsErrors=true` means an unused using or a moved-but-not-updated namespace fails the build.
- All app paths are derived, not hard-coded: `%LOCALAPPDATA%` for the sidecar and logs, `%APPDATA%` for
  `config.json`, HKCU for stores. Core must accept roots as parameters instead of owning them.

### Integration Points
- `App.axaml.cs` registers `IInferenceSidecar`, `ISidecarDownloadService`, `Session`, `HelpService`, and the
  window/view models. Only the engine registrations change; `Session` must be registered before the sidecar
  so the device provider adapter can resolve it.
- `Synapic.Main.Tests` compiles against Main; after the move it sees Core transitively. Tests that name moved
  types need `using Synapic.Core.Services...`, nothing more.
- The `Synapic.Avalonia` project exists in `src/` but is not in the solution; do not add it.

</code_context>

<deferred>
## Deferred Ideas

- A `Synapic.Core` configuration abstraction for both hosts (indexer config is phase 5; desktop keeps its own)
- Moving `SidecarDownloadService` into Core (indexer ships a fixed sidecar; download UX is desktop-only)
- Dedup extraction (v2 / BG-01)
- Any change to `MetadataWriterService`'s overwrite behavior — that is phase 7's write-back policy work and
  needs its own tests; this phase keeps write semantics byte-identical

</deferred>

---

*Phase: 04-extract-headless-engine-core*
*Context gathered: 2026-10-22 from the spec + code read*
