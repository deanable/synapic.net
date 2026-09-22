# C# Host Reference (`src/Synapic.Avalonia`, `src/Synapic.Shared`)

Every type in the Avalonia host, what it owns, and how it behaves. For the
system-level picture read [`codebase-guide.md`](codebase-guide.md) first.

Assembly name is **`Synapic`** (`Synapic.Avalonia.csproj` → `AssemblyName`),
output type `WinExe`, targeting `net10.0` with nullable + warnings-as-errors
from `Directory.Build.props`.

---

## Application

### `Program.cs`
`Main` is `[STAThread]`, builds the Avalonia app via `AppBuilder.Configure<App>()
.UsePlatformDetect().WithInterFont().LogToTrace()` and starts a classic desktop
lifetime.

### `App.axaml.cs`
Owns startup wiring and shutdown. Exposes the static DI container
`App.Services`. Responsibilities in order: load config → initialise Serilog →
install crash reporting → runtime check → telemetry → build DI → create the
window → detect/auto-launch the sidecar → stop it on shutdown. See the
[startup sequence](codebase-guide.md#4-startup-sequence-apponframeworkinitializationcompleted).

### `Views/MainWindow.axaml(.cs)`
Shell layout: toolbar (server indicator, Start/Stop Server, Build Server,
download progress), a scrollable content area hosting the current wizard step
via `ContentControl` data templates, navigation bar, and a live **Log** list.
The code-behind attaches the view model's sidecar log stream and
auto-scrolls the log list to the newest entry on every collection change.

### `Views/CrashDialogWindow.axaml(.cs)`
Non-terminal crash dialog with a copy-diagnostics action. Static `Show(report)`
posts on the UI thread.

### `Views/Wizard/*.axaml(.cs)`
One user control per step. Code-behind is intentionally thin:

| View | Code-behind behaviour |
|------|-----------------------|
| `Step1Datasource.axaml.cs` | `OnBrowseFolder` — OS folder picker writes `vm.LocalPath` |
| `Step2Engine.axaml.cs` | bare `InitializeComponent` |
| `Step3Process.axaml.cs` | auto-scrolls the log list to the last line |
| `Step4Results.axaml.cs` | `Refresh` button + auto-scrolls the results grid to the newest row |
| `StepDedup.axaml.cs` | `OnBrowseFolder` — folder picker writes `vm.FolderPath` |

---

## View models (`ViewModels/`)

Built on CommunityToolkit.Mvvm (`[ObservableProperty]`, `[RelayCommand]`).

### `ViewModelBase`
Empty base class (`ObservableObject`).

### `MainWindowViewModel` (singleton)
The shell. Owns:

- `ServerState` (`ServerUiState`: `Detecting`, `NotDetected`, `Building`,
  `Stopped`, `Starting`, `Running`, `Error`) and `ServerBrush` for the status
  dot; `StatusText` is derived from the state.
- `StartServerCommand` / `StopServerCommand` / `BuildServerCommand` with the
  usual `CanExecute` gates.
- The sidecar setup panel: `SidecarVariants` (`SidecarVariantViewModel` per
  buildable RID), `IsSidecarReady`, `IsSidecarRequired`,
  `IsSidecarPanelVisible`, and `IsWorkspaceEnabled` (the whole wizard is inert
  until a sidecar exists).
- `ApplyDownloadStatus(HealthResponse)` — drives the model-download progress
  bar/text from the `/health` `download` field; also emits one log line per
  status transition.
- `AppendLog`, `AttachSidecarLog`, `LogEntries` (ring buffer, capped at 2000).
- `PersistConfig()` — snapshots `Session` into `config.json` (v2 shape).
- `DetectServerAsync()` — locates the executable, refreshes variants, and sets
  the indicator. It never adopts a server from a previous run.
- `PollHealthAsync` — 2-second background poll while Starting/Running; feeds
  the download panel.

Constructed with optional locator delegates (`sidecarExecutableLocator`,
`sidecarVariantLocator`) so tests can inject fake filesystem state.

### `WizardViewModel`
Linear navigation with validation gates. Holds the five step view models;
`CurrentStep` drives `ContentControl`; `CurrentStepIndex`,
`CurrentStepTitle`, `NextButtonText`, and tab-enablement properties are derived.
`IsNavigationLocked` mirrors `Step3.IsRunning`.

Gates: `Session.ValidateForStep2()` (datasource usable) and
`Session.ValidateForStep3(daminionConnected)` (model chosen, ≥1 tag field,
Daminion connected when applicable). Step-entry side effects: Step 2 refreshes
the model list, Step 4 calls `Refresh()`, and reaching Results persists config.
Step 2 settings are persisted when leaving the engine step.

### `SidecarVariantViewModel`
One buildable variant row (CPU / CUDA): `Rid`, `DisplayName`, `Detail`,
`IsBuilt`, `ExePath`, `SizeText`, `IsBuilding`, `BuildPercent`, `BuildStage`,
`HasProgress`, `StatusText`, `StatusBrush`, and a `BuildCommand`. Progress
updates are marshalled to the UI thread.

### `Steps/Step1DatasourceViewModel`
- Datasource type (`local` / `daminion`) with settable radio bindings
  (`IsLocalSelected` / `IsDaminionSelected` — the getter-only booleans are not
  two-way bindable).
- Local: `LocalPath`, `LocalRecursive`, folder browse.
- Daminion: URL/user/password/catalog, `ConnectCommand` (re-evaluated as
  fields change), `Disconnect`, `ConnectedClient`, `IsDaminionConnected`,
  `ConnectionMessage`, `ActiveCatalog` (the catalog GUID the login landed on).
- Scope: `ScopeIndex` → `all` / `search` / `collection` / `saved_search` with
  `SavedSearches` / `Collections` pickers loaded after connect (manual id
  entry always available as a fallback).
- Filters: status (`all`/`approved`/`rejected`/`unassigned`) and untagged
  Keywords/Categories/Description (+ "Select all untagged").
- Processing limits: `MaxItems`, **`ProcessAll`** (ignore the ceiling and page
  until the server returns nothing), `ResizeScale`, `UseThumbnailOverride`.
- `CountCommand` → `GetFilteredItemCountAsync`; `CountText` shows the result or
  a failure hint.
- `ToSelectionForProcessing(client)` builds the orchestrator's
  `DatasourceSelection` (sharing the authenticated client).

Persistence: on a **successful** connect, every field is saved to
`DaminionConnectionStore`; on construction the form is pre-filled from the
store (never auto-connected).

### `Steps/Step2EngineViewModel`
- Local model picker (`LocalModels` from `/models/list`, `SelectedModel`,
  manual `ManualModelId`, `RefreshModelsCommand`, `DownloadSelectedCommand`).
- Task is fixed to `image-text-to-text` (`MultimodalTask`).
- `DeviceIndex` (CPU/CUDA/MPS), `ConfidenceThreshold`,
  `ProbabilityModeIndex` (llm/probability/both), `ProbabilityThreshold`.
- `ProbabilityCandidates` (comma-separated → string[] in the session).
- `SystemPrompt` (VLM system message) and `EmbeddingRescueEnabled`.
- Tag-field checkboxes `TagKeywords` / `TagCategories` / `TagDescription` with
  `HasNoTagFieldSelected` for the validation hint.
- Persists to `EngineSettingsStore` via `SaveToStore()`; `MakeSelectionValid()`
  pushes UI state into the session on navigation.

### `Steps/Step3ProcessViewModel`
Owns a `ProcessingOrchestrator` (4-way parallelism). Exposes
`ProgressPercent`, `ProgressText`, `EtaText`, `CurrentFile`, `IsRunning`,
`IsPaused`, `IsIdle`, and the `LogLines` collection (ring buffer 2000, fed
through the UI dispatcher).

- `StartCommand` builds the `TagRequest` from Step 2 state, creates a linked
  CTS + `PauseTokenSource`, streams `ProcessProgress` into the UI and
  `Session` counters, forwards log lines, records telemetry for the batch.
- `Pause` / `Resume` / `Abort` with the expected `CanExecute` gates.
- ETA text is `ETA ~Xm Ys remaining — ~As Bs per image` (from
  `ProcessProgress.Eta`/`PerItem`), shown from the first completed item and
  cleared when the batch completes.
- `BuildTagRequest()` is reused by Step 4's retry.
- `AppendLog` re-marshals to the UI thread (the orchestrator calls from
  thread-pool threads).

### `Steps/Step4ResultsViewModel`
`Results` (from `Session.Results`), `Summary`, `SelectedResult`, `IsBusy`.
- `Refresh()` repopulates the grid.
- `ExportCsvCommand` writes `synapic_results_<timestamp>.csv` to the user's
  Documents folder.
- `RetryFailedCommand` re-fetches items, keeps the previously failed file
  names, drops their old rows, and re-runs `RunItemsAsync` on the subset.
- `VerifyDaminionCommand` re-reads each Daminion-backed item through
  `VerifyItemMetadataAsync`, marking rows `Verified` and logging mismatches.

### `Steps/StepDedupViewModel`
Folder + algorithm (PHash/DHash/AHash/ColorMoment) + threshold, `ScanCommand`
enumerates supported image extensions and calls `IDedupService`, `Groups`
holds the duplicate groups, and `ApplyCommand` applies the selected
Tag/Move/Delete action to the selected group. Records a dedup telemetry count.

---

## Models (`Models/`)

### `Session` (singleton)
Single source of truth for wizard state:
- `Datasource` (`DatasourceState`) — type, local path/recursive, Daminion
  credentials/scope/search/saved-search/collection id, untagged flags, status
  filter, `MaxItems`, `ProcessAll`, `ResizeScale`,
  `UseThumbnailOverride`; `UntaggedFields()`; `ToSelection(client)`.
- `Engine` (`EngineState`) — model id, task, device, thresholds, probability
  mode/candidates, system prompt, embedding rescue, the three tag-field flags,
  `HasSelectedTagField`, `ToTagFieldSelection()`.
- `Results` (`List<ProcessItemResult>`) plus `TotalItems`/`ProcessedItems`/
  `FailedItems` and `ResetStats()`.
- Validation gates `ValidateForStep2()` / `ValidateForStep3(daminionConnected)`.

---

## Services (`Services/`)

### Sidecar lifecycle & API

**`InferenceSidecarService`** (`IInferenceSidecar`) — the process manager.
- `SidecarStatus { Stopped, Starting, Ready, Error }`, `StatusChanged`,
  `LogReceived` (raw stdout/stderr lines), `CurrentStatus`, `SidecarPort`,
  `IsRunning`.
- `StartAsync`: refuses a duplicate start; resolves the executable; sweeps
  stale `synapic_port_*.txt` files; launches with `--port=0` and
  `SYNAPIC_PORT_FILE` + `HF_HOME`; wires stdout/stderr; assigns the process to
  the Windows kill-on-close job object; reads the port file (validating pid
  liveness); polls `/health` for `ready` (max 120 s).
- `StopAsync`: `POST /shutdown` → wait (default 5 s) → kill tree; deletes its
  port file; always sets status `Stopped`.
- `WatchProcessExit` flips status to `Error` ("Server stopped unexpectedly")
  when a ready/starting process dies on its own.
- Static helpers: `ExeName`, `PreferredRid()`, `BuildableRids()`
  (`win-x64`, `win-x64-cuda` on Windows), `VariantDisplayName`,
  `FindExecutable()` / `FindExecutableForRid()`, `FindRepoRoot()`,
  `ModelsRoot()`.
- `ConfigurePort` **recreates** the `HttpClient`/`InferenceApiClient` because
  `BaseAddress` cannot change after the first request.

**`InferenceApiClient`** — typed HTTP calls (`health`, `models/list`,
`models/download`, `tag`, `config` GET/PUT, `shutdown`), serializing with the
source-generated `SynapicJsonContext`. `/tag` has a 5-minute timeout and
retries once after 3 s on HTTP 503 (model loading). Errors surface as
`InferenceApiException` (status + `detail`).

### Daminion (`Services/Daminion/`)

**`IDaminionApi`** — Refit interface for the endpoints Synapic uses:
`UserManager/Login|Logout`, `MediaItems/Get|GetByIds|GetCount|GetAbsolutePath`,
`ItemData/GetAll|BatchChange|GetDefaultLayout`, `Thumbnail/Get`, `Preview/Get`,
`Download/Get`, `Settings/GetVersion|GetLoggedUser|GetCatalogGuid|GetTags`,
`IndexedTagValues` (+ fallback route), `SharedCollection/GetCollections|GetItems`.
Query parameters are all explicitly supplied where the server routes on a full
parameter set (documented on the interface).

**`DaminionApiClient`** — the behaviour layer (port of `daminion_client.py`):
- Auth with rate limiting; typed exceptions `DaminionException`,
  `DaminionAuthenticationException`, `DaminionNetworkException`,
  `DaminionRateLimitException`.
- `NormalizeBaseUrl`, tag-GUID map (`MappedTagGuidCount`,
  `ExtractLayoutTagPairs`, layout parsing).
- `GetItemsFilteredAsync(scope, savedSearchId, collectionId, searchTerm,
  untaggedFields, statusFilter, maxItems, startIndex)` — returns **at most one
  batch** (≤ `PageSize` = 500) per call; callers paginate. Collection and
  saved-search scopes apply client-side filters after the fetch.
- `GetFilteredItemCountAsync` — with the whole-catalog sanity fallback and the
  keyword-value-id fallback described in code comments; returns `-1` on
  failure.
- `GetSavedSearchesAsync` (enumeration + query-sweep discovery),
  `GetSharedCollectionsAsync`, `GetCatalogGuidAsync`.
- `DownloadThumbnailAsync` / `DownloadPreviewAsync` / `DownloadOriginalAsync`
  (temp files), `GetItemDimensionsAsync`.
- `UpdateItemMetadataAsync` (BatchChange), `RemoveKeywordsAsync`,
  `VerifyItemMetadataAsync` (re-read + compare → `DaminionVerifyResult`).
- `LogoutAsync`.

**`DaminionModels`** — response wrappers tolerant of server-version key
variation (`mediaItems` / `items` / `data`, `count` / `totalCount` / `data`),
`DaminionItem` (+ `Dimensions`), `DaminionSavedSearch`, `DaminionCollection`,
`DaminionVerifyResult`, `DaminionBatchChangeRequest`,
`DaminionTagOperation`.

**`DaminionConnectionStore`** — Windows-registry persistence of the Step 1
form (`HKCU\Software\Synapic\Daminion`), with the password DPAPI-protected
(base64 in a binary value) under the current user. `Load()` / `Save()` /
`Clear()`; a no-op on non-Windows. The record `DaminionConnectionParams`
carries every Step 1 field including `MaxItems`, `ResizeScale`,
`UseThumbnailOverride`, and `ProcessAll`.

### Processing (`Services/Processing/`)

**`ProcessingOrchestrator`** — the batch pipeline.
- `ProcessProgress(Processed, Failed, Total, Percent, Eta, CurrentFile, PerItem)`
  and `EstimateProgress(elapsed, processed, total)` (public static, the ETA
  math: per-item = elapsed/processed, ETA = per-item × remaining; `null` until
  the first item completes).
- `FetchItemsAsync(ds, ct)`:
  - Local: recursive/shallow extension scan (`.jpg .jpeg .png .tif .tiff`).
  - Daminion: one batch per call via `GetItemsFilteredAsync`, advancing
    `startIndex`; stops on an empty batch, when `AutoPaginate` is off, or on a
    partial (<500) page — **except** when `ProcessAll` is set, which ignores
    both the `MaxItems` ceiling and partial pages and keeps requesting until the
    server returns nothing, guarded by an identical-page-id infinite-loop
    check.
- `RunAsync(ds, template, progress, log, ct, results, pause, tagFields)` —
  fetch then `RunItemsAsync`.
- `RunItemsAsync(...)` — `SemaphoreSlim`-bounded per-item tasks; reports
  progress at item start and completion; per-item failures increment the
  failed count and log without aborting the batch; cancellation propagates.
- `ProcessSingleItemAsync` — obtains the image (local path, or Daminion
  thumbnail/preview/original per settings, deleting temp files afterwards),
  calls `/tag`, applies `TagFieldSelection`, and writes metadata
  (`MetadataWriterService` or `UpdateItemMetadataAsync`).
- Supporting types: `ProcessItemResult` (+ `KeywordsCsv`), `ScoringResultDto`,
  `TagFieldSelection`, `DatasourceSelection`, `ProcessWorkItem`.

**`PauseToken` / `PauseTokenSource`** — cooperative pause. Items call
`WaitWhilePausedAsync(ct)` between work units; running items finish, queued
items wait; cancellation wins.

**`DedupService`** (`IDedupService`) — NetVips-backed perceptual hashing
(pHash via a 32×32 DCT, dHash, aHash; ColorMoment currently approximates aHash),
Union-Find grouping with a hamming-distance threshold, and
`ApplyActionsAsync` for Delete / Move (into a `duplicates/` subfolder) / Tag.
`ComputeHash` is public static and returns `null` for unreadable images.
Types: `HashAlgorithm`, `DedupAction`, `DedupOptions`, `DedupProgress`,
`DuplicateGroup`, `DedupResult`.

### Media & metadata

**`MetadataWriterService`** (`IMetadataWriter`) — `ReadAsync` /
`WriteAsync(filePath, TagResult, ct)`. Writes a minimal XMP packet for
JPEG (APP1 byte surgery), PNG (`iTXt`), and TIFF (`.xmp` sidecar); reads XMP
plus IPTC/EXIF fallbacks. `TagResult(Category, Keywords, Description)`. See
[metadata mapping](codebase-guide.md#9-metadata-writing-what-lands-where).

### Configuration, logging, diagnostics

- **`ConfigService`** — loads/saves `AppConfig` (v2): `DatasourceSettings`,
  `EngineSettings`, `ProcessingSettings`, `UiSettings`. Unknown fields survive
  round-trips via `JsonNode` preservation. `AppConfig.DefaultDirectory` /
  `DefaultFilePath` resolve the platform config location.
- **`SynapicLog`** — static Serilog bootstrap. One file sink (Debug+, **file
  deleted at startup**) at `logs/synapic.log` and an in-memory `UiLogSink`
  filtered to the configured UI level. `LogDirectory` falls back to
  `%LOCALAPPDATA%\Synapic\logs` when the app dir is read-only. `For(context)`
  and `Debug/Info/Warning/Error` helpers. `UiLogSink` keeps a capped ring
  buffer and raises `Emitted`.
- **`TelemetryService`** — opt-in local counters (`synapic-usage.json`):
  launches, batches, items/failures, model usage, dedup runs. Disabled by
  default; `Shared` defaults to a disabled no-op so tests never touch disk.
- **`CrashReporterService`** — installs AppDomain / TaskScheduler / thread
  exception handlers, writes one report per crash (with a session-log snapshot)
  under `logs/crashes/` next to the app log (same LocalAppData fallback as the
  logger), prunes to the newest 20 reports, and raises `CrashCaptured`.
  `LastReportPath` and `CrashCount` are exposed for the UI. Nothing is sent
  anywhere.
- **`UpdateCheckService`** — checks the GitHub Releases API for
  `deanable/Synapic.NET` (`https://api.github.com/repos/deanable/Synapic.NET/releases/latest`),
  compares against a semver-ish current version, and returns
  `UpdateCheckResult(UpdateAvailable, LatestVersion, DownloadUrl, Notes)`;
  never throws.
- **`DotNetRuntimeCheckService`** — Windows-only: detects the .NET 10 Desktop
  Runtime, downloads and silently installs it when missing; logs each step.
- **`ProcessJob`** — Windows Job Object wrapper (`KILL_ON_JOB_CLOSE`) so child
  processes die with the app; `AssignChild(process)` is best-effort and never
  throws.
- **`EngineSettingsStore`** — registry persistence for Step 2
  (`HKCU\Software\Synapic\Engine`), no-op off Windows.

### Build pipeline

- **`SidecarBuildService`** (`ISidecarBuildService`) — runs
  `build/build-server.ps1|sh <rid>` with streamed output, de-duplicated
  progress reporting, cancellation (kills the whole process tree), and
  `CanBuild` (repo root + build script present). Rejects malformed RIDs.
- **`SidecarBuildProgressTracker`** — maps raw build output to a stage +
  0-100 percentage across the three bands (fetch Python → install deps →
  PyInstaller packaging, the last driven by bytes written versus the previous
  build's executable size). `SidecarBuildProgress(Percent, Stage)`.

---

## Shared contracts (`src/Synapic.Shared`)

`Contracts/Contracts.cs` — records matching the sidecar protocol exactly
(snake_case via explicit `[JsonPropertyName]`): `HealthResponse` (+
`ModelDownloadProgress`), `ModelInfo`, `DownloadRequest`, `TagRequest` /
`TagOptions`, `TagResponse`, `ScoringResult` / `ScoredKeyword`, `ConfigDto`.

`JsonSerializerContext.cs` — `SynapicJsonContext`, a source-generated
`JsonSerializerContext` for every DTO above (camelCase policy, null-ignoring,
string-number reading). Both processes rely on this being the
single wire definition; changing a contract means updating the DTO, this
context, and the Python Pydantic models, then regenerating
`docs/sidecar-protocol.md` (`python build/generate-protocol-doc.py`, verified in
CI by `--check`) and re-running both test suites.
