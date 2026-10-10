# Testing

Four test projects, run from `Synapic.Net.sln` and pytest. The suites are
deliberately hermetic: nothing needs the network unless you opt in with
environment variables, and the image fixtures are generated in temp folders.

```bash
# Everything .NET (Avalonia + Shared + Integration)
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

---

## `tests/Synapic.Main.Tests` (xUnit + Avalonia.Headless)

| File | What it pins down |
|------|-------------------|
| `ContractsRoundTripTests` (Shared project) | DTO ↔ JSON field-name stability against the frozen wire protocol. |
| `SessionTests` | `Session` stats/`ResetStats` and the `ValidateForStep2/3` gates. |
| `ConfigServicePersistenceTests` | `ConfigService.Save`'s document-merge promise (unknown keys survive, known sections are replaced, the write is atomic), plus the start screen's source round trip: a persisted folder/type comes back through `ApplyStoredSource` on the next launch. |
| `WizardNavigationTests` | Forward/back navigation, validation errors, step-entry side effects. |
| `RouteSplitTests` | The entry-point split: the app opens on the start-screen chooser, all three route cards carry their commands through the real compiled window (a mistyped Avalonia binding is otherwise silent), the cards stay disabled until the source panel has a usable source and enable when a folder is picked (with the folder light turning green), the Daminion connect form exists on the start screen and only there, the dedup route hides Engine/Process/Results and sends Next from Datasource straight to the dedup step with the source carried over, and Home returns to the chooser. |
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
| `ProcessingEtaTests` | ETA math (`WorkflowRunner.EstimateProgress`) and `ProcessAll` propagation from Step 1 into the selection. |
| `TagFieldSelectionTests` | Which returned fields are written, per `TagFieldSelection` permutation, through a fake sidecar + capturing metadata writer. |
| `RepairedReplyStatusTests` | The repaired-reply status: an item the sidecar had to put back together reports `Success (repaired)` with a run-log line naming what needed repairing, a clean one stays `Success` with no such line, `Retry Failed` leaves repaired items alone, verification carries the marker into `Verified (repaired)`, the report summary counts them (and only shows the count when there is one), and a sidecar too old to send `reply_repairs` at all reads as nothing to repair — the absent member deserializes to null and once crashed every item of the batch. Also the retry: a `reply_retried` item logs that the model was asked again, an ordinary one says nothing, and an absent flag (older sidecar) reads as "not retried". |
| `PauseTests` | Pause/resume semantics (running items finish, queued items wait) and cancellation wins. |
| `MainWindowPopulationTests` | Main-window/shell population and server-detection wiring. |
| `ListAutoScrollTests` | Teardown safety: the view model stops feeding the UI log once the shell detaches it, and a list that is not attached is never scrolled (the "Invalid Arrange rectangle" crash on close). |
| `ServerDetectionTests` | `MainWindowViewModel.DetectServerAsync`, buildable RIDs, the variant panel, and `/health` download-status application (`ApplyDownloadStatus`). |
| `SidecarBuildProgressTrackerTests` | Raw build-output → stage/percent mapping. |
| `DedupServiceTests` + `DedupTestHarness` | pHash/dHash/aHash grouping and thresholds on generated images. |
| `MetadataWriterTests` | XMP write/read round-trips for JPEG and PNG. |
| `EndToEndRoundTripTests` | Full path where the environment allows: real sidecar executable and/or a live Daminion (see below). |
| `DaminionBaseUrlTests` | `DaminionApiClient.NormalizeBaseUrl` behaviour. |
| `DaminionCatalogGuidTests` | `ExtractCatalogGuid` across every observed `GetCatalogGuid` shape (bare string, object key, Daminion 11 envelope, unusable payloads). |
| `SidecarStalenessTests` | `InferenceSidecarService.DescribeStaleness` — flags an exe older than `src/Synapic.Inference`, ignores non-bundle files, reports "unknown" without a repo. |
| `DaminionLayoutParsingTests` | Layout payload parsing / tag-GUID extraction. |
| `DaminionIndexedTagValuesRequestTests` | The all-parameters-required routing rule for `GetIndexedTagValues`. |
| `DaminionDeleteRequestTests` | The catalog delete posts to `/api/MediaItems/Remove` with `{ids, delete:false}` (never `ItemData/BatchChange`, which answers success without removing anything) and reports failure unless a follow-up `GetByIds` shows the ids gone. |
| `DaminionTempDownloadTests` | The download folder never grows: a truncated download leaves no partial file, a successful one lands in the configured directory and is the caller's to delete, the stale sweep removes only files older than its cutoff (and the cutoff is longer than the HTTP timeout), and a missing directory is a no-op. |
| `DaminionConnectionStoreTests` | Step 1 registry persistence incl. `ProcessAll` (Windows-only). |
| `EngineSettingsStoreTests` | Step 2 registry persistence (Windows-only). |
| `DaminionLiveSessionTests` | Opt-in live Daminion: login, filtered fetch, count, logout. |
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
| `DaminionLiveSessionTests`, `EndToEndRoundTripTests` (Daminion half) | `SYNAPIC_TEST_DAMINION_URL`, `SYNAPIC_TEST_DAMINION_USERNAME`, `SYNAPIC_TEST_DAMINION_PASSWORD` |
| `EndToEndRoundTripTests` (sidecar half) | a built sidecar executable (`artifacts/<rid>/synapic-inference*` or bundled next to the app) |
| `DaminionConnectionStoreTests`, `EngineSettingsStoreTests` | Windows (registry) |

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
