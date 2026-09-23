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
| `ProcessingEtaTests` | ETA math (`EstimateProgress`, `ProcessingOrchestrator.EstimateProgress`) and `ProcessAll` propagation from Step 1 into the selection. |
| `TagFieldSelectionTests` | Which returned fields are written, per `TagFieldSelection` permutation, through a fake sidecar + capturing metadata writer. |
| `PauseTests` | Pause/resume semantics (running items finish, queued items wait) and cancellation wins. |
| `MainWindowPopulationTests` | Main-window/shell population and server-detection wiring. |
| `ServerDetectionTests` | `MainWindowViewModel.DetectServerAsync`, buildable RIDs, the variant panel, and `/health` download-status application (`ApplyDownloadStatus`). |
| `SidecarBuildProgressTrackerTests` | Raw build-output → stage/percent mapping. |
| `DedupServiceTests` + `DedupTestHarness` | pHash/dHash/aHash grouping and thresholds on generated images. |
| `MetadataWriterTests` | XMP write/read round-trips for JPEG and PNG. |
| `EndToEndRoundTripTests` | Full path where the environment allows: real sidecar executable and/or a live Daminion (see below). |
| `DaminionBaseUrlTests` | `DaminionApiClient.NormalizeBaseUrl` behaviour. |
| `DaminionLayoutParsingTests` | Layout payload parsing / tag-GUID extraction. |
| `DaminionIndexedTagValuesRequestTests` | The all-parameters-required routing rule for `GetIndexedTagValues`. |
| `DaminionConnectionStoreTests` | Step 1 registry persistence incl. `ProcessAll` (Windows-only). |
| `EngineSettingsStoreTests` | Step 2 registry persistence (Windows-only). |
| `DaminionLiveSessionTests` | Opt-in live Daminion: login, filtered fetch, count, logout. |
| `SynapicLogTests` | Logger initialisation, file sink, UI sink ring buffer. |
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
forces `SYNAPIC_DISABLE_AUTO_DOWNLOAD=1`, and resets the download registry
between tests.

| File | What it pins down |
|------|-------------------|
| `test_service_contract.py` | Served routes via `TestClient`: `/health` shape and status, `/models/list`, `/config` GET/PUT (incl. model-switch unload), `/tag` validation (422/404), `/shutdown`. |
| `test_model_loader.py` | Compatibility rules, task suggestion, fuzzy label matching, cache heuristics, state/download-progress, and concurrent model loads (the build lock). |
| `test_keyword_scoring.py` | Softmax constructions, sum-to-one invariant, thresholding, and the `ScoreResult` contract. |
| `test_tag_extractor.py` | `json_utils` extraction/repair, Title Case, and extraction for classification / zero-shot / image-to-text, including limits and de-duplication. |
| `test_inference_engine.py` | `_generation_kwargs`: clears the conflicting `max_length`, does not mutate the pipeline's config, and falls back for unknown pipeline shapes (the transformers-warning fix). |
| `test_check_sidecar_variant.py` | The CPU/CUDA payload rules in `build/check-sidecar-variant.py`: a CPU bundle must contain no CUDA runtime binaries (but torch's CUDA *python* modules are not leaks), a CUDA bundle must contain `torch_cuda.dll` plus cudart/cublas/cudnn, and optional extras may be absent. Runs without torch or PyInstaller installed. |
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

## Conventions when adding tests

- Never require the network or a Daminion server for a default-run test; gate
  it behind `SYNAPIC_TEST_*` and return early.
- Use the shared fakes in `TestFakes.cs` for the sidecar rather than hitting a
  real one.
- Python tests must import without torch/transformers installed.
- `TreatWarningsAsErrors` is on for C#, so a new warning fails the build.
