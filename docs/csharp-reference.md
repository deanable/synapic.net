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

### `Views/MainWindow.axaml(.cs)` and `Views/MainLayout.axaml(.cs)`
`MainWindow` carries window-level behaviour only — F1's help resolution,
Alt+Home, Ctrl+S and log teardown — and hosts the shell, `MainLayout`: a left
**sidebar of five navigation rows** and one content panel to its right, the open
row lit.

| Row | Command | Opens |
|-----|---------|-------|
| **Settings** | `GoHomeCommand` | the Settings view (`SettingsTab`) — also what `Shell.Current == null` means |
| **Tagging** | `StartTaggingRouteCommand` | the tagging operation |
| **Deduplication** | `StartDedupRouteCommand` | the duplicate scan |
| **Similar images** | `StartSimilarRouteCommand` | the similarity search; the row sits directly below Deduplication because it is that search's looser sibling |
| **Upscaling** | `StartUpscaleRouteCommand` | the upscaling operation |

Each row's `Classes.active` flag is computed from `Shell.Current`
(`IsSettingsVisible`, `IsTagModeActive`, `IsDedupModeActive`,
`IsSimilarModeActive`, `IsUpscaleModeActive`), so the lit row is derived rather
than remembered by the sidebar; every row is disabled while
`IsNavigationLocked` (`Operations.IsRunning`), so a batch is not navigated away
from mid-flight. The server indicator, the download read-out, the device notice
and the **Help** button are pinned to the bottom of the sidebar, so they are on
screen in every view.

The content panel shows exactly one thing: the Settings view when nothing is
open, otherwise the open operation's processing view (`OperationLayout`), whose
region content is handed over in code-behind — a binding written on that element
would resolve against its own `DataContext`, not the shell. Above it the sidecar
banner appears while no usable variant exists or the built one is stale, and the
workspace stays inert until a sidecar is ready. The diagnostics **Log** list at
the bottom is in the tree only while the Logging setting says so (there is no
always-visible log strip; an operation's run log lives in its processing view).
The code-behind attaches the view model's sidecar log stream and auto-scrolls the
log list to the newest entry on every collection change.

Below ~1000 px of shell width the sidebar collapses to an icon-only rail
(`MainLayout.ApplyCompact` — Avalonia has no XAML breakpoint): each row keeps its
glyph and drops its label, the title and the wide status lines go with it, and
Help shortens to `?`.

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

### `Views/Wizard/*.axaml(.cs)`, `Views/Operation/*` and `Views/Settings/*`
The user controls the shell renders. Code-behind is intentionally thin:

| View | Code-behind behaviour |
|------|-----------------------|
| `DatasourceSourcePanel.axaml.cs` | `OnBrowseFolder` — OS folder picker writes `vm.LocalPath`; hosts `SourceStatusStrip` |
| `SourceStatusStrip.axaml.cs` | the two source lights (Daminion / folder), shared by the Settings data-source form and the processing views' *Working on* region |
| `Step2Engine.axaml.cs` | the full tagging settings form, hosted by the Settings view's Tagging tab; prompt-preset Delete handling |
| `DedupSettingsPanel.axaml.cs` | the dedup scan rules, hosted by the Settings view's Dedup tab |
| `SimilarSettingsPanel.axaml.cs` | the similarity search's rules, hosted by the Settings view's Similar tab; it renders the shell's `ImageMatchProfile.Similar` instance, so the threshold set here is never the duplicate scan's |
| `UpscaleSettingsPanel.axaml.cs` | the upscale parameters, hosted by the Settings view's Upscale tab |
| `Step4Results.axaml.cs` | `Refresh` button + auto-scrolls the results grid to the newest row |
| `StepDedup.axaml.cs` | the run page of **both** image-matching operations (one `StepDedupViewModel` template serves dedup and the similarity search): `OnBrowseMoveFolder` writes `vm.MoveDestinationFolder`, and `DataContextChanged` wires `vm.ConfirmAction` to `ConfirmDialogWindow.ShowAsync`, so a destructive apply fails closed when no dialog can answer |
| `StepUpscale.axaml.cs` | auto-scrolls the run log list to the last line |

| View | Code-behind behaviour |
|------|-----------------------|
| `Operation/OperationLayout.axaml.cs` | the shared processing template. `SharedSource`, `Run` and `Report` are Avalonia (`Styled`) properties, so writing `Shell.Current` really re-renders the regions — a plain CLR property would be read once and could keep showing the previous operation's run. The `Run` slot renders `RunStateBar` plus the operation's own page (`StepDedup` for both image-matching operations, `StepUpscale`), and the report slot renders `Step4Results` for tagging |
| `Operation/RunStateBar.axaml.cs` | the one run bar every mode inherits from `RunStateViewModel`; it owns the run log's auto-scroll (guarded so a line arriving during teardown cannot arrange a detached list) |
| `Operation/ScopeSelectionPanel.axaml.cs` | the catalog scope, its filters and the record count, bound to the one shared source view model the Settings view binds too; carries no commands of its own |
| `Operation/SourceLimitsPanel.axaml.cs` | Region A's limits row (max items, process all, image scale), bound to the same shell-owned `Step1DatasourceViewModel` |
| `Settings/SettingsTab.axaml.cs` | the Settings view as seven tabs — Connection, Inference server, Tagging engine, Deduplication, Similar images, Upscaling, App preferences — plus the search box: `TabSuggestions` maps each tab to the queries that name it (`Similar`/`Similarity`/`Near-match` for the Similar tab), `ApplyFilter` switches to the matching tab and reports what it did, and `ShowSection` / `ShowTab` are what the tests land on |
| `Settings/InferenceServerSection.axaml.cs` | the Inference server section: status, Start/Stop/Build, auto-launch, download progress and the per-variant rows, all bound to the shell |

---

## View models (`ViewModels/`)

Built on CommunityToolkit.Mvvm (`[ObservableProperty]`, `[RelayCommand]`).

### `ViewModelBase`
Empty base class (`ObservableObject`).

### `MainWindowViewModel` (singleton)
The shell. Owns:

- **Navigation** — `Shell` (`ShellViewModel`): `Shell.Current` is null on the
  Settings view and names the one open operation otherwise, and it is the only
  navigation state in the app (the route strings, the `Is*Route` booleans and the
  wizard's current step are gone). The sidebar's five rows bind the commands
  `GoHomeCommand`, `StartTaggingRouteCommand`, `StartDedupRouteCommand`,
  `StartSimilarRouteCommand` and `StartUpscaleRouteCommand`; each calls
  `Shell.Open(<key>)` (`OperationShellViewModel.TagKey` / `DedupKey` /
  `SimilarKey` / `UpscaleKey`) or `Shell.Home()`, logs the move, and the route
  machine reports back through `Shell.SetCurrent`. Everything the chrome needs is
  derived from that one value: `IsSettingsVisible`, `IsOperationVisible`,
  `OperationTitle`, the four `Is*ModeActive` flags and `ContextHelpTopic`.
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
- **The operations as the template sees them** — `TagOperation`,
  `DedupOperation`, `SimilarOperation` and `UpscaleOperation` (the Phase 1
  adapters), handed to `ShellViewModel` as its operation list.
  `SimilarOperation` is a `DedupOperationViewModel` over the
  `ImageMatchProfile.Similar` view model: the same adapter class as
  `DedupOperation`, so the two are separate operations rather than one mode with
  a flag, and each reads its identity off the step it wraps.
- `SettingsCommand` — the Settings shortcut (`Shell.Home()`, the same move the
  sidebar's first row makes through `GoHomeCommand`). Every configuration lives
  on the Settings view now, so this is navigation, never a window.
- Help: `OpenHelpCommand` (the sidebar's **Help** button) opens
  `HelpTopics.Home`; `OpenContextHelpCommand` (<kbd>F1</kbd>, bound in
  `MainWindow.axaml`) opens `ContextHelpTopic` — the sidecar topic while
  `IsSidecarRequired`, otherwise the open operation's own `HelpTopic`
  (`HelpTopics.Similar` for the similarity search, `ForStepIndex(4)` for the
  duplicate scan and upscaling), and the help home on the Settings view.
  `HelpScope` is consulted first, so focus inside an annotated section or setting
  opens that scope's topic instead.
- `PollHealthAsync` — 2-second background poll while Starting/Running; feeds
  the download panel.

Constructed with optional locator delegates (`sidecarExecutableLocator`,
`sidecarVariantLocator`) and an optional `IHelpService` so tests can inject fake
filesystem state.

### `ShellViewModel` and `OperationShellViewModel`
The operation host (`ViewModels/Operations/OperationShellViewModel`) owns the
view models the shared template renders and nothing else: `Source` (Region A — the
one shared `Step1DatasourceViewModel` every mode reads), `TagParameters` /
`TagRun` / `TagReport`, `Dedup` and `Similar` (one `StepDedupViewModel` class
under the two `ImageMatchProfile`s), and `Upscale`. `ParametersFor(key)` /
`RunFor(key)` / `ReportFor(key)` hand each mode its regions; `ReportFor` is null
for dedup and the similarity search, which review and apply inside their own run
content, so the similarity search is a region of its own over its own instance
rather than a second view of dedup. `IsRunning`
(`TagRun.IsRunning || Upscale.IsRunning`) is what the sidebar's run lock reads,
and `NavigationLockChanged` tells the shell to re-read it — the wiring that keeps
a running batch from being navigated away from.

`ShellViewModel.Current` is the navigation state described above. `Open(key)` and
`Home()` forward through the route machine (`MainWindowViewModel`'s `Start*Route`
and `GoHome`), which reports back through `SetCurrent`; nothing binds `Open` /
`Home` directly, because the sidebar rows and the operation layout's mode rail
themselves use the route commands, so a second navigation model cannot grow.

The step view models in `ViewModels/Steps/` are still what each mode runs on, but
they no longer carry navigation: no current step, no Next/Back, no
mutual-exclusion route flags, no `PrefillDedupSource`. Each mode declares its own
ready-to-run gate as `IOperationViewModel.IsRunEnabled` with a
`RunDisabledReason`, `Session.ValidateForStep2/3` stay the underlying source and
engine checks, and the shared run bar in `OperationLayout` is the one run surface
for all four operations.

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
- Source readiness (the gate every operation's `RunDisabledReason` reads):
  `LocalFolderExists`,
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
- `ApplyStoredSource` (on `DatasourceState`) restores the folder/type at
  startup, so the processing views have a source before the user touches
  anything. `App` feeds it from the registry store (`restored.ToDatasourceSettings()`),
  not from `config.json`, which is now only a backup.
- `ToSelectionForProcessing(client)` builds the orchestrator's
  `DatasourceSelection` (sharing the authenticated client).

Persistence: on a **successful** connect, every field is saved to
`DaminionConnectionStore`; on construction the form is pre-filled from the
store, and `InitializeAsync` reconnects to it once at launch (a failed login
just leaves the form filled in).

The *active source* is written back too, through `SaveStoredSource`
(`AppSettingsRegistryStore`, fields `DatasourceType` / `LocalPath` /
`LocalRecursive`): switching the radio buttons records which tab wins the next
launch, and a folder path is recorded once it resolves (or is cleared), so a
half-typed path never becomes the default. Redundant writes are skipped by
comparing with the stored state. Wiring: `OperationShellViewModel` passes the
store into `Step1DatasourceViewModel`. Because launch reads the source from the
registry, a session that ended connected to a catalog reopens on Daminion (and
is reconnected), and one that ended on a folder reopens on that folder.

The Daminion retrieval path logs every query it sends and every answer it gets,
because a total and a page set that disagree are otherwise impossible to
diagnose from the UI. `GetFilteredItemCountAsync` logs `Count request:` (scope,
ids, search/queryLine/operators) and `Count response:` (the raw `count`,
`totalCount` and `data` fields and the effective value), plus the sanity-check
and fallback probes. `GetItemsFilteredCoreAsync` logs one `Page <endpoint>:`
line per request with its index, size, scope, saved search/collection id, search
clauses and operators, and the response's raw/eligible/returned counts, its
`totalCount` and the id range. The count and the pages deliberately build their
status/untagged clauses from the same `BuildFilterClauses` (the documented
text values `flag:approved` / `flag:rejected` / `flag:unassigned`), so the number
shown for a scope and the pages walked for it come from one query; an
unfiltered scope sends no `search` at all (not a literal `*`).
`WorkflowRunner.FetchItemsCoreAsync` logs the whole walk's parameters, takes the
dedup/similar denominator from `GetFilteredItemCountAsync` for `all`/`search`
scopes when it succeeds (falling back to the page's `totalCount`; collection and
saved-search pages keep the page total, because their raw cursor and a locally
filtered count are not on the same scale), and warns when a page's total
contradicts it or when a server-filtered walk returns fewer items than the
scope count — the two symptoms of the old "500 of 47,851" progress bar.
Paging itself is resilient to the two meanings of `index`: the walk steps by
item offset (what every working catalog walk has been served with), but when a
page comes back empty while the search's own record count still has records
left, it continues with **page serials** — what the REST reference documents
for `index` — resuming at the page the raw records already cover, and stops once
that count is collected (with a page budget as the backstop). That is how a
saved-search scope that answered page 0 and nothing else still gets fetched
whole; the switch, the mode each page used and any shortfall are all logged, and
`Scanned` in the diagnostics is the raw records the server returned.
`FetchEntireScopeAsync` also takes an optional one-shot
`CatalogRetrievalDiagnostics` sink. At the end of a whole-scope walk it reports
the scope count, the last page total, the pages read, the raw records scanned
and the unique items kept in one record (including for an empty scope, which
produces no page progress at all) and logs a `Daminion retrieval diagnostics:`
line. The dedup/similar scan card keeps the last report and renders it as a
diagnostics block — `HasRetrievalDiagnostics`, `RetrievalCountText`,
`RetrievalPageTotalText`, `RetrievalPagesText`, `RetrievalScannedText`,
`RetrievalKeptText` and `RetrievalComparisonText` — so a count/page/kept
mismatch is visible without opening the log. The numbers themselves are
read-only; two non-destructive actions sit under them. When
`HasRetrievalMismatch` (a count-vs-kept disagreement on an all/keyword scope),
`ReWalkUsingPageTotalsCommand` runs the scan again through the normal
`ScanCommand` with `ignoreScopeCount: true`: the scope count is still reported
but no longer steers the walk, so each page's `totalCount` does. The mode is
one-shot, and a normal scan is back on the count. `ExportRetrievalReportCommand`
writes a text report (operation, scope, count, page total, pages, raw scanned,
kept, which total was followed, the comparison and the log path) to a
timestamped file in Documents, with the result in `RetrievalActionMessage`.
Neither action changes the catalog. The block is catalog-only;
collection/saved-search differences are labelled as the expected result of
client-side filtering and do not offer the re-walk.

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
One class, two operations. The constructor takes an `ImageMatchProfile`
(`Duplicates` or `Similar`) and the instance serves either the **Deduplication**
operation or the **Similar images** one: the scan, the review and the apply are
the same code, and the profile picks only what has to differ — the starting
threshold (`DefaultThresholdFor(profile, algorithm)`: `0.90` for duplicates and
`0.85` for the similarity search under pHash — the other two fingerprints put
different pictures far closer together and carry their own tighter numbers in
`AppSettingsRegistryStore.DedupThresholdDefaultFor` / `SimilarThresholdDefaultFor`;
the store resolves that same default from a *saved* algorithm when no threshold
was ever stored), the wording
(`OperationTitle`, `MatchNoun` / `MatchNounPlural`, `ScanCardTitle`,
`ApplyCardTitle`, the hint lines), the operation identity (`OperationKey` —
`dedup` or `similar`, the key it is opened, hosted and persisted under), and
which half of the settings store it reads and writes (`Dedup*` versus
`Similar*`), so widening one search never widens the other. The two profiles are
two operations end to end: separate keys, sidebar rows, settings tabs, help
topics and registry values.

Algorithm (PHash/DHash/AHash) + threshold over two sources — the
same radio pattern as Step 1: `IsLocalSelected`/`IsDaminionSelected` pick a
local `FolderPath` or the Step 1 Daminion scope (`DaminionScopeSummary`, fed by
the injected `Step1DatasourceViewModel` + sidecar). `ScanCommand` enumerates
supported extensions (local) or fetches Daminion items and groups them: the
catalog is walked whole through `FetchEntireScopeAsync`, and items with a server
`HashCode` are grouped without download; items without fall back to downloading
each original, hashing it, and deleting the temp file. Both phases drive the
shared `RunStateViewModel` bar — `ApplyCatalogFetchProgress` for the walk
(records/page/ETA) and `ApplyProgress` for hashing — so a dedup run no longer
leaves the bar idle while it works, and a cancelled scan clears the estimate.
The card also carries the last walk's **retrieval diagnostics**
(`LastRetrieval`, `HasRetrievalDiagnostics`, and the `Retrieval*Text`
properties): the scope count, the pages' own total, pages, raw records and
kept items side by side, plus a sentence that spells out a mismatch. On a
count-vs-kept disagreement `HasRetrievalMismatch` exposes
`ReWalkUsingPageTotalsCommand`, which re-runs the normal scan with the count
ignored as the denominator (one-shot; the shared Stop still cancels it), and
`ExportRetrievalReportCommand` always writes the last report to a timestamped
text file in Documents (`BuildRetrievalReport`; path injectable for tests). It
is shown only for a Daminion source and after a walk; the numbers are not
editable and neither action touches the catalog. A walk that ends before the
scope's own count while dropping nothing on this side is reported as a short
read (`RetrievalComparisonText`), never excused as "filtered locally" — that
excuse is now reserved for `Scanned > Kept`, and `ScanSummary` carries
"The scope holds N; the walk read M." next to the result.
`BuildOptions` decides whether the catalog's content hash may be grouped:
`UseServerHashes: !IsSimilar`. The Server
hash picker (Deduplication only — `ShowsServerHashRule`) decides how those
server hashes are compared —
Exact (identical values only, the default) or Hamming at the threshold. Produces 
`Groups` — a vertical list of
`DuplicateGroupViewModel` cards, each holding `DedupItemViewModel` rows with an
`IsChecked` keep checkbox (checked = kept; unchecked = action target). The
`SelectOldest`/`SelectNewest`/`SelectSmallest`/`SelectLargest` auto-select
switches recompute every group's checkboxes as the union of their picks
(keep-first when none is active; unknown dates/sizes are never picked).

After a scan the results card carries a **live threshold slider**
(`RetuneThresholdSlider`, visible through `CanRetune`). It edits the same
`Threshold` the settings tab writes, and a change schedules `RetuneAsync`
through a 300 ms debounce (`RetuneCompletion` is the awaitable tests use): the
retained `DedupResult.PerceptualHashes` / `ExactHashes` are re-grouped with
`IDedupService.GroupFromHashes` on the thread pool — no image is hashed or
re-read — and the cards are rebuilt with `preserveSelection` so the user's
keep checkboxes survive by item key. Item view models are cached across
regroups (`_localItems` / `_pendingDaminionItems`), so a preview is rendered or
downloaded once per scan and only items that newly enter a group are fetched.
The slider hides while scanning or applying, after an Apply (the source changed,
so `_lastScan` is dropped), and when the selected algorithm no longer matches
the one that produced the fingerprints — hashes are not comparable across
algorithms. Measured release-build grouping cost: ~0.4 ms for 1,000 items,
~22 ms for 10,000, ~90 ms for 20,000 and ~0.5–1.0 s for 50,000 (O(n²)
comparisons), which is what makes the slider feel live up to a large personal
library and still usable, with a busy line, on a huge catalog.
A completed scan is also **saved for the next launch**. `DedupFingerprintCache`
(gzipped JSON, one file per operation and source under
`%LOCALAPPDATA%\Synapic\fingerprints`, the newest eight per operation kept)
stores `CachedDedupScan` — version, source key, algorithm, threshold, the
per-item row metadata and both hash dictionaries — and `StepDedupViewModel`
restores it from the constructor (`RestoreCompletion` is the awaitable tests
use) and whenever the configured source changes. The source key is
`local|<full path>` or `daminion|<url>|<scope>|<ids>`, so a cache is never
borrowed by another folder, scope or operation. Restore re-groups those hashes
with the current settings (`GroupFromHashes`), rebuilds the rows (catalog rows
keep their ids, so Apply works after reconnecting) and sets `ScanSummary` to
say the results are from the last scan. It is skipped when the saved algorithm
no longer matches the selected one, when a scan/apply has already rebuilt the
list, and after the source moves again mid-load. The Similar profile also
discards a saved result that holds only catalog content hashes and no
fingerprints (`IsStaleSimilarCache`) — that is the old server-hash path's
output, and restoring it would show exact-match groups the search no longer
produces; a corrupt, truncated or
other-version file is logged and ignored, never repaired. `Apply` deletes the
source's file (its fingerprints describe files that have changed), a rescan
replaces it, and a scan larger than `MaxItems` (250,000) is not cached.
Writes are atomic (temp file then move), so a crash mid-save cannot leave a
partially readable cache.

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
- `GetItemsPageAsync(scope, savedSearchId, collectionId, searchTerm,
  untaggedFields, statusFilter, maxItems, startIndex)` — returns **at most one
  batch** (≤ `PageSize` = 500) per call; callers paginate. Returns a
  `DaminionItemPage`: `Items` (the eligible subset), `RawItems`/`RawCount`
  (the full server page) and `TotalCount` (the scope total the endpoint
  reported, when it reports one). Collection and saved-search scopes apply
  client-side filters after the fetch, so `RawCount` — never the eligible
  count — is the cursor advance. `GetItemsFilteredAsync` is the `Items`-only
  convenience wrapper.
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
  - Daminion: one batch per call via `GetItemsPageAsync`, advancing
    `startIndex` by the raw page (`DaminionItemPage.RawCount`) so a page the
    client-side status/untagged filter empties does not end the walk; work items
    are deduped by catalog id; stops on a completely empty raw page, when
    `AutoPaginate` is off, or on a partial (<500) page — **except** when
    `ProcessAll` is set, which ignores both the `MaxItems` ceiling and partial
    pages and keeps requesting until the server returns nothing, guarded by an
    identical-raw-page-id infinite-loop check.
- `FetchEntireScopeAsync(ds, progress, ct, diagnostics)` (static) — the same
  walk with the ceiling and the paginate switch forced off, for operations
  that are only correct over the complete set. Deduplication and Similar images
  must use this: Step 1's `MaxItems` ceiling defaults to 100 and the catalog
  API answers at most 500 records per call, so inheriting the tagging ceiling
  silently compared the first 100 items. Reports
  `CatalogFetchProgress(Kept, Scanned, Pages, Total)` after every page —
  `Scanned` is the raw cursor (the metric the server's total is comparable
  with), `Kept` the eligible subset, `Pages` the completed 500-record pages —
  and logs each boundary. The optional `IProgress<CatalogRetrievalDiagnostics>`
  reports the whole walk once at the end (count, last page total, pages,
  scanned, kept) for the scan card's diagnostics block; `ignoreScopeCount`
  keeps that count in the report but lets the pages' totals steer the walk
  (the re-walk action's diagnostic mode). The scan renders the
  walk on the shared run bar as `Fetching catalog: 1,500/47,851 items` plus
  `ETA … per page`, using `EstimateCatalogFetchProgress(elapsed, pages, scanned,
  total)`: average page cost so far × pages the scope total still implies, with
  no ETA when the endpoint reports no total. Tagging and upscaling keep
  `FetchItemsAsync`, where a partial batch is meaningful.
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
item is grouped directly from that hash — no download, no algorithmic hash —
unless the scan set `DedupOptions.UseServerHashes = false`, which the similarity
profile does: a content hash answers "is this the same file", not "does this look
alike", so reusing it made every catalog similarity scan an exact-match scan (a
live run reported 500 items, 500 grouped from server hash, 0 hashed). The value
is stored zero-extended over 32 bits (`unchecked((ulong)(uint)serverHash)`):
Daminion's hash is a signed 32-bit number, and casting long → ulong
sign-extended it into 32 guaranteed-equal (or guaranteed-different) high bits.
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
(pHash via a 32×32 DCT, dHash, aHash),
Union-Find grouping with a hamming-distance threshold, and
`ApplyToPathsAsync` (also behind `ApplyActionsAsync`) for Delete / Move (into a
`duplicates/` subfolder) / Tag. `ComputeHash` is public static (instance member
on the interface too) and returns `null` for unreadable images. Before anything
is measured it normalises the pixel layout, because the fingerprint has to
describe the picture a person sees: EXIF orientation is applied (a phone
portrait is stored sideways, and hashing the raw buffer scored the same shot
~0.5 similarity), transparency is composited onto white (libvips keeps the
alpha band through `Colourspace(Bw)`, and the single-band readers then sampled
an interleaved grey/alpha buffer — the same picture scored 31–32 bits apart,
and pHash produced no hash at all), and the working copy is capped at
`DedupOptions.MaxDimension` (the documented "resize before hash"; pictures
already inside the cap are never upscaled). Each algorithm then scales down to
its own 8×8 / 32×32 grid;
`GroupFromHashes(perceptualHashes, exactHashes, opts)` groups already-computed
hashes — the Daminion scan's
incremental path; server hashes are compared only with each other, by
`DedupOptions.ServerHashMatch` (exact equality by default, hamming on request),
and their hamming budget and similarity scores are measured over the hash's own
32 bits — `MaxHammingDistance(threshold, bits)` takes the width, because 10% of
32 bits is not 10% of 64.
Both it and `FindDuplicatesAsync` put the dictionaries they grouped on the
returned `DedupResult` (`PerceptualHashes` / `ExactHashes`), which is what lets
the results page re-group at another threshold without reading, hashing or
downloading anything again.
`DedupFingerprintCache` (`Services/Processing/DedupFingerprintCache.cs`)
persists a `CachedDedupScan` per operation and source — see
`StepDedupViewModel` above for the restore rules — with
`SaveAsync`/`LoadAsync`/`Delete` and pruning to the newest eight files per
operation.

`CreateThumbnail(path, maxSize = 128)` scales an image into
an in-memory JPEG buffer for review previews (null for unreadable images,
alpha flattened for JPEG). `ReadImageDateUtc(path)` reads the EXIF capture date
(DateTimeOriginal, else IFD0 DateTime) for the auto-select rules, returning
null when absent so callers fall back to file time.
Types: `HashAlgorithm`, `DedupAction`, `ServerHashMatchMode`, `DedupOptions`, `DedupProgress`,
`DuplicateGroup`, `DedupResult`.

### Media & metadata

**`MetadataWriterService`** (`IMetadataWriter`) — `ReadAsync` /
`WriteAsync(filePath, TagResult, ct)`. Nonempty values patch JPEG APP1,
PNG `iTXt`, or TIFF's adjacent `image.tif.xmp` sidecar; keywords merge and
omitted fields/unowned XMP survive. `PatchAsync(filePath, MetadataPatch, ct)`
on the concrete service adds explicit Unchanged/Replace/Merge/Clear semantics
(Merge is keyword-only; description changes affect `x-default`, not translations).
Writes stream to a same-directory staging file, validate XMP/image decoding,
then atomically replace the destination; cancellation propagates before commit.
Oversized basic JPEG packets and ambiguous/malformed/extended XMP are rejected.
Reads XMP (including TIFF sidecars) plus IPTC/EXIF fallbacks.
`TagResult(Category, Keywords, Description)`. See
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
- **`AppSettingsRegistryStore`** — the general settings as one flat value map
  under `HKCU\Software\Synapic\AppData` (no-op off Windows): appearance and
  logging, the new-session defaults, the local source and the processing limits,
  and the rules of the three processing operations. The dedup and similar rules
  are two independent sets of nine values (`Dedup*` / `Similar*`), which is what
  keeps the two searches' algorithms, thresholds, keep-sets, actions and move
  destinations apart, with starting thresholds that belong to the fingerprint
  rather than to the profile (`SimilarThresholdDefault` is `0.85` for pHash,
  `0.91` for dHash and `0.94` for aHash; the duplicate scan's equivalents are
  `0.90`, `0.97` and `0.99`). `Load()` returns
  `null` when nothing has been saved yet, `Defaults` is the shipped state the
  load-then-modify writers start from, and a one-time import maps an old
  `%APPDATA%\Synapic\config.json` into the key while it is still empty.
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
  `Tips`, `Upscale`, `Similar`, `ForStepIndex(stepIndex)` — index 4 is the
  duplicate scan, 5 upscaling). `NormalizeTopic` maps anything that is not a
  plain `.html` name with an optional `#anchor` to the home page, so a bad value
  cannot reach outside the help.
- Payload: `Synapic.Main.csproj` embeds `docs/help/Synapic.chm` and
  `help-payload.json` as `Synapic.Help.*` resources (when they exist — both are
  artifacts of `docs/help/build-chm.ps1`), and copies `docs/help/*.html` and
  `help.css` into `help/` for the **non-Windows** RIDs, which have no `.chm`
  viewer.

### Build pipeline

- —
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
