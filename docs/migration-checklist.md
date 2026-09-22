# Migration Checklist

File-by-file porting map from the Python app
(`C:\Users\deank\repos\Synapic`) to Synapic.NET, per spec §12.

## Python sidecar (minimal changes; I/O boundaries only)

| Original | New | Status |
|----------|-----|--------|
| `src/core/huggingface_utils.py` (load/download/cache, label-prob path, CLIP scorer) | `src/Synapic.Inference/model_loader.py` | ✅ ported (HTTP status state replaces UI queue) |
| `src/core/processing.py` (per-item inference) | `src/Synapic.Inference/inference_engine.py` | ✅ ported (`run_inference`) |
| `src/core/image_processing.py` (extraction) | `src/Synapic.Inference/tag_extractor.py` | ✅ ported |
| `src/core/keyword_scoring.py` | `src/Synapic.Inference/keyword_scoring.py` | ✅ ported verbatim |
| `src/core/keyword_scoring_adapters.py` | `src/Synapic.Inference/model_loader.py` (scoring section) | ✅ merged |
| `src/utils/json_utils.py` | `src/Synapic.Inference/json_utils.py` | ✅ ported verbatim |
| `src/core/config.py` (inference constants) | `src/Synapic.Inference/config.py` | ✅ ported (UI constants dropped) |
| — (new thin wrapper) | `src/Synapic.Inference/service.py` | ✅ new FastAPI app |
| `main.spec` | `src/Synapic.Inference/synapic-inference.spec` | ✅ adapted (deps trimmed) |

## C# frontend (rewrites)

| Original | New | Status |
|----------|-----|--------|
| `main.py` | `Program.cs` + `App.axaml(.cs)` | ✅ |
| `src/ui/app.py` | `MainWindowViewModel` + `WizardViewModel` | ✅ |
| `src/ui/steps/step1_datasource.py` | `Step1DatasourceViewModel` + `.axaml` | ✅ |
| `src/ui/steps/step2_tagging.py` | `Step2EngineViewModel` + `.axaml` | ✅ |
| `src/ui/steps/step3_process.py` | `Step3ProcessViewModel` + `.axaml` | ✅ |
| `src/ui/steps/step4_results.py` | `Step4ResultsViewModel` + `.axaml` | ✅ |
| `src/ui/steps/step_dedup.py` | `StepDedupViewModel` + `.axaml` | ✅ |
| `src/core/processing.py` (orchestration) | `ProcessingOrchestrator` | ✅ |
| `src/core/daminion_api.py` | `Services/Daminion/IDaminionApi.cs` (Refit) | ✅ |
| `src/core/daminion_client.py` | `Services/Daminion/DaminionApiClient.cs` | ✅ |
| `src/core/image_processing.py` (write_metadata) | `MetadataWriterService` | ✅ (XMP/IPTC via byte-surgery; EXIF XP fields via XMP-compatible tags) |
| `src/core/dedup/*` | `DedupService` (NetVips + Union-Find) | ✅ |
| `src/core/session.py` | `Models/Session.cs` + step VM state | ✅ |
| `src/core/config.py` + `src/utils/config_manager.py` | `ConfigService` | ✅ |
| `src/utils/logger.py` | `SynapicLog` (Serilog) | ✅ |
| `src/utils/concurrency.py` | `SemaphoreSlim` in `ProcessingOrchestrator` | ✅ |
| `src/utils/version_check.py` | `UpdateCheckService` | ✅ |
| — (spec §11 Q4) | *dropped — cloud engines removed from scope; no API keys to store* | — |

## Deferred (out of scope until post-parity)

| Original | Destination | Reason |
|----------|-------------|--------|
| `src/ui/steps/step_upscale.py`, `src/core/upscaler.py` | TBD | Not in spec §5.2 wizard flow |
| `src/core/vector_embedder.py` (faiss/semantic search) | TBD | Not in spec §5.2 wizard flow |
| `src/core/enhanced_progress.py` | partially in `ProcessProgress` | granular stage tracking post-MVP |

## Verification status

- ✅ `dotnet build` clean (TreatWarningsAsErrors)
- ✅ 6 contract tests (Synapic.Shared.Tests)
- ✅ 119 C# service/model/view-model tests (Synapic.Avalonia.Tests)
- ✅ 94 pytest sidecar tests incl. live FastAPI contract checks

See [`testing.md`](testing.md) for what each suite covers and which tests are
environment-gated.
- ✅ P6.1 crash reporting + opt-in telemetry (local-only; no network)
- ⏳ End-to-end run against a real Daminion server (needs environment)
- ⏳ 4-RID PyInstaller bundle builds (CI)
- ⏳ Installer signing/notarization (needs certs)
- ⏳ P6.2 profiling + 10k-image leak soak
