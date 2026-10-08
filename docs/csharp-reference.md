# C# Host Reference (`src/Synapic.Main`, `src/Synapic.Shared`)

Every type in the Avalonia host, what it owns, and how it behaves. For the
system-level picture read [`codebase-guide.md`](codebase-guide.md) first.

Assembly name is **`Synapic`** (`Synapic.Main.csproj` → `AssemblyName`),
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
download progress), a **start screen** with the **source panel**
(`DatasourceSourcePanel` — folder picker, Daminion connect, scope, filters and the
record count — bound to `Wizard.Step1`, the same view model every route runs on)
and three route cards (`Tagging` / `Deduplication` / `Upscaling`, bound to
`StartTaggingRouteCommand` / `StartDedupRouteCommand` / `StartUpscaleRouteCommand`,
visible while `IsHomeVisible` and gated on **`CanStartRoute`** = the source panel
has a usable source: `Step1.HasUsableSource` — a folder that exists, or a live
Daminion session), a navigation bar whose tabs follow the active
route (`Wizard.ShowTaggingTabs`, `Wizard.ShowDatasourceTab`, route title, `⌂
Home` button), a scrollable content area hosting the current wizard step
via `ContentControl` data templates, and a live **Log** list.
The code-behind attaches the view model's sidecar log stream and
auto-scrolls the log list to the newest entry on every collection change.

### `Views/CrashDialogWindow.axaml(.cs)`
Non-terminal crash dialog with a copy-diagnostics action. Static `Show(report)`
posts on the UI thread. The file also holds `ProcessExtensions`, the small
shell helper (`OpenUrl`, `OpenDirectory`) the dialogs share.

### `Views/RuntimeDialogWindow.axaml(.cs)`
The other startup-failure dialog: it carries the official .NET 10 download page
`DotNetRuntimeCheckService` handed over, with actions to open it in the default
browser, copy it, or continue. Static `Show(result)` is posted on the UI thread
by `App`'s `RuntimeUnavailable` subscription; a refused browser launch is
logged, never thrown.

### `Views/Wizard/*.axaml(.cs)`
One user control per step. Code-behind is intentionally thin:

| View | Code-behind behaviour |
|------|-----------------------|
| `Step1Datasource.axaml.cs` | bare `InitializeComponent`; step 1 — Source & model: the source summary (`SourceStatusStrip`), the model picker and the processing limits |
| `DatasourceSourcePanel.axaml.cs` | `OnBrowseFolder` — OS folder picker writes `vm.LocalPath`; hosts `SourceStatusStrip` |
| `SourceStatusStrip.axaml.cs` | the two source lights (Daminion / folder), shared by the header, the start screen and Step 1 |
| `EngineModelPicker.axaml.cs` | the compact model + device picker (step 1 and the start screen), over the engine view model |
| `Step2TagSettings.axaml.cs` | bare `InitializeComponent`; 3 — Settings as a summary, with the tag-field gates |
| `Step2Engine.axaml.cs` | the full tagging settings form, hosted by `EngineSettingsDialog`; prompt-preset Delete handling |
| `DedupSettingsPanel.axaml.cs` | the dedup scan rules, hosted by `DedupSettingsDialog` |
| `UpscaleSettingsPanel.axaml.cs` | the upscale parameters, hosted by `UpscaleSettingsDialog` |
| `Step3Process.axaml.cs` | auto-scrolls the log list to the last line |
| `Step4Results.axaml.cs` | `Refresh` button + auto-scrolls the results grid to the newest row |
| `StepDedup.axaml.cs` | `OnBrowseFolder` — folder picker writes `vm.FolderPath`; wires `vm.ConfirmAction` to `ConfirmDialogWindow.ShowAsync` for the catalog delete |
| `StepUpscale.axaml.cs` | auto-scrolls the run log list to the last line |

---

## View models (`ViewModels/`)

Built on CommunityToolkit.Mvvm (`[ObservableProperty]`, `[RelayCommand]`).

### `ViewModelBase`
Empty base class (`ObservableObject`).

### `MainWindowViewModel` (singleton)
The shell. Owns:

- **Routes** — `Route` (`HomeRoute` / `TaggingRoute` / `DedupRoute` /
  `UpscaleRoute`) with
  `IsHomeVisible`, `IsWizardVisible`, `IsTaggingRoute`, `IsDedupRoute`,
  `IsUpscaleRoute`,
  `RouteTitle`, and the four commands `StartTaggingRouteCommand`,
  `StartDedupRouteCommand`, `StartUpscaleRouteCommand`, `GoHomeCommand`. The app
  opens on `HomeRoute` (the chooser); entering a route delegates to
  `Wizard.EnterTaggingRoute()` /
  `EnterDedupRoute()` / `EnterUpscaleRoute()` and logs the choice.
- `ServerState` (`ServerUiState`: `Detecting`, `NotDetected`, `Building`,
  `Stopped`, `Starting`, `Running`, `Error`) and `ServerBrush` for the status
  dot; `StatusText` is derived from the state.
- `StartServerCommand` / `StopServerCommand` / `BuildServerCommand` with the
  usual `CanExecute` gates.
- The sidecar setup panel: `SidecarVariants` (`SidecarVariantViewModel` per
  buildable RID), `IsSidecarReady`, `IsSidecarRequired`,
  `IsSidecarPanelVisible`, and `IsWorkspaceEnabled` (the whole wizard is inert
  until a sidecar exists). `IsSidecarPanelVisible` also stays true while a
  built variant is outdated - stale, or with a newer prebuilt on GitHub - so
  the panel that offers the **Build**/**Download** pair cannot hide itself
  exactly when a newer sidecar exists. `DownloadVariantAsync` stops a running
  server first (a locked exe can be neither rebuilt nor overwritten) and starts
  it again on the replacement, as does a build.
- `ApplyDownloadStatus(HealthResponse)` — drives the model-download progress
  bar/text from the `/health` `download` field; also emits one log line per
  status transition.
- `AppendLog`, `AttachSidecarLog`, `LogEntries` (ring buffer, capped at 2000).
- `PersistConfig()` — snapshots `Session` into `config.json` (v2 shape).
- `DetectServerAsync()` — locates the executable, refreshes variants, and sets
  the indicator. It never adopts a server from a previous run.
- `CheckForSidecarUpdatesAsync()` — the always-on GitHub check, run by `App`
  right after detection: for every buildable variant, is there a prebuilt file
  on GitHub newer than the executable on disk (or no executable at all)? Sets
  the row's `GitHubUpdateText` / `IsGitHubUpdate`, which flips an outdated row
  to the **Build**/**Download** pair (and reopens the panel) so the prebuilt is
  offered instead of a local build; it logs one line per offer, and the offer
  is cleared once the variant is built or downloaded. Advisory by
  design: failures log and return, nothing blocks startup, and only the real
  desktop launch reaches it (headless test sessions never do).
- `OpenSettingsCommand` — **3 · Settings**: opens the dialog that owns the
  settings of the operation on screen (tagging's engine dialog, or the dedup /
  upscale one) over that operation's own view model. `CreateSettingsDialog()` is
  the public factory behind it, so the choice is testable without a desktop
  lifetime. The header, the sidebar entry and the action bar all run this one
  command.
- Help: `OpenHelpCommand` (the toolbar **Help** button) opens
  `HelpTopics.Home`; `OpenContextHelpCommand` (<kbd>F1</kbd>, bound in
  `MainWindow.axaml`) opens `ContextHelpTopic` — the sidecar topic while
  `IsSidecarRequired`, otherwise the topic for `Wizard.CurrentStepIndex`.
- `PollHealthAsync` — 2-second background poll while Starting/Running; feeds
  the download panel.

Constructed with optional locator delegates (`sidecarExecutableLocator`,
`sidecarVariantLocator`) and an optional `IHelpService` so tests can inject fake
filesystem state.

### `WizardViewModel`
Linear navigation with validation gates. Holds the six step view models;
`CurrentStep` drives `ContentControl`; `CurrentStepIndex`,
`CurrentStepTitle`, `NextButtonText`, and tab-enablement properties are derived.
`IsNavigationLocked` mirrors `Step3.IsRunning || Upscale.IsRunning`.

**The workflow as a sequence**: step 1 is *Source & model* (source summary,
model picker, limits), the settings page is *3 · Settings* (a summary; the form
is the operation's dialog), and the run steps follow — *4 · Process*, *5 ·
Results* for tagging, `4` for the dedup and upscale operations. Step `2 ·
Operation type` is the start screen, which the shell owns, so the wizard skips
that number. `Wizard.Step1.Engine` carries the engine view model so the model
picker on step 1 edits the same state as the settings dialog.

**Route split**: `IsDedupRoute` / `IsUpscaleRoute` flip the wizard between the
tagging flow and the two shorter ones. In the dedup/upscale routes
`ShowTaggingTabs` hides Engine/Process/Results, `ShowDatasourceTab` keeps step 1
on screen, `Next` from step 1 validates the datasource and lands on `Dedup`
(after `PrefillDedupSource()` copies the Step 1 folder/scope onto the dedup
step) or on `Upscale` (which reads its source straight off Step 1), and
`GoToDedupCommand` / `GoToUpscaleCommand` apply the same gate — so both tools
are reachable from the onset instead of being the last tab of a wizard.
`ShowDedupTab`/`ShowUpscaleTab` hide each other's tab while a route owns the
wizard (both stay visible in the tagging route, where `CanGoToDedupTab` /
`CanGoToUpscaleTab` only open at Results).

Gates: `Session.ValidateForStep2()` (datasource usable) and
`Session.ValidateForStep3(daminionConnected)` (model chosen, ≥1 tag field,
Daminion connected when applicable). Step-entry side effects: Step 2 refreshes
the model list, Step 4 calls `Refresh()`, and reaching Results persists config.
Step 2 settings are persisted when leaving the engine step.

### `SidecarVariantViewModel`
One buildable variant row (CPU / CUDA): `Rid`, `DisplayName`, `Detail`,
`IsBuilt`, `ExePath`, `SizeText`, `IsBuilding`, `IsDownloading`,
`BuildPercent`, `BuildStage`, `HasProgress`, `StatusText`, `StatusBrush`,
`StaleNotice` / `IsStale` / `StaleText`, `GitHubUpdateText` / `IsGitHubUpdate`
(the startup GitHub check's "Download instead of building" note), and two
commands. Progress updates are marshalled to the UI thread.

- `BuildCommand` / `DownloadCommand` — the pair shown while the variant is
  missing or outdated (`IsMissingOrOutdated`): compile it, or take the prebuilt
  exe from the latest release instead. On an outdated variant Download replaces
  the executable on disk (stop → fetch → restart, like a rebuild).
- `IsUpToDateVisible` — the no-action state: built and current, rendered as a
  disabled **Up to date** button. There is deliberately no update command: a
  row with nothing to fetch or rebuild offers no enabled action at all.

`IsBuildButtonVisible` / `IsDownloadButtonVisible` / `IsUpToDateVisible`
are the visibility gates the axaml binds: Build+Download together for a
missing or outdated row, the disabled marker for a current one - never both
at once, and all three hidden while the row itself runs an operation (one
shared progress bar). `ApplyBuildState(exePath, staleNotice)` applies
detection, and `CanBuild` / `CanDownload` are false for every row while any
operation runs (one progress bar is shared by both).

### `Steps/Step1DatasourceViewModel`
- Datasource type (`local` / `daminion`) with settable radio bindings
  (`IsLocalSelected` / `IsDaminionSelected` — the getter-only booleans are not
  two-way bindable).
- Local: `LocalPath`, `LocalRecursive`, folder browse.
- Daminion: URL/user/password, `ConnectCommand` (re-evaluated as fields
  change), `Disconnect`, `ConnectedClient`, `IsDaminionConnected`,
  `ConnectionMessage`, `ActiveCatalog` (the catalog GUID the login landed on —
  the catalog is chosen by the server URL, so it is reported rather than
  entered).
- Scope: `ScopeIndex` → `all` / `search` / `collection` / `saved_search` with
  `SavedSearches` / `Collections` pickers loaded after connect (manual id
  entry always available as a fallback).
- Filters: status (`all`/`approved`/`rejected`/`unassigned`) and untagged
  Keywords/Categories/Description (+ "Select all untagged").
- Processing limits: `MaxItems`, **`ProcessAll`** (ignore the ceiling and page
  until the server returns nothing), `ResizeScale`, `UseThumbnailOverride`.
- Source readiness (the start screen's gate): `LocalFolderExists`,
  `HasUsableSource`, `DaminionSourceBrush` / `LocalSourceBrush` and
  `DaminionSourceText` / `LocalSourceText` — the two source lights.
- Record count (automatic): `RefreshCountAsync` counts `WorkflowRunner`'s
  `CountLocalImages` for a folder or `GetFilteredItemCountAsync` for the catalog;
  `ScheduleCountRefresh` debounces every scope/filter/folder change onto it,
  `IsCounting` drives the spinner and `CountText` shows the result or a failure
  hint. `CountCommand` ("Recount") is only a manual re-ask.
- `InitializeAsync` — the launch sequence `App` runs: reconnect when the stored
  source is Daminion (`ConnectCoreAsync`, shared with the Connect button), then
  count once.
- `ApplyStoredSource` (on `DatasourceState`) restores the folder/type from
  `config.json` at startup, so the start screen has a source before the user
  touches anything.
- `ToSelectionForProcessing(client)` builds the orchestrator's
  `DatasourceSelection` (sharing the authenticated client).

Persistence: on a **successful** connect, every field is saved to
`DaminionConnectionStore`; on construction the form is pre-filled from the
store, and `InitializeAsync` reconnects to it once at launch (a failed login
just leaves the form filled in).

### `Steps/Step2EngineViewModel`
- Local model picker (`LocalModels` from `/models/list`, `SelectedModel`,
  manual `ManualModelId`, `RefreshModelsCommand`, `DownloadSelectedCommand`).
- Task is fixed to `image-text-to-text` (`MultimodalTask`).
- `DeviceIndex` (CPU/CUDA/MPS), `ConfidenceThreshold`,
  `ProbabilityModeIndex` (llm/probability/both), `ProbabilityThreshold`.
- `ProbabilityCandidates` (comma-separated → string[] in the session).
- `SystemPrompt` (VLM system message) and `EmbeddingRescueEnabled`.
- System-prompt presets: `SystemPromptPresets` (newest first),
  `SelectedSystemPromptPreset` and `SystemPromptMessage`.
  `RememberSystemPrompt()` is the commit point (leaving the box or Step 2) and
  `DeleteSystemPromptPresetCommand` removes the highlighted entry, clearing the
  box when that prompt was the one in use.
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
Algorithm (PHash/DHash/AHash/ColorMoment) + threshold over two sources — the
same radio pattern as Step 1: `IsLocalSelected`/`IsDaminionSelected` pick a
local `FolderPath` or the Step 1 Daminion scope (`DaminionScopeSummary`, fed by
the injected `Step1DatasourceViewModel` + sidecar). `ScanCommand` enumerates
supported extensions (local) or fetches Daminion items and groups them: items 
with a server `HashCode` are grouped without download; items without fall back 
to downloading each original, hashing it, and deleting the temp file. The Server
hash picker (Daminion only) decides how those server hashes are compared —
Exact (identical values only, the default) or Hamming at the threshold. Produces 
`Groups` — a vertical list of
`DuplicateGroupViewModel` cards, each holding `DedupItemViewModel` rows with an
`IsChecked` keep checkbox (checked = kept; unchecked = action target). The
`SelectOldest`/`SelectNewest`/`SelectSmallest`/`SelectLargest` auto-select
switches recompute every group's checkboxes as the union of their picks
(keep-first when none is active; unknown dates/sizes are never picked).
After grouping, `LoadThumbsAsync` decodes a preview bitmap per grouped item
(local files via `DedupService.CreateThumbnail`, Daminion items via
`DownloadThumbnailAsync` with the temp file deleted after reading — in-memory
only, never cached) and hands them to the rows; previews are disposed when a
rescan or an applied action removes their groups. Records
a dedup telemetry count. `ApplyCommand` acts on the unchecked items:
Tag/Move/Delete for a local source (Delete gated by the same `ConfirmAction`
prompt), or — after that modal (null = fail closed) —
`DaminionApiClient.DeleteItemsAsync` for the catalog. Scan and apply are fully
instrumented for post-mortem debugging: one line per scan with source/scope,
algorithm and threshold, one per skipped item (download failure, unhashable
file) with the reason, a hashed/skipped tally, and one line per apply with the
ids about to be removed plus the server's answer.

### `Steps/StepUpscaleViewModel`
The upscale step (port of `step_upscale.py`) — the Daminion "Feature
enhancement" utility. Settings mirror the original app's dropdowns:
`Workflows` (`quality`/`balanced`/`fast`), `Factors` (2x/4x), `Precisions`
(auto/fp16/fp32), `OutputFormats` (keep/JPEG/PNG/WEBP), `JpegQuality` (default
95), `DenoiseStrength` (0–1, `IsDenoiseEnabled` only for balanced),
`SharpenAmount` (0–2), `OverwriteExisting`. `BuildOptions()` clamps every value
to the sidecar contract's ranges and maps the dropdown indices to wire values
(factor index → 2/4).

The source is read back from Step 1 (`SourceReady` / `SourceSummary` via
`ToSelectionForProcessing` + `ScopeDescription` — no prefill, unlike dedup), so
both the Daminion scope and a local folder work. `StartCommand` echoes the
original app's `Parameters:` log line, then runs the shared `WorkflowRunner`
(key `upscale`, parallelism 1) with an `UpscaleItemHandler`; `StopCommand`
cancels the token (in-flight request cancelled, no further items, the handler
undoes a checkout so nothing is left locked). Progress/ETA reuse
`ProcessProgress` + `ProcessProgress.FormatDuration`, and `AppendLog`
marshals to the UI thread (runner callbacks arrive on thread-pool threads).

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
- `StartAsync`: refuses a duplicate start; resolves the executable *for the
  selected device* (`FindExecutableForDevice`: the CUDA build when `cuda` is
  selected and one is on disk, the CPU build otherwise, warning when CUDA was
  asked for and only a CPU build exists); sweeps
  stale `synapic_port_*.txt` files; launches with `--port=0` and
  `SYNAPIC_PORT_FILE` + `HF_HOME` + `SYNAPIC_DEVICE`; wires stdout/stderr; assigns the process to
  the Windows kill-on-close job object; reads the port file (validating pid
  liveness); pushes the device with `PUT /config` (bounded at 5 s, non-fatal) so
  a sidecar that predates `SYNAPIC_DEVICE` is configured as well; polls
  `/health` for `ready` (max 120 s).
- `SetDeviceAsync(device)`: `PUT /config` on a running server — how a device
  changed while the server runs reaches the pipeline `/tag` builds. The device
  lives in the sidecar's session config; it is not a `/tag` field.
- `StopAsync`: `POST /shutdown` → wait (default 5 s) → kill tree; deletes its
  port file; always sets status `Stopped`.
- `WatchProcessExit` flips status to `Error` ("Server stopped unexpectedly")
  when a ready/starting process dies on its own.
- Static helpers: `ExeName`, `PreferredRid()`, `BuildableRids()`
  (`win-x64`, `win-x64-cuda` on Windows), `VariantDisplayName`,
  `NormalizeDevice()`, `VariantPreferenceForDevice()`,
  `FindExecutable()` / `FindExecutableForRid()` / `FindExecutableForDevice()`,
  `FindRepoRoot()`,
  `ModelsRoot()`, and `DescribeStaleness(exe, repoRoot)` — compares the exe's
  timestamp with the newest file under `src/Synapic.Inference` (dev checkouts
  only) and returns a description when the binary predates the source. The
  launch path stores it in `StaleBuildNotice` (interface default `null`), which
  the status bar appends as "stale build, rebuild recommended".
- `ConfigurePort` **recreates** the `HttpClient`/`InferenceApiClient` because
  `BaseAddress` cannot change after the first request.

**`InferenceApiClient`** — typed HTTP calls (`health`, `models/list`,
`models/download`, `tag`, `upscale`, `config` GET/PUT, `shutdown`), serializing
with
the source-generated `SynapicJsonContext`. `/tag` has a 5-minute timeout and
retries once after 3 s on HTTP 503 — which the server now reserves for a load
that outlasted its 240 s wait (an in-flight load is waited out server-side).
`/upscale` has a 30-minute timeout and **no** 503 retry (the model loads
inline, and replaying an upscale would double the work). `IInferenceSidecar`
carries `UpscaleAsync` as a **default interface method** that throws
`NotSupportedException`, so every existing fake stays source-compatible;
`InferenceSidecarService` overrides it by delegating to the Api client. Errors
surface as `InferenceApiException` (status + `detail`).

### Daminion (`Services/Daminion/`)

**`IDaminionApi`** — Refit interface for the endpoints Synapic uses:
`UserManager/Login|Logout`, `MediaItems/Get|GetByIds|GetCount|GetAbsolutePath|Remove`,
`ItemData/GetAll|BatchChange|GetDefaultLayout`, `Thumbnail/Get`, `Preview/Get`,
`Download/Get`, `Settings/GetVersion|GetLoggedUser|GetCatalogGuid|GetTags`,
`IndexedTagValues` (+ fallback route), `SharedCollection/GetCollections|GetItems`,
`VersionControl/CheckOut|UndoCheckOut|CheckIn` (port of `daminion_api.py`
`VersionControlAPI`: CheckOut/UndoCheckOut take `{"Ids":[…]}`, CheckIn is
multipart `id` + optional `comment` + `file`; `HttpResponseMessage` return
types keep Refit from throwing on non-2xx so the caller decides).
Query parameters are all explicitly supplied where the server routes on a full
parameter set (documented on the interface).

**`DaminionApiClient`** — the behaviour layer (port of `daminion_client.py`):
- Auth with rate limiting; typed exceptions `DaminionException`,
  `DaminionAuthenticationException`, `DaminionNetworkException`,
  `DaminionRateLimitException`.
- `NormalizeBaseUrl`, tag-GUID map (`MappedTagGuidCount`,
  `ExtractLayoutTagPairs`, layout parsing), and `ExtractCatalogGuid` — accepts
  a bare GUID string, a well-known object key, or the Daminion 11 envelope
  `{"data":"<guid>",…}` that used to warn "no usable value" on every connect.
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
  (temp files), `GetItemDimensionsAsync`. Downloads stream into
  `TempDirectory` (default `%TEMP%\synapic_daminion`, constructor-overridable —
  there is no image cache anywhere) and a partial file is deleted on failure.
  `CleanupStaleDownloads()` sweeps leftovers older than `StaleDownloadAge`
  (1 h, deliberately above the 15-min HttpClient timeout so an in-flight
  download is never removed) — called from `App` startup and from the dedup
  scan, which then logs `TempDirectoryUsage()` so a leak is visible; both are
  pinned by `DaminionTempDownloadTests`.
- `UpdateItemMetadataAsync` (BatchChange), `RemoveKeywordsAsync`,
  `VerifyItemMetadataAsync` (re-read + compare → `DaminionVerifyResult`).
- `DeleteItemsAsync(ids)` — POST `MediaItems/Remove` with
  `{ids, delete:false}` (daminion_api.py `delete_items`), then **verifies** with
  `GetByIds` (one 750 ms settle retry) and only reports success when the ids are
  actually gone. It previously posted `delete:true` to `ItemData/BatchChange`,
  which answers `success:true` without removing anything — the bug behind "dedup
  delete did nothing". Logs the request ids and the response envelope.
- Version control for the upscale flow: `CheckOutItemsAsync(ids)` /
  `UndoCheckOutItemsAsync(ids)` (POST `{"Ids":[…]}`, `false` when the server
  refuses so the item is skipped/rolled back) and
  `CheckInItemAsync(id, filePath, message)` — multipart with the comment first,
  then one retry **without** the comment (some server versions reject the
  field), reopening the file stream per attempt because session recovery
  replays the whole send. Returns `false` when the upload is refused; the
  caller then undoes the checkout so nothing is left locked.
- `LogoutAsync`.

**`DaminionModels`** — response wrappers tolerant of server-version key
variation (`mediaItems` / `items` / `data`, `count` / `totalCount` / `data`),
`DaminionItem` (+ `Dimensions`, + `HashCode`, + `FileSize`), `DaminionSavedSearch`, 
`DaminionCollection`,
`DaminionVerifyResult`, `DaminionBatchChangeRequest`,
`DaminionTagOperation`, `DaminionRemoveRequest` (ids + delete flag),
`DaminionRemoveResponse` (per-id status map + success/errorCode envelope).

`DaminionItem.HashCode` is the server-computed content hash from the Daminion 
API's `/api/MediaItems/Get` response (`hashCode` field). It is recalculated 
when a file or new file version is imported. Synapic uses it in the dedup scan 
as a free grouping key: items whose `HashCode` matches are grouped together 
without downloading the original or computing an algorithmic hash. The field is 
nullable — older server builds or scoped queries may omit it, and a missing 
value falls back to the download-and-hash path. The API's `fileSize` is carried
through as the dedup record's size on the no-download path, so the
smallest/largest auto-select rules still work there. **Caveat:** the Daminion API 
docs describe `hashCode` as a content hash but do not state whether it is 
perceptual (tolerant of resize/re-encode) or exact (byte-identical only). 
`HashCode` is compared only with other server hashes — never with the algorithmic
hashes — by the rule the dedup step's Server hash picker sets: exact equality
(the default) or hamming distance at the threshold. Each scan's diagnostic
summary reports the hashCode distribution to help settle which it is.

**`DaminionConnectionStore`** — Windows-registry persistence of the Step 1
form (`HKCU\Software\Synapic\Daminion`), with the password DPAPI-protected
(base64 in a binary value) under the current user. `Load()` / `Save()` /
`Clear()`; a no-op on non-Windows. The record `DaminionConnectionParams`
carries every Step 1 field including `MaxItems`, `ResizeScale`,
`UseThumbnailOverride`, and `ProcessAll`.

### Processing (`Services/Processing/`)

**`WorkflowRunner`** — the shared batch kernel every route runs on. The
per-workflow parts are the `WorkflowDefinition(Key, Title,
MaxDegreeOfParallelism, Description)` (the `Description` clause is appended to
the batch-start log line, so each workflow states its own parameters in the
same sentence) and the `IWorkflowItemHandler` implementation; the runner owns
fetching, throttling, pause/cancel, progress with ETA, error isolation and the
`WorkflowRunSummary(Total, Processed, Succeeded, Failed)`:
- `FetchItemsAsync(ds, ct)` (static):
  - Local: recursive/shallow extension scan (`.jpg .jpeg .png .tif .tiff`).
  - Daminion: one batch per call via `GetItemsFilteredAsync`, advancing
    `startIndex`; stops on an empty batch, when `AutoPaginate` is off, or on a
    partial (<500) page — **except** when `ProcessAll` is set, which ignores
    both the `MaxItems` ceiling and partial pages and keeps requesting until the
    server returns nothing, guarded by an identical-page-id infinite-loop
    check.
- `RunAsync(workflow, ds, handler, progress, log, pause, ct)` — fetch, report
  the fetched total immediately (a cold model must not leave an empty bar),
  then `RunItemsAsync`.
- `RunItemsAsync(...)` — `SemaphoreSlim`-bounded per-item tasks (the bound is
  `MaxDegreeOfParallelism`); reports progress at item start and completion;
  thrown exceptions and `WorkflowItemOutcome.Fail` outcomes increment the
  failed count and log without aborting the batch; cancellation propagates as
  `OperationCanceledException`.
- `EstimateProgress(elapsed, processed, total)` (static) — the ETA math:
  per-item = elapsed/processed, ETA = per-item × remaining; `null` until the
  first item completes.
- Handlers in this folder: the tagging handler (nested in
  `ProcessingOrchestrator`), `DedupScanHandler` and `UpscaleItemHandler`.

**`ProcessingOrchestrator`** — the tagging workflow over that kernel.
- `RunAsync(ds, template, progress, log, ct, results, pause, tagFields)` /
  `RunItemsAsync(...)` (Step 4's retry uses the second) build the private
  `TaggingItemHandler` and a `WorkflowDefinition("tagging", …, parallelism, …)`
  — parallelism comes from the constructor (default 4; Step 3 hard-codes 4).
- The handler's per-item pipeline (port of `ProcessingManager`):
  obtain the image (local path, or Daminion thumbnail/preview/original per
  settings, deleting temp files afterwards), call `/tag`, apply
  `TagFieldSelection`, write metadata (`MetadataWriterService` or
  `UpdateItemMetadataAsync`) and append a `ProcessItemResult` to
  `Session.Results` (lock-guarded); ordinary failures are reported as outcomes,
  not thrown.
- Supporting types: `ProcessItemResult` (+ `KeywordsCsv`), `ScoringResultDto`,
  `TagFieldSelection`, `DatasourceSelection`, `ProcessWorkItem`,
  `ProcessProgress(...)` (+ `FormatDuration`, the shared ETA-line formatter).

**`DedupScanHandler`** — the Daminion dedup scan's per-item operation.
For items that carry a server-computed `HashCode` (from `DaminionItem`), the 
item is grouped directly from that hash — no download, no algorithmic hash. 
For items without a server hash (older server builds, or scoped queries that 
omit it), the original is downloaded and hashed the old way (download → 
`DedupService.ComputeHash` → record → delete temp). One item at a time keeps 
a large scope from landing on disk whole. Counters: 
`ServerHashGrouped` (items grouped from server hash only), 
`DownloadFailures`, `HashFailures`. Produces `ExactHashes` (server content
hashes, grouped by the Server hash rule) and `PerceptualHashes` 
(both `Dictionary<string, ulong>` keyed `"daminion:{id}"`, never compared
with each other) plus `Records` 
(`Dictionary<string, DedupScanRecord>`) the step builds its groups from via 
`IDedupService.GroupFromHashes`. The local scan stays a direct
`DedupService` call (no downloads involved).

**`UpscaleItemHandler`** — the upscale workflow's per-item operation (port of
`step_upscale._process_single_item`): for Daminion items check out
(`CheckOutItemsAsync` — a refused checkout skips the item) → download the
original → `POST /upscale` → check in with the
`Upscaled using {workflow} {factor}x (…)` comment → flush the `{id}_original*`
temp variants; on check-in failure or cancellation the checkout is undone so
nothing stays locked. Local items skip the version dance and the output lands
beside the original as `{name}_upscaled{ext}`.

**`PauseToken` / `PauseTokenSource`** — cooperative pause. Items call
`WaitWhilePausedAsync(ct)` between work units; running items finish, queued
items wait; cancellation wins.

**`DedupService`** (`IDedupService`) — NetVips-backed perceptual hashing
(pHash via a 32×32 DCT, dHash, aHash; ColorMoment currently approximates aHash),
Union-Find grouping with a hamming-distance threshold, and
`ApplyToPathsAsync` (also behind `ApplyActionsAsync`) for Delete / Move (into a
`duplicates/` subfolder) / Tag. `ComputeHash` is public static (instance member
on the interface too) and returns `null` for unreadable images;
`GroupFromHashes(perceptualHashes, exactHashes, opts)` groups already-computed
hashes — the Daminion scan's
incremental path; server hashes are compared only with each other, by
`DedupOptions.ServerHashMatch` (exact equality by default, hamming on request).
`CreateThumbnail(path, maxSize = 128)` scales an image into
an in-memory JPEG buffer for review previews (null for unreadable images,
alpha flattened for JPEG). `ReadImageDateUtc(path)` reads the EXIF capture date
(DateTimeOriginal, else IFD0 DateTime) for the auto-select rules, returning
null when absent so callers fall back to file time.
Types: `HashAlgorithm`, `DedupAction`, `ServerHashMatchMode`, `DedupOptions`, `DedupProgress`,
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
- **`SynapicLog`** — static Serilog bootstrap. One file sink (Debug+) at
  `logs/synapic.log` that **starts empty each run while the previous run is
  rotated into `logs/archives/synapic-<stamp>.log`** and pruned to
  `MaxArchivedLogs` (10), plus an in-memory `UiLogSink`
  filtered to the configured UI level. `LogDirectory` / `ArchivesDirectory`
  fall back to `%LOCALAPPDATA%\Synapic\logs` when the app dir is read-only.
  `For(context)`
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
  Runtime from the runtime's own folder (`shared/Microsoft.WindowsDesktop.App`,
  plus `DOTNET_ROOT`) with `dotnet --list-runtimes` as the fallback — **not**
  from `HKLM\…\InstalledVersions\x64\sharedhost`, which reports the *host*
  version and disagrees with the framework's in both directions. It downloads
  and silently installs the runtime when missing, and reports a
  `RuntimeCheckResult` (`RuntimeCheckOutcome`: `RuntimePresent`, `Installed`,
  `DownloadFailed`, `InstallFailed`, `NotApplicable`) instead of throwing.
  A runtime older than `MinimumVersion` counts as missing. When the install
  cannot be finished it logs the link and raises `RuntimeUnavailable` with
  `DownloadPageUrl` - the official
  `https://dotnet.microsoft.com/download/dotnet/10.0` page, derived from
  `MinimumVersion` - which is how the user still gets a way forward: the
  installers can refuse to run, but only the running app can offer a link.
  The `HttpClient`, the version probe and the platform are injectable, so the
  failure paths are tested without uninstalling a framework.
- **`ProcessJob`** — Windows Job Object wrapper (`KILL_ON_JOB_CLOSE`) so child
  processes die with the app; `AssignChild(process)` is best-effort and never
  throws.
- **`EngineSettingsStore`** — registry persistence for Step 2
  (`HKCU\Software\Synapic\Engine`), no-op off Windows.
- **`SystemPromptPresetStore`** — the system-prompt history as plain JSON
  (`%APPDATA%/Synapic/system-prompts.json`, shape `SystemPromptPresetFile`,
  newest first, capped at `MaxPresets`). History only: the live prompt stays in
  `Session`/registry, so pruning the list can never lose a run's configuration.

### Help

- **`HelpService`** (`IHelpService`) — opens the user help (`docs/help`, the
  HTML the Windows `.chm` is compiled from). The two platforms have different
  payloads and never share one:
  - **Windows** — the compiled `Synapic.chm`, **embedded in the assembly** and
    opened through `hh.exe` with the `ms-its:<chm>::/<topic>` moniker. The bytes
    are checked against the SHA-256 that `build-chm.ps1` recorded in
    `help-payload.json` (embedded beside them) and, if they match, unpacked to
    `%LOCALAPPDATA%\Synapic\help\Synapic-<hash>.chm` for the viewer to read.
    That unpacked copy is re-verified on every open. It is the only target
    there is: help that fails its hash, or an `hh.exe` that will not start, is
    logged and refused rather than quietly swapped for loose HTML.
  - **macOS/Linux, and Windows builds with no compiled help yet** — the same
    topics as HTML in `help/` beside the app, then a source checkout's
    `docs/help`, in the default browser.
  `Open` logs the target it opened, or why nothing could be
  (`Help is not available: …`, `Help: the compiled Synapic.chm … does not match
  the SHA-256 …`) — those lines appear in the in-app log.
- **`EmbeddedHelp`** — the compiled help as the app carries it: the bytes, the
  file name, and the SHA-256 they were compiled with. `HelpService.FromManifest`
  turns the two embedded resources into one, or into `null` when either is
  missing or unusable — a payload that cannot be checked is not opened.
- **`HelpTopics`** — the only place topic file names exist (`Home`,
  `FirstRunSidecar`, `Troubleshooting`, `SetupGuide`, `SettingsReference`,
  `Tips`, `ForStepIndex(stepIndex)`). `NormalizeTopic` maps anything that is
  not a plain `.html` name with an optional `#anchor` to the home page, so a bad
  value cannot reach outside the help.
- Payload: `Synapic.Main.csproj` embeds `docs/help/Synapic.chm` and
  `help-payload.json` as `Synapic.Help.*` resources (when they exist — both are
  artifacts of `docs/help/build-chm.ps1`), and copies `docs/help/*.html` and
  `help.css` into `help/` for the **non-Windows** RIDs, which have no `.chm`
  viewer.

### Build pipeline

- **`SidecarBuildService`** (`ISidecarBuildService`) — runs
  `build/build-server.ps1|sh <rid>` with streamed output, de-duplicated
  progress reporting, cancellation (kills the whole process tree), and
  `CanBuild` (repo root + build script present). Rejects malformed RIDs.
  Every output line also lands in `logs/synapic.log` at Debug (the UI ring
  buffer is gone once the app closes — a failed build must stay readable),
  and a lock pre-flight runs before the script: PyInstaller cannot replace an
  executable Windows still holds open (the build would die with
  `PermissionError` only after minutes of packaging), so processes running
  from *this* output path are killed (orphans; the view model stops the
  managed server first) or the build fails in seconds with a message that says
  what to stop.
- **`SidecarBuildProgressTracker`** — maps raw build output to a stage +
  0-100 percentage across the three bands (fetch Python → install deps →
  PyInstaller packaging, the last driven by bytes written versus the previous
  build's executable size). `SidecarBuildProgress(Percent, Stage)`.
- **`SidecarDownloadService`** (`ISidecarDownloadService`) — the other way to
  get a variant: reads the recent-releases list (`/releases?per_page=20`, the
  same feed the startup check reads) and picks the newest release that ships a
  `SHA256SUMS.txt` manifest *and* assets for this RID — CI's rolling `nightly`
  prerelease and the versioned tags are peers, so a push to main offers bytes
  fresher than the last tag. It picks the assets named
  `synapic-inference-<rid>` (never its `-cuda` sibling, matched on the
  extension boundary), joins `.partN` in part order, stages to
  `<destination>.download`, moves into place only once complete, then restores
  the unix executable bit (release assets never carry it). Progress is percent
  of the declared total; a failed or cancelled pull deletes the staging file
  so detection never sees a half server. `CheckForUpdateAsync(rid,
  localExePath)` is the advisory startup check: returns
  `SidecarUpdateInfo?(TagName, PublishedAt, TotalBytes, LocalMissing)` or null
  (nothing newer than the local file — `published_at` vs mtime — or GitHub
  unreachable) and never throws.

---

## Shared contracts (`src/Synapic.Shared`)

`Contracts/Contracts.cs` — records matching the sidecar protocol exactly
(snake_case via explicit `[JsonPropertyName]`): `HealthResponse` (+
`ModelDownloadProgress`), `ModelInfo`, `DownloadRequest`, `TagRequest` /
`TagOptions`, `TagResponse`, `ScoringResult` / `ScoredKeyword`, `ConfigDto`,
`UpscaleRequest` / `UpscaleOptions`, `UpscaleResponse`.

`JsonSerializerContext.cs` — `SynapicJsonContext`, a source-generated
`JsonSerializerContext` for every DTO above (camelCase policy, null-ignoring,
string-number reading). Both processes rely on this being the
single wire definition; changing a contract means updating the DTO, this
context, and the Python Pydantic models, then regenerating
`docs/sidecar-protocol.md` (`python build/generate-protocol-doc.py`, verified in
CI by `--check`) and re-running both test suites.
