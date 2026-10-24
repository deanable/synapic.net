# Testing

Six test projects, run from `Synapic.Net.sln` and pytest. The suites are
deliberately hermetic: nothing needs the network unless you opt in with
environment variables, and the image fixtures are generated in temp folders.

```bash
# Everything .NET (Core + Indexer + Main + Shared + Integration)
dotnet test Synapic.Net.sln -c Release

# Sidecar
python -m pytest tests/Synapic.Inference.Tests -q
# CI installs only pytest + httpx + fastapi + uvicorn + pillow + huggingface_hub
# (no torch/transformers), so the Python suite must stay import-light.

# Run it with that whole set, or download tests fail on a missing import rather
# than on what they test — four of TestDownloadProgress monkeypatch
# huggingface_hub.snapshot_download, which raises ModuleNotFoundError without it:
#   uv run --with pytest --with httpx --with fastapi --with uvicorn \
#          --with pillow --with huggingface_hub \
#          python -m pytest tests/Synapic.Inference.Tests -q
```

A test that cannot run on this machine says so instead of passing quietly:

- **`Live`** — the opt-in catalog probes are `[SkippableFact]` and
  `[Trait("Category", "Live")]`. They skip when `SYNAPIC_TEST_DAMINION_*` (or the
  saved connection's credentials) are absent, when the server cannot be reached,
  and when the live catalog does not contain the fixture items the comparison is
  about. Filter them out for a hermetic run:
  `dotnet test Synapic.Net.sln -c Release --filter "Category!=Live"`.
  The phase 10 round trip (tag → read back → re-run, plus a real firewall
  outage) runs through `build/acceptance-daminion.ps1`; see
  [Live catalog round trip](#live-catalog-round-trip-phase-10).
- **Windows-only** — registry tests use the `[WindowsFact]` attribute
  (`PlatformFacts.cs`), so off Windows they report as skipped with the reason
  instead of a green that asserted nothing. The handful of `[AvaloniaFact]`
  tests that touch the registry still return early, because Avalonia's fact
  attribute is sealed and has no runtime skip.
- The Python suite needs an interpreter with pytest; the bundled one works:
  `build/_python/win-x64/python/python.exe -m pytest tests/Synapic.Inference.Tests -q`.
  `uv build` in `src/Synapic.Inference` builds the sdist and wheel.

---

## `tests/Synapic.Core.Tests` (xUnit, headless)

The engine's own suite: it references `Synapic.Core` and xunit only, with no
Avalonia package and no desktop test host, so a Core type that grew a UI
dependency would fail to compile here. A test belongs in this project when it
needs no view model, no `Session` and no rendered control.

| File | What it pins down |
|------|-------------------|
| `MetadataWriterTests` | The XMP write→read round trip field by field, the staged/validated/commit sequence, the read-only and symlink refusals, and TIFF sidecar handling. |
| `DaminionBaseUrlTests`, `DaminionCatalogGuidTests`, `DaminionCaptureDateTests` | URL joining and normalization, the catalog guid a scan is bound to, and capture-date parsing edge cases. |
| `DaminionDeleteRequestTests`, `DaminionFlagRequestTests`, `DaminionIndexedTagValuesRequestTests` | The exact Daminion request shapes (fields and envelopes) a wrong name would silently break. |
| `DaminionLayoutParsingTests`, `DaminionTempDownloadTests` | Layout/tag-value parsing and the version-control temp download lifecycle. |
| `TagFieldSelectionTests` | Which fields a run writes, over the real kernel with a capturing writer. |
| `SidecarDeviceProviderTests` | The sidecar host launches for the device an `ISidecarDeviceProvider` reports and defaults to CPU without one — with no `Session` anywhere. |

`TestData/sample.jpg` is this project's own fixture; the writer tests find it by
walking up from the test output directory.

---

## `tests/Synapic.Indexer.Tests` (xUnit, Windows)

The service host's suite: `net10.0-windows`, no Avalonia, and no real SCM. `sc.exe`
and `icacls` are injected as function seams, so the command builders and the
ProgramData grants are asserted as token lists rather than by changing the
machine. The one real sidecar process (and real model) is the opt-in
`RealSidecarKillTests`, which skips with its reason unless asked for.

| File | What it pins down |
|------|-------------------|
| `IndexerConfigStoreTests` | Registry→JSON precedence value by value, malformed JSON as an error naming the file, unknown keys as warnings, the password save preserving unknown members, DPAPI LocalMachine round trip. |
| `ApplyConfigCommandTests` | The `--apply-config` merge: unknown members survive, the merged result is validated before anything is saved (a refusal leaves the file byte-identical, exit 2), a running service is restarted while a stopped one is not, an unreadable patch fails without touching the config. |
| `ContractQueueReaderTests` | The shared contract reader against a database the service’s own schema and repository wrote: counts, audit events, read-only enforcement, and garbage surfacing its error on query — the drift test that keeps the GUI’s queries and the service’s schema one shape. |
| `IndexerConfigValidatorTests` | Every rule with the dotted path it must name, watch paths absolute/present, Daminion connection fields, and all problems reported in one pass. |
| `IndexerLoggingTests` | One daily `indexer-*.log` (never a second `synapic.log`), the configured level filtering it, and the Event Log guard that neither throws nor creates a source. |
| `ShutdownBudgetTests` | A real `Host` with the worker stops inside 10 s and logs both the started and stopped lines. |
| `SidecarSupervisorTests` | Readiness only after `/health` says ready, timeout, missing executable naming the searched paths, stop clears the pid record, restart, the idle rule (including pending work) and the orphan decision. |
| `ServiceCommandLineTests`, `ProgramDataSetupTests` | Control-mode parsing (unknown flag cannot fall through to service mode, `--status` is its own mode), `sc.exe` token sequences, SID-based ACL grants, and the password command storing ciphertext without printing the secret. |
| `JobStateMachineTests`, `IndexingQueueRepositoryTests` | All 25 state pairs (9 permitted, 16 forbidden), the enqueue matrix (Pending/InFlight no-op, Completed unchanged vs changed, Failed/Transient unchanged vs changed, mtime-only refresh, case-insensitive keys), due-retry promotion at claim time, `job_events` trails, recovery of InFlight rows, persistence across a reopened repository, activity from the queue, and eight parallel enqueues of one key producing one row. |
| `LocalFolderScanSourceTests` | A real fixture tree: the five supported non-empty files enqueued once, a second scan with **zero** hash reads (proven by a counting hasher), a touch costing exactly one hash and no enqueue, one changed byte requeueing, nested roots yielding one row each, a missing root counted while the other scans, cancellation leaving no batch, a `FileShare.None` writer skipped, and non-recursive mode. |
| `DebounceBufferTests`, `FileStabilityProbeTests`, `FolderWatcherTests` | Window extension per key, independent release, `ReArm` attempt counting, case-insensitive keys and 500 concurrent adds against drains; the probe under a held handle, a deleted file and a bad path; and real OS watcher events: one enqueue after the debounce, a writer-held file deferred until the handle closes, an unsupported extension ignored, recursion on and off, an overflow requesting a full scan, and stop releasing every root. |
| `DaminionWorkSourceTests`, `ScanSchedulerTests`, `StatusCommandTests` | The selection (scope + untagged fields) reaching `FetchItemsAsync`, `daminion|<id>` keys with `srv:` fingerprints, an unreachable server leaving the queue untouched, id-less items skipped, missing/undecryptable passwords and unsupported scopes as typed configuration errors; immediate-then-interval scheduling with a failing source isolated; the queue-backed activity adapter and its 1 s cache; and `--status` printing every state plus the five newest failures, `no queue yet` when absent, and leaving the database mtime and directory contents unchanged — plus `--status --failed`, which lists more rows (up to 50, or `Failures: none`), each with its key, attempt count and a one-line error preview. |
| `BatchExecutorTests`, `PipelineHarness` | The executor seam: two local images tagged once and `Completed` with the tags on disk, a failed metadata write retried in 60 s, a locked file classified transient rather than corrupt, the observed maximum parallelism equalling `maxConcurrentItems` (measured, not inferred), an empty queue as a no-op, and cancellation returning every claimed row to `Pending` — all over a real temp SQLite queue, real JPEG fixtures and a canned sidecar. |
| `WriteBackPolicyTests` | `overwriteExistingTags` at the write seam: a fully tagged file completed with no `/tag` call and the `existing tags preserved` event, a partially tagged file filling only the empty selected fields, overwrite writing every selected field while keywords still merge (the pinned limitation), a disabled field never written, and the post-write fingerprint refresh that makes a rescan enqueue nothing. |
| `JobOutcomeClassifierTests` | Every row of the spec's §8 table in one place: a missing error as transient, lock contention after 60 s, decode/corrupt/`InferenceApiException` as permanent, catalog network/timeout as a five-minute pause (catalog items only), an auth failure as permanent, a killed sidecar's `HttpRequestException` as a restart rather than a catalog pause, a live sidecar's timeout as transient, and the third unknown failure becoming permanent. |
| `ResilienceTests` | The five §8 scenarios through the executor: a crash between items restarted with the affected row retried (`attempts == 2`), a sidecar that will not restart leaving rows transient with a five-minute due time, a file held open during the write retried after 60 s once released, a corrupt JPEG failed permanently on the first attempt while the batch continues, and an unreachable catalog (a real client on a closed port) pausing the run with every row back in `Pending` and no claim inside the window. |
| `ParityTests` | TAG-04 over persisted metadata: one fixture image through the desktop's `RunAsync` and the indexer's `BatchExecutor` with the same template produces the same category, description and keyword set, and the same happens with `writeDescription=false`. |
| `RealSidecarKillTests` | Opt-in (`SYNAPIC_TEST_REAL_KILL=1`): the production `InferenceSidecarService` launches the built bundle, the batch starts, the process is killed during the first in-flight `/tag`, and the supervisor restarts it (new pid) with every row `Completed`, at least one retried and the tags read back from disk. Skips with the reason when the bundle or cached model is absent. |
| `DaminionLiveAcceptanceTests` | Opt-in (`Category=Live`, `[SkippableFact]`): the live round trip — the resolved scope reported before any write, 25 items tagged through the real executor and the real client, the catalog read back per item (verifier + exact tag values), the same ids written again with one copy of each keyword, and a real firewall outage pausing the run and it resuming by itself. Skips with its reason when there are no credentials, no outage helper, or too few items. See [Live catalog round trip](#live-catalog-round-trip-phase-10). |

---

## `tests/Synapic.Main.Tests` (xUnit + Avalonia.Headless)

| File | What it pins down |
|------|-------------------|
| `ContractsRoundTripTests` (Shared project) | DTO ↔ JSON field-name stability against the frozen wire protocol. |
| `SessionTests` | `Session` stats/`ResetStats` and the `ValidateForStep2/3` gates. |
| `ConfigServicePersistenceTests` | `ConfigService.Save`'s document-merge promise (unknown keys survive, known sections are replaced, the write is atomic), plus the start screen's source round trip: a persisted folder/type comes back through `ApplyStoredSource` on the next launch. |
| `SourceSettingsPersistenceTests` | The active source survives a restart through the registry store launch reads: the type and folder round-trip, a missing type falls back to `local`, a chosen folder (and the subfolders switch) comes back, choosing the catalog comes back and is what reconnects, switching back to the folder tab wins, and the shell's shared source step really writes through the store `App` passes it. |
| `MainLayoutShellTests` | The shell through the real compiled window: the sidebar's five rows in the mock-up's order (Settings, Tagging, Deduplication, Similar images, Upscaling) with no dashboard and no Back/Next/StartOver bar, a row switching the view and lighting only the open one (the Similar row opens its own adapter and leaves Dedup's unlit), the icon-only collapse below the width threshold, and the engine form living on the Settings view instead of in a processing view. |
| `WorkflowOrderTests` | The same shell read as a whole: Settings first and then the operations with no numbered sidebar, the Settings row returning from every view with the views' state intact, the Settings view hosting every configuration form, no view rendering a view-model type name as text, actionable guidance on an empty tagging run and before the first report, the shared run bar rendering the state it binds (each mode's own Stop/Pause), exactly one run surface per processing view, and each processing view reporting the settings configured on Settings. |
| `RouteSplitTests` | The entry point as the sidebar defines it: the app opens on the Settings view with nothing open (`Shell.Current` null), that view carries its configuration sections (`SectionDataSource` … `SectionSimilar` … `SectionUpscale`), each sidebar row opens its own processing view and only that one, the dedup run is gated without a source and says why, the dedup view reads the catalog scope off the shared source, entering a view is free while only the run is gated, the Daminion connect form lives on the Settings view and nowhere else, the source form lays out above the engine section, and Home returns to Settings with the views' state kept. |
| `RetiredUiConceptTests` | The objection to the redesign's four retired concepts coming back: at the source level (no numbered step heading or retired wording, no `navMode` / `dashboardPanel` chrome class, no binding to a deleted `WizardViewModel` member, no settings dialog view type) and in the rendered window (nothing a user can read), across every `.axaml`. |
| `UiLayoutAuditTests` | Geometry on the real views at five audited sizes: the Settings view's seven tabs and the processing views lay out with no overlapping or clipped control, and with the source summary above the output. |
| `EmptyBranchInventoryTests` | The drift check behind `docs/ui-design.md` §10 criterion 6: every row of [`ui-empty-branches.md`](ui-empty-branches.md) that names a test resolves to a class and method that exist, a `Named`/`Partial` row with no test and an unknown status fail, and the table cannot shrink below the branches it was built from. One fact runs the same check over a table whose tests are gone, so the failure path is pinned rather than assumed. |
| `HelpScopeTests` | The context-aware half of the help: `HelpScope.Topic` annotations resolve to the nearest annotated ancestor, an anchored annotation beats a coarser one, a rejected topic falls through to the next scope up, annotations are normalized, F1 opens the focused control's scope rather than the fallback (and falls back to the sidecar topic when nothing covers the focus), and every annotation in the views points at a topic, anchor and `Synapic.hhp` `[FILES]` entry that exist. |
| `OperationContractTests` | The Phase 1 operation contract: the exact nine template members, no datasource member on a mode, the adapters' keys and titles, `Run` being the wrapped mode's own command, `IsRunEnabled` mirroring that command's `CanExecute`, a source-gated mode starting disabled with a reason, adapters re-raising their members when the wrapped mode changes, and — the similar-image case — the similarity search being its own operation over the same adapter (distinct key, title, description, help topic and threshold). |
| `IndexerServiceViewModelTests` | The Service tab’s view model against fakes: the status→button mapping, the watch-folder round trip through the one elevated save (patch JSON asserted field by field), the queue view with failures and activity, and every missing-something branch — no service, no executable, no queue, not Windows — each said out loud. |
| `SettingsViewTests` | The Settings view: its four-section app-preferences form, the theme and log-level changes applying to the running app and persisting, the diagnostics toggle showing and hiding the shell log view, defaults seeding a session with no saved engine state, the device auto-selection in a file without defaults, the inference-server section operating the server through the shell, the settings search filtering to matching sections and jumping to the first, every suggestion finding at least one section, and About reporting the version and links. |
| `OperationGatingTests` | The tagging run's own gate: Start stays disabled until at least one tag field is on, while opening the tagging mode is never blocked by the tag fields (the wizard's half of that gate is gone with the step chain). |
| `OperationLockWiringTests` | The run lock's re-arming: a running batch locks navigation and Home/A row unlock when it stops — the host raises `NavigationLockChanged` when a run's `IsRunning` flips, without which a running batch left Home clickable. |
| `StepDedupViewModelTests` | The dedup step's own behaviour: the local/Daminion source switch and its action list, the per-item keep checkboxes, the oldest/newest/smallest/largest auto-select rules that drive them, `BuildOptions` mapping and clamping, the server-hash rule, the scan's guidance lines for an empty folder, images without duplicates, unreadable images, a failure and a cancellation, what the shared run bar says while a catalog walk (`Fetching catalog: N/M items`, page number, per-page ETA, and no invented ETA without a scope total) or local hashing runs, the retrieval diagnostics block (count/page total/pages/raw scanned/kept side by side in the rendered view, a spelled-out count-vs-kept difference, the two action buttons and the re-walk appearing only on a disagreement, hidden for a local source while the last report is retained), the report export writing its numbers and comparison to a chosen file, and the live threshold slider — regrouping the retained fingerprints without a rescan, coalescing a burst of values into one recalculation, carrying the keep-set across a retune, doing nothing before a scan, and the two-way binding through the compiled view. |
| `ApplyBusyStateTests` | The dedup Apply busy state: `IsApplying` wraps the real apply duration, a second Apply is refused while one runs, Stop clears the busy state and cancels the operation, the state is idle before a run, and the rendered apply card really shows the bar and Stop. |
| `DedupScanHandlerTests` | The catalog scan's two cheap paths: a server-hashed item is grouped with no transfer at all (no preview either), an item without one is fingerprinted from the server's preview (with the catalog's `fileSize` in the row, not the preview's), a missing preview, a missing dimension and an undecodable preview each fall back to the original, and a preview-hashed row has no capture date (the review list fills it in). Plus the server-hash shortcut itself: an item carrying a server `hashCode` is grouped without downloading, identical server hashes form a group, Hamming mode bridges values inside the threshold, items without a hash fall back to download-and-hash, mixed scopes split between the two, the server's `fileSize` survives the no-download path, a download or hash failure is counted rather than thrown, and a non-Daminion item is a soft failure. Plus the similarity profile's rule: `UseServerHashes = false` downloads and hashes even a server-hashed item, a negative `hashCode` is zero-extended to 32 bits (`-1` → `0xFFFFFFFF`, and 1 of 32 bits apart from `int.MaxValue`), and the hamming budget follows the 32-bit width. |
| `UpscaleRouteTests` | The third route: the ✨ Upscaling card carries its command through the real compiled window, the upscale route opens on Datasource with Engine/Process/Results and the dedup tab gated off (and the ✨ tab shown), Next skips the wizard onto `Wizard.Upscale` with the Step 1 source read back, a source-less datasource is refused, and Home → re-enter works. |
| `StepUpscaleViewModelTests` | `BuildOptions` mapping (dropdown indices → wire values) and every contract clamp (workflow/factor/precision/format/JpegQuality 70–100/denoise 0–1/sharpen 0–2); denoise enabled only for `balanced` and precision disabled for `fast`; `SourceReady`/`SourceSummary` without a step, with a missing folder and with a real one; Start gated by source **and** sidecar; and a full Start run over an empty folder asserting the shared-kernel summary plus the original app's `Upscale run started.` / `Parameters: …` / `Done.` log lines (locale-independent decimals). |
| `WorkflowRunnerTests` | The shared batch kernel: one throwing item fails alone (summary + `Failed:`/`Batch finished:` log lines), soft outcomes count as failed without an error line, `MaxDegreeOfParallelism: 1` never overlaps items, cancellation propagates as `OperationCanceledException`, a paused run holds every item until resume, progress walks 0 → total, local fetch picks only image extensions (honouring recursion), `CountLocalImages` reports exactly what that fetch would process (extension case included, missing folder → 0) and a missing folder throws `DirectoryNotFoundException`. |
| `DaminionVersionControlRequestTests` | The upscale catalog dance over a `TcpListener` stub: CheckOut/UndoCheckOut post `{"Ids":[…]}` to `/api/VersionControl/…` (refusal → `false`, no ids → no request), and CheckIn is multipart with `id`/`comment`/`file`, retried **without** the comment when the first attempt is rejected (400), failing when both attempts are refused, and never called for a missing file. The stub de-chunks Refit's chunked multipart bodies. |
| `SidecarUpdateCheckTests` | The always-on startup GitHub check against a fake releases feed: a fresher `nightly` beats an older tag (newest-wins), a local build newer than the release is never offered, a missing executable is reported as available, a RID is only offered from a release that actually carries it, manifest-less releases are never offered, an offline GitHub degrades to `null` instead of throwing, and the download path takes the same release the check offered (verified via `SHA256SUMS.txt`). |
| `SidecarBuildStopRestartTests` | The "build dies with PermissionError" fix: a direct Build stops a running server and restarts it afterwards, a build with no running server touches no server state, and a build output locked by this process fails in seconds with a clear message — before a build ever starts. |
| `Step1DatasourceTests` | `ConnectCommand` enablement and `CanExecuteChanged` notifications as credentials change; the source gates (`HasUsableSource`, the two lights' brushes/text, and their property notifications) for a folder, a folder that does not exist, typed-but-unconnected Daminion credentials and a live session; and the automatic record count — a folder change counts without a button (debounced), the recursive toggle recounts, the launch sequence counts a stored folder without signing anywhere, and a stored Daminion source with no credentials does not sign in. |
| `Step2EngineTests` | Engine view-model state → session propagation. |
| `TagInstructionTests` | The editable tag instruction: blank means "the sidecar's built-in prompt" and stays null on the wire, "Use built-in instruction" loads the sidecar's own text (once, cached) and never overwrites the box with an empty answer, reset clears back to the default, and the value round-trips through the registry. |
| `Step2PromptBindingTests` | The Step 2 prompt editor through real compiled XAML: the box two-way binds to `UserPrompt`, both buttons carry their commands (a mistyped Avalonia binding is otherwise silent), the preset combobox lists the history and fills the box when one is chosen, and a real Delete key press removes the highlighted preset. |
| `SystemPromptPresetStoreTests` | The system-prompt history file: object and bare-array shapes, a missing or corrupt file degrading to an empty list, newest-first ordering, trim/dedupe, the `MaxPresets` cap, and removing an unknown entry as a no-op — all against a temp file, never `%APPDATA%`. |
| `SystemPromptPresetTests` | Step 2's preset behaviour: committing a prompt (leaving the step) remembers it, blank input is never saved, the highlight follows the box, Delete removes the highlighted preset and clears the box when that prompt was the one in use, and deletions persist to the next session. |
| `ProcessingEtaTests` | ETA math (`WorkflowRunner.EstimateProgress`, and `EstimateCatalogFetchProgress` — average page cost × pages left, no ETA without a scope total or before the first page) and `ProcessAll` propagation from Step 1 into the selection. |
| `TagFieldSelectionTests` | Which returned fields are written, per `TagFieldSelection` permutation, through a fake sidecar + capturing metadata writer. |
| `RepairedReplyStatusTests` | The repaired-reply status: an item the sidecar had to put back together reports `Success (repaired)` with a run-log line naming what needed repairing, a clean one stays `Success` with no such line, `Retry Failed` leaves repaired items alone, verification carries the marker into `Verified (repaired)`, the report summary counts them (and only shows the count when there is one), and a sidecar too old to send `reply_repairs` at all reads as nothing to repair — the absent member deserializes to null and once crashed every item of the batch. Also the retry: a `reply_retried` item logs that the model was asked again, an ordinary one says nothing, and an absent flag (older sidecar) reads as "not retried". |
| `PauseTests` | Pause/resume semantics (running items finish, queued items wait) and cancellation wins. |
| `MainWindowPopulationTests` | Main-window/shell population and server-detection wiring. |
| `ListAutoScrollTests` | Teardown safety: the view model stops feeding the UI log once the shell detaches it, and a list that is not attached is never scrolled (the "Invalid Arrange rectangle" crash on close). |
| `ServerDetectionTests` | `MainWindowViewModel.DetectServerAsync`, buildable RIDs, the variant panel, and `/health` download-status application (`ApplyDownloadStatus`). |
| `SidecarBuildProgressTrackerTests` | Raw build-output → stage/percent mapping. |
| `DedupServiceTests` + `DedupTestHarness` | pHash/dHash/aHash grouping and thresholds on generated images, and a real scan retaining its fingerprints on the result (the live slider's raw material, re-groupable without touching the files again). |
| `DaminionCaptureDateTests` | The catalog capture-date reader behind preview hashing: the nested `ItemData/GetAll` payload (`propertyName`/`propertyValue`, plus multi-value `values`) flattens to tag pairs, "Date Taken"/"DateTimeOriginal" win over an import "Created" date, a modified/expiry date is never taken as a capture date, the server's own dotted `2025.12.27.11.23.52` rendering parses (as do ISO and culture shapes, with an offset honoured), an empty or unparseable value is null rather than an exception, and the first non-empty value for a tag wins. |
| `DedupAlgorithmEquivalenceTests` | The optimised comparison against the definitions it replaced: grouping equals a pairwise union-find sweep for pHash at 0.50/0.85/0.90/0.97/0.99/1.00 and for 32-bit server hashes in hamming mode (group membership, order and per-item scores), a chained A–B–C group stays one group, a set whose fingerprints all share one region is still fully compared (the balanced pigeonhole split), and the shipped pHash equals a full 32×32 DCT reference on structured, gradient, edge, detail, portrait and small images. Plus the parallel local scan producing the same fingerprints and the same ordered groups as a serial one (an original and its copy grouped together), and a featureless image hashing stably with its copies still matching. |
| `DedupGroupingScaleTests` | Grouping at catalog scale stays inside a time ceiling — 5,000 and 20,000 fingerprints at 0.90, and 20,000 byte-identical copies, the case a pairwise union made quadratic (measured 14.3 s before the bucketed search, 13 ms after). |
| `DaminionVerifyMetadataTests` | The "Verify Daminion writes" read-back on the payload server 11.x actually returns: a written category, keyword set and description are found through the tag-name/value pairs (the JSON-member matcher this replaced could only ever find `propertyName`/`propertyValue` and reported every field missing), a keyword must match a whole value rather than a substring, a missing field is named, and a tolerance check keeps the case-insensitive tag-name matching. |
| `DedupFingerprintRestoreTests` | The restart cache: a saved scan round-trips its hashes and row metadata, belongs to one operation and source, ignores a corrupt or other-version file instead of throwing, is deleted per source, and is pruned to the newest eight per operation. Through the view model: a new instance reopens the last scan with no second scan, restores the slider live and retunes from the saved hashes, refuses a scan saved under another algorithm or another folder, takes the slider away when the source moves, drops the file after Apply, and the shipped container resolving the shell hands the registered store to both searches (a saved scan reopens through the real DI wiring). |
| `SimilarSearchTests` | The Similar images operation as its own thing: the two profiles over one implementation (each with its own key, title and wording), the looser 0.84 starting threshold — 10 of the 64 bits where duplicates allow 6, and the `BuildOptions` the scan actually runs with — the band between the two thresholds (fingerprints 10 bits apart group only under the similarity search, 12 bits apart under neither), transitive chaining (A-B and B-C is one group where the duplicate scan finds nothing), the per-fingerprint defaults behind those numbers (pHash 0.90/0.84, dHash 0.96/0.90, aHash 0.98/0.93 — measured over 220 same-photo and 462 different-photo pairs of real photographs, with the budget floored so each default keeps its bit count), that an untouched threshold follows the algorithm it is switched to while a tuned one is left alone, and that a store holding a saved algorithm but no threshold falls back to *that* algorithm's default, and the two registry halves: each form restores its own algorithm, threshold, keep-set and move destination while leaving the other's at its defaults (those last two facts return early off Windows, where the store is a no-op). |
| `MetadataWriterTests` | XMP write/read round-trips for JPEG and PNG. |
| `EndToEndRoundTripTests` | Full path where the environment allows: real sidecar executable and/or a live Daminion (see below). An absent or stale sidecar bundle is reported as a skip with the reason, not as a pass. |
| `DaminionBaseUrlTests` | `DaminionApiClient.NormalizeBaseUrl` behaviour. |
| `DaminionCatalogGuidTests` | `ExtractCatalogGuid` across every observed `GetCatalogGuid` shape (bare string, object key, Daminion 11 envelope, unusable payloads). |
| `SidecarStalenessTests` | `InferenceSidecarService.DescribeStaleness` — flags an exe older than `src/Synapic.Inference`, ignores non-bundle files, reports "unknown" without a repo. |
| `DaminionLayoutParsingTests` | Layout payload parsing / tag-GUID extraction. |
| `DaminionIndexedTagValuesRequestTests` | The all-parameters-required routing rule for `GetIndexedTagValues`. |
| `DaminionDeleteRequestTests` | The catalog delete posts to `/api/MediaItems/Remove` with `{ids, delete:false}` (never `ItemData/BatchChange`, which answers success without removing anything) and reports failure unless a follow-up `GetByIds` shows the ids gone. |
| `DaminionFlagRequestTests` | "Reject in catalog" posts `ItemData/BatchChange` with the Flag tag's guid and the text value `Rejected` (`id:0`, `remove:false`), verifies via a `GetByIds` read-back that accepts the numeric and text flag forms, fails (no request) when the layout has no Flag tag, and no-ops on an empty id set. |
| `StepDedupRejectActionTests` | The apply card's reject action over a connected stub: it offers delete + reject on a Daminion source, applies the reject without ever prompting (reversible, unlike delete), trims exactly the acted-on rows, drops groups reduced to a single image and keeps the rest, and reports `Rejected N item(s)…`. Plus `RunLogVisibilityTests`: the shared run bar's log-box visibility follows the first and later log lines (the grey strip under the action row is gone while nothing has been logged). |
| `DaminionSelectionBehaviorTests` | The catalog selection and paging contract used by every workflow, over a loopback stub: saved-search filters, max-items across full and partial pages, process-all walking past a short page, the raw-page cursor for collection/saved-search scopes (a filtered-empty page is not the end), the repeated-page guard, the count and the pages sending one and the same status clause, an unfiltered walk sending no `*` search, a whole-scope walk taking its total from the scope count rather than a page `totalCount` that claims the whole catalog, the whole-scope walk dedup/similar use — 1,500 records requested through `FetchEntireScopeAsync` with `MaxItems = 100` and `AutoPaginate = false`, page progress reported at 500/1,000/1,500, the tagging path still capped, a page-serial server (page 0 answers, then only `index` as a page number does) still yielding the whole 1,500-record scope through the offset-then-serial switch — the one-shot retrieval diagnostics report (count, page total, pages, raw scanned and kept together; an empty scope still reporting zeroed numbers), the `ignoreScopeCount` re-walk mode (page totals steer the bar while the count is still reported), and the dedup screen itself grouping a pair that only exists on the second page, leaving the shared run bar on the hashing phase's final state and holding the last retrieval report — plus the re-walk action re-running the walk, switching the report to page totals, and reverting to the count on the next normal scan. Also the exact-hash result (slider hidden, "compared exactly" notice shown, a plain Scan still re-grouping instantly) and the scan-generation gate: a catalog whose guid changed under the same URL refuses the apply before any `MediaItems/Remove` is sent. |
| `DaminionQueryLoggingTests` | The retrieval log a mismatch is diagnosed from: the count request carries the scope and text clauses and its response line carries the raw count/totalCount/data fields, every page request names its index, size, scope, search/queryLine/operators and its response reports raw/eligible/returned and the page's `totalCount`, and a page total that contradicts the scope count is logged as a warning instead of silently moving the progress bar. |
| `DaminionTempDownloadTests` | The download folder never grows: a truncated download leaves no partial file, a successful one lands in the configured directory and is the caller's to delete, the stale sweep removes only files older than its cutoff (and the cutoff is longer than the HTTP timeout), and a missing directory is a no-op. |
| `DaminionConnectionStoreTests` | Step 1 registry persistence incl. `ProcessAll` (Windows-only). |
| `EngineSettingsStoreTests` | Step 2 registry persistence (Windows-only). |
| `DaminionLiveSessionTests` | Opt-in live Daminion (`Category=Live`, `[SkippableFact]`): login, filtered fetch, count, logout — skipped with the reason when credentials are missing or the server is unreachable. |
| `SynapicLogTests` | Logger initialisation, file sink, UI sink ring buffer, per-run rotation into `logs/archives` and pruning to `MaxArchivedLogs`. |
| `CrashReporterTests` | Crash capture → report file + session-log snapshot. |
| `TelemetryServiceTests` | Opt-in counters and the disabled no-op path. |
| `RuntimeCheckTests` | .NET runtime detection helpers. |
| `HelpServiceTests` | Where help is found (compiled `.chm` on Windows, HTML topics elsewhere, a source checkout last), the `ms-its:` topic launch, falling through when a target will not start, topic-name normalisation, the `HelpTopics` → `Synapic.hhp` `[FILES]` drift gate, and the Help/<kbd>F1</kbd> commands. |
| `TestFakes.cs` | `FixedTagSidecar`, fake inference sidecar, and shared stubs. |
| `TestAppBuilder.cs`, `SynapicLogSerialCollection.cs` | Headless app bootstrap; serialises tests that mutate the global logger. |

### Opt-in / environment-gated tests

Some tests only run when the environment is available; they self-skip
otherwise, so CI (ubuntu) stays green:

| Test | Requires |
|------|----------|
| `DaminionLiveSessionTests`, `EndToEndRoundTripTests` (Daminion half), `DaminionLiveAcceptanceTests` | `SYNAPIC_TEST_DAMINION_URL`, `SYNAPIC_TEST_DAMINION_USERNAME`, `SYNAPIC_TEST_DAMINION_PASSWORD` (or `build/acceptance-daminion.ps1`, which reads the saved Windows connection) |
| `DaminionLiveAcceptanceTests.Live_outage_pauses_and_the_queue_completes` | `SYNAPIC_TEST_DAMINION_BLOCK_DIR`, pointing at the directory an elevated `build/block-daminion.ps1` is watching |
| `EndToEndRoundTripTests` (sidecar half) | a built sidecar executable (`artifacts/<rid>/synapic-inference*` or bundled next to the app) |
| `RealSidecarKillTests` | `SYNAPIC_TEST_REAL_KILL=1`, a built sidecar bundle and `LFM2.5-VL-1.6B` in `%LOCALAPPDATA%\Synapic\models`; loads the model twice |
| `DaminionConnectionStoreTests`, `EngineSettingsStoreTests` | Windows (registry) |

---

## Live catalog round trip (phase 10)

`tests/Synapic.Indexer.Tests/DaminionLiveAcceptanceTests.cs` is the only suite
that writes to a real catalog, and it is never part of a hermetic run:

```powershell
# The whole live round trip, with the credential the app already saved
# (HKCU\Software\Synapic\Daminion, DPAPI) — nothing is printed or committed.
build/acceptance-daminion.ps1

# One fact
build/acceptance-daminion.ps1 -Filter "FullyQualifiedName~Live_round_trip"
```

`acceptance-daminion.ps1` resolves `SYNAPIC_TEST_DAMINION_URL/_USERNAME/_PASSWORD`
first and falls back to the saved connection, then hands the values to the child
`dotnet test` process through its environment only. It exits 3, naming the
missing piece, when neither source is complete.

What it writes: metadata (category, one keyword, description) on the items of
the selected scope — 25 for the round trip, a handful more for the outage. It
deletes nothing, sets no flags, and does not change catalog administration. Runs
are marked with a dated keyword (`synapic-live-YYYY-MM-DD`) so a later run can
find what this one wrote; the tags stay, because tagging is the product's output,
not test residue.

The scope is resolved against what the server holds, not trusted from a constant:
the named saved search if the server enumerates it **and it yields records**,
then a saved search by name, then the catalog. The fallback is printed with the
reason — a structured saved-search query that names an id the server does not
have can quietly answer with the whole catalog, and a saved search with the
right number can be an unrelated one that holds nothing (one catalog has a
saved search 16 of its own, "No Description", with zero records). Either way a
run on the whole catalog is never reported as a run on the scope it was asked
for.

The round trip does not require items that are already empty. On the server this
was built against, `keywords:none` is ignored by both the item endpoint and the
count, so an "untagged page" is really the catalog head. It selects real items
from the resolved scope, snapshots what they carry, and asserts that the written
keyword and category land exactly once while every value already there survives
the write.

The outage fact needs a second, elevated shell, because "the catalog went away"
is a real Windows firewall block on this machine's outbound TCP, not a closed
port:

```powershell
# elevated
build/block-daminion.ps1 -WorkDir artifacts/phase-10/block -TimeoutSeconds 300
```

It waits for `block.request`, applies the rule, proves a new connection now
fails, writes `block.ack`, waits for `unblock.request`, proves the server answers
again, removes the rule in a `finally` and writes `unblock.ack`. Without
`SYNAPIC_TEST_DAMINION_BLOCK_DIR` pointing at that directory the outage fact
skips, naming the helper — it will not pretend a closed port is an outage.

The rule covers **every address the name resolves to, both families**, and the
proof is a connect *by hostname*: a host that answers on IPv6 as well stays
reachable through the address the rule left alone. The helper records
`open_addresses_after_apply=` so a partial block shows up in the ack instead of
being inferred, and the fact itself re-proves the outage with a fresh client
before it touches the queue.

The transcript of the acceptance run is committed at
[`.planning/phases/10-live-daminion-round-trip/10-ACCEPTANCE.md`](../.planning/phases/10-live-daminion-round-trip/10-ACCEPTANCE.md).
The raw console output (`artifacts/phase-10/live-*.log`) is written by the script
and is not committed — `artifacts/` is ignored.

---

## `tests/Synapic.Shared.Tests`

`ContractsRoundTripTests.cs` — serialises every DTO through
`SynapicJsonContext` and asserts the exact snake_case wire names and shapes.
Any contract change must update these and
[`sidecar-protocol.md`](sidecar-protocol.md).

---

## `tests/Synapic.Inference.Tests` (pytest)

`conftest.py` puts `src/Synapic.Inference` on `sys.path` (flat imports),
forces `SYNAPIC_DISABLE_AUTO_DOWNLOAD=1` and `SYNAPIC_DISABLE_WARMUP=1`, and
resets the download registry between tests.

| File | What it pins down |
|------|-------------------|
| `test_service_contract.py` | Served routes via `TestClient`: `/health` shape and status, `/models/list`, `/config` GET/PUT (incl. model-switch unload), `/prompt` returning exactly the instruction `/tag` falls back to, `/tag` validation (422/404, incl. that `user_prompt` is accepted, that whitespace means "built-in", and that an over-long instruction is rejected), `/shutdown`. |
| `test_upscale.py` | `POST /upscale`: validation (404 missing file, 422 non-image and out-of-range workflow/factor/precision/denoise/sharpen/format/quality), the fast Lanczos workflow end-to-end (dimensions, JPEG output, `{stem}_upscaled` naming, explicit output path, sharpen applied), the AI workflows through a monkeypatched `_load_model` (quality x2 model id, balanced 2x runs the x4 model then downsamples, denoise=0 is byte-identical to pure Lanczos, alpha channel survives), and the `upscaler.py` internals (output-target resolution, overwrite-free suffixing). |
| `test_model_loader.py` | Compatibility rules, task suggestion, fuzzy label matching, cache heuristics, state/download-progress — the byte bar mirrored from the hub's tqdm, a partial-progress snapshot, an already-downloaded model skipping the network, a failure recorded without flipping the server into the error state, and the completion TTL both ways (a finished or failed download is reported inside it and hidden past it, on a hand-wound clock rather than a patched TTL racing Windows' ~15.6 ms `time.time()`) — and concurrent model loads (the build lock). |
| `test_keyword_scoring.py` | Softmax constructions, sum-to-one invariant, thresholding, and the `ScoreResult` contract. |
| `test_tag_extractor.py` | `json_utils` extraction/repair — including the payload shapes a small VLM gets wrong (envelopes, capitalised or synonymous keys, a literal newline in a string, truncation inside a string or array, a member the model ran into the next one without closing its quote or its comma, JSON delivered as a string) and the unrecognised payload that is deliberately left as raw text — plus the repair *reasons* a caller receives (`repairs`: `missing member separator`, `truncated payload`, nothing for a clean reply, and nothing left behind by a search that failed), the reply verdicts behind the retry (`classify_reply`/`reply_is_malformed`: clean, repaired, unreadable — including that a caption is "unreadable" because it is not a payload, and that `reads_better_than` only adopts a strictly better retry), `vlm_reply_text` across pipeline shapes, Title Case, and extraction for classification / zero-shot / image-to-text, including limits and de-duplication. |
| `test_inference_engine.py` | The VLM message shape from `build_vlm_messages` (every turn carries content *parts* — a bare-string system turn raises `TypeError: string indices must be integers` inside transformers 5.1 and failed every image of a run) plus an end-to-end `run_inference` through a fake pipeline with and without a system prompt; and `_generation_kwargs`: clears the conflicting `max_length`, pins greedy decoding, does not mutate the pipeline's config, and falls back for unknown pipeline shapes (the transformers-warning fix). Also what `run_inference` reports back to the host in `reply_repairs` — empty for a clean reply or a plain caption, `missing member separator` when a reply had to be rebuilt, `truncated payload` when `max_new_tokens` cut it off — and the log line that names the repair. And the one retry a malformed reply gets: a broken or truncated first reply is answered by a second ask (which shows the model its own reply verbatim), the second reply is used only when it reads better, a reply that stays broken keeps the first one, a clean reply is asked once, a captioner is never asked twice, a custom instruction that asks for JSON still is, a prose instruction is not, and a retry that fails to generate leaves the item's tags intact. |
| `test_tag_prompt.py` | `DEFAULT_VLM_USER_PROMPT` names every key `tag_extractor` reads, bans code fences and surrounding text, demands double quotes, asks for the whole object — not just the description — on one line, and shows the object shape. These are format guards, not prose review: the wording they pin took measured strict-JSON compliance from 0/13 to 13/13 (harness in `build/check-tag-prompt.py`), so dropping one sends every reply back through the rescue path. |
| `test_check_installer_appid.py` | The Windows installer's AppId guard in `build/check-installer-appid.py`: Inno's `{{` escaping, comment and section handling, duplicate/constant/absent directives, and the refusal when the script disagrees with `build/installer-appid.txt` — including that the repository's own two files agree. |
| `test_check_sidecar_variant.py` | The CPU/CUDA payload rules in `build/check-sidecar-variant.py`: a CPU bundle must contain no CUDA runtime binaries (but torch's CUDA *python* modules are not leaks), a CUDA bundle must contain `torch_cuda.dll` plus cudart/cublas/cudnn, and optional extras may be absent. Runs without torch or PyInstaller installed. |
| `test_check_load_dtype.py` | The packed-policy rules in `build/check-load-dtype.py`: a bundle whose `_load_dtype` is missing, answers `"auto"` on CPU, raises, or is never called by `_construct_model` fails with a message naming the device; the shipped policy (cpu=`float32`, cuda/mps=`auto`) passes. Runs without torch or PyInstaller installed. |
| `test_warmup.py` | Cold start: `_wait_for_model_ready` returns at once when idle, blocks until an in-flight load finishes, times out into a 503; `_warm_up_model` skips itself when disabled or when the weights are absent, never leaks an `error` status on failure (the host treats that as fatal), and reports the model when it succeeds. |
| `test_protocol_doc.py` | `build/generate-protocol-doc.py --check`: the committed `sidecar-protocol.md` matches the live OpenAPI schema + the C# DTOs, so the contract doc cannot drift. |
| `test_split_release_asset.py` | `build/split-release-asset.py`, which makes the >2 GiB CUDA sidecar publishable: parts rejoin byte-for-byte, parts are exact ranges, the oversized original is not left in the upload directory (one invalid file fails the whole release), stale parts from an earlier run are cleared, a cap at/above GitHub's limit is refused, and the emitted helper (`.bat`/`.sh`) names every part and reproduces the file. |

---

## `tests/Synapic.Integration.Tests`

Socket-level contract tests for the real `InferenceApiClient` against a stub
HTTP server (`SidecarHttpContractTests`): /health payload binding, the
503-then-retry /tag semantics, error-detail surfacing, models/config
round-trips, and shutdown. No Python or GPU needed. Real-environment
end-to-end coverage (a genuinely built sidecar) lives in
`EndToEndRoundTripTests` inside the Avalonia suite.

---

## CI

`.github/workflows/build.yml` runs, in order:

1. **`python-tests`** — pytest with the light dependency set (no torch), then
   `python build/generate-protocol-doc.py --check`, which fails if
   `docs/sidecar-protocol.md` is stale or the FastAPI request models and the C#
   contract DTOs disagree.
2. **`dotnet-tests`** — `dotnet build` + `dotnet test` on Release.
3. **`build`** — a 3-RID matrix (win-x64, linux-x64, osx-arm64): fetch Python,
   install deps, `check-lfm2vl-tie.py`, PyInstaller (which itself runs
   `check-sidecar-variant.py`), publish the app, stage the bundle, and (Windows)
   build the installer.
4. **`sidecar-cuda`** — `main` pushes and manual dispatch only (PRs need the
   `build-cuda` label): builds `win-x64-cuda`, checks the installed torch is a
   CUDA 12.x build, boots the bundle and polls `/health`, and uploads the
   executable + SHA-256. See `packaging.md`.
5. **`installer-smoke`** — on `main` pushes only: installs the Windows setup
   silently, verifies the install path and that the .NET Desktop Runtime is
   present afterwards, then launches the app and asserts the `[runtime]` line
   appears in the log.

PRs use a path filter (`src/**`, `tests/**`, `build/**`, workflows, solution,
`global.json`, `Directory.Build.props`) so doc-only PRs skip the matrix.

`.github/workflows/release.yml` is not triggered by pushes to `main`; it runs on
`v*` tags and on manual dispatch (`gh workflow run release.yml -f
version=0.0.0-dryrun`), which builds and smoke-tests everything while skipping
the publish job. Nothing in this document's suites covers it — treat a release
change as untested until a dry run is green.

### Measurement tools (not run in CI)

Some behaviour can only be measured against the real model, so it is measured
by hand and then pinned by a unit test that runs in CI:

| Tool | What it measures |
|------|------------------|
| `build/check-tag-prompt.py` | Strict-JSON compliance of the tag prompt on the default model, per prompt variant (`AB_VARIANTS`/`AB_IMAGES` filter a run). The numbers it produces are quoted in `inference_engine.DEFAULT_VLM_USER_PROMPT`'s comment and guarded by `test_tag_prompt.py`. |

## Conventions when adding tests

- Never require the network or a Daminion server for a default-run test; gate
  it behind `SYNAPIC_TEST_*` and return early.
- Use the shared fakes in `TestFakes.cs` for the sidecar rather than hitting a
  real one.
- Python tests must import without torch/transformers installed.
- `TreatWarningsAsErrors` is on for C#, so a new warning fails the build.
