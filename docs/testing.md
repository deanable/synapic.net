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
```

---

## `tests/Synapic.Avalonia.Tests` (xUnit + Avalonia.Headless)

| File | What it pins down |
|------|-------------------|
| `ContractsRoundTripTests` (Shared project) | DTO ↔ JSON field-name stability against the frozen wire protocol. |
| `SessionTests` | `Session` stats/`ResetStats` and the `ValidateForStep2/3` gates. |
| `WizardNavigationTests` | Forward/back navigation, validation errors, step-entry side effects. |
| `Step1DatasourceTests` | `ConnectCommand` enablement and `CanExecuteChanged` notifications as credentials change. |
| `Step2EngineTests` | Engine view-model state → session propagation. |
| `TagInstructionTests` | The editable tag instruction: blank means "the sidecar's built-in prompt" and stays null on the wire, "Use built-in instruction" loads the sidecar's own text (once, cached) and never overwrites the box with an empty answer, reset clears back to the default, and the value round-trips through the registry. |
| `Step2PromptBindingTests` | The Step 2 prompt editor through real compiled XAML: the box two-way binds to `UserPrompt`, both buttons carry their commands (a mistyped Avalonia binding is otherwise silent), the preset combobox lists the history and fills the box when one is chosen, and a real Delete key press removes the highlighted preset. |
| `SystemPromptPresetStoreTests` | The system-prompt history file: object and bare-array shapes, a missing or corrupt file degrading to an empty list, newest-first ordering, trim/dedupe, the `MaxPresets` cap, and removing an unknown entry as a no-op — all against a temp file, never `%APPDATA%`. |
| `SystemPromptPresetTests` | Step 2's preset behaviour: committing a prompt (leaving the step) remembers it, blank input is never saved, the highlight follows the box, Delete removes the highlighted preset and clears the box when that prompt was the one in use, and deletions persist to the next session. |
| `ProcessingEtaTests` | ETA math (`EstimateProgress`, `ProcessingOrchestrator.EstimateProgress`) and `ProcessAll` propagation from Step 1 into the selection. |
| `TagFieldSelectionTests` | Which returned fields are written, per `TagFieldSelection` permutation, through a fake sidecar + capturing metadata writer. |
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
| `DaminionConnectionStoreTests` | Step 1 registry persistence incl. `ProcessAll` (Windows-only). |
| `EngineSettingsStoreTests` | Step 2 registry persistence (Windows-only). |
| `DaminionLiveSessionTests` | Opt-in live Daminion: login, filtered fetch, count, logout. |
| `SynapicLogTests` | Logger initialisation, file sink, UI sink ring buffer, per-run rotation into `logs/archives` and pruning to `MaxArchivedLogs`. |
| `CrashReporterTests` | Crash capture → report file + session-log snapshot. |
| `TelemetryServiceTests` | Opt-in counters and the disabled no-op path. |
| `RuntimeCheckTests` | .NET runtime detection helpers. |
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
| `test_model_loader.py` | Compatibility rules, task suggestion, fuzzy label matching, cache heuristics, state/download-progress, and concurrent model loads (the build lock). |
| `test_keyword_scoring.py` | Softmax constructions, sum-to-one invariant, thresholding, and the `ScoreResult` contract. |
| `test_tag_extractor.py` | `json_utils` extraction/repair — including the payload shapes a small VLM gets wrong (envelopes, capitalised or synonymous keys, a literal newline in a string, truncation inside a string or array, JSON delivered as a string) and the unrecognised payload that is deliberately left as raw text — plus Title Case and extraction for classification / zero-shot / image-to-text, including limits and de-duplication. |
| `test_inference_engine.py` | The VLM message shape from `build_vlm_messages` (every turn carries content *parts* — a bare-string system turn raises `TypeError: string indices must be integers` inside transformers 5.1 and failed every image of a run) plus an end-to-end `run_inference` through a fake pipeline with and without a system prompt; and `_generation_kwargs`: clears the conflicting `max_length`, pins greedy decoding, does not mutate the pipeline's config, and falls back for unknown pipeline shapes (the transformers-warning fix). |
| `test_tag_prompt.py` | `DEFAULT_VLM_USER_PROMPT` names every key `tag_extractor` reads, bans code fences and surrounding text, demands double quotes, asks for a single-line description, and shows the object shape. These are format guards, not prose review: the wording they pin took measured strict-JSON compliance from 0/13 to 13/13 (harness in `build/check-tag-prompt.py`), so dropping one sends every reply back through the rescue path. |
| `test_check_installer_appid.py` | The Windows installer's AppId guard in `build/check-installer-appid.py`: Inno's `{{` escaping, comment and section handling, duplicate/constant/absent directives, and the refusal when the script disagrees with `build/installer-appid.txt` — including that the repository's own two files agree. |
| `test_check_sidecar_variant.py` | The CPU/CUDA payload rules in `build/check-sidecar-variant.py`: a CPU bundle must contain no CUDA runtime binaries (but torch's CUDA *python* modules are not leaks), a CUDA bundle must contain `torch_cuda.dll` plus cudart/cublas/cudnn, and optional extras may be absent. Runs without torch or PyInstaller installed. |
| `test_check_load_dtype.py` | The packed-policy rules in `build/check-load-dtype.py`: a bundle whose `_load_dtype` is missing, answers `"auto"` on CPU, raises, or is never called by `_construct_model` fails with a message naming the device; the shipped policy (cpu=`float32`, cuda/mps=`auto`) passes. Runs without torch or PyInstaller installed. |
| `test_warmup.py` | Cold start: `_wait_for_model_ready` returns at once when idle, blocks until an in-flight load finishes, times out into a 503; `_warm_up_model` skips itself when disabled or when the weights are absent, never leaks an `error` status on failure (the host treats that as fatal), and reports the model when it succeeds. |
| `test_protocol_doc.py` | `build/generate-protocol-doc.py --check`: the committed `sidecar-protocol.md` matches the live OpenAPI schema + the C# DTOs, so the contract doc cannot drift. |
| `test_split_release_asset.py` | `build/split-release-asset.py`, which makes the >2 GiB CUDA sidecar publishable: parts rejoin byte-for-byte, parts are exact ranges, the oversized original is not left in the upload directory (one invalid file fails the whole release), stale parts from an earlier run are cleared, a cap at/above GitHub's limit is refused, and the emitted helper (`.bat`/`.sh`) names every part and reproduces the file. |

---

## `tests/Synapic.Integration.Tests`

Currently a placeholder project (references the Avalonia project, no test
files). Real environment-dependent end-to-end coverage lives in
`EndToEndRoundTripTests` inside the Avalonia suite, gated on the same
environment variables.

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
