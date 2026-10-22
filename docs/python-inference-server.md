# Synapic Python Inference Server (`Synapic.Inference`)

The **Synapic Python Inference Server** (`Synapic.Inference`) is the machine learning sidecar engine for Synapic.NET. It is packaged as a standalone, single-file executable via PyInstaller (`synapic-inference` / `synapic-inference.exe`) and serves deep learning vision models, keyword probability scoring, and super-resolution upscaling over loopback HTTP (`127.0.0.1`) to the Avalonia desktop frontend (`Synapic.Main`).

---

## 1. High-Level Architecture & Process Lifecycle

```
┌─────────────────────────────────────────────────────────────────────────────┐
│                          SYNAPIC APPLICATION BUNDLE                         │
├─────────────────────────────────────────────────────────────────────────────┤
│                                                                             │
│  ┌─────────────────────────────┐     HTTP/JSON (127.0.0.1:port)     ┌─────┐│
│  │   AVALONIA FRONTEND (C#)    │ ◄─────────────────────────────────► │     ││
│  │   (Synapic.Main)            │                                     │     ││
│  │                             │   GET  /health      → status/VRAM   │     ││
│  │   - Wizard UI & State       │   POST /tag         → tags & scoring│     ││
│  │   - File Scanner & EXIF     │   POST /upscale     → upscaled img  │     ││
│  │   - Daminion Catalog Sync   │   GET/PUT /config   → session model │     ││
│  │   - Process Manager         │   POST /shutdown    → graceful exit │     ││
│  └──────────────┬──────────────┘                                     │  ▼  ││
│                 │                                                    │     ││
│                 │ Spawns & reads port from SYNAPIC_PORT_FILE         │     ││
│                 ▼                                                    │     ││
│  ┌───────────────────────────────────────────────────────────────┐   │     ││
│  │   PYTHON INFERENCE SIDECAR (synapic-inference.exe)            │   │     ││
│  │   ┌───────────────────────────────────────────────────────┐   │   │     ││
│  │   │  FastAPI + Uvicorn (service.py)                       │   │   │     ││
│  │   ├───────────────────────────────────────────────────────┤   │   │     ││
│  │   │  Model Loader & Cache Manager (model_loader.py)      │   │   │     ││
│  │   │   - Hugging Face Hub downloads (%LOCALAPPDATA%/Synapic) │   │   │     ││
│  │   │   - Single-model residency cache & device resolution  │   │   │     ││
│  │   │   - Tiered Keyword Scoring (Label match / CLIP)       │   │   │     ││
│  │   ├───────────────────────────────────────────────────────┤   │   │     ││
│  │   │  Inference Engine (inference_engine.py)               │   │   │     ││
│  │   │   - Vision-Language Models (LFM2.5-VL, Qwen2-VL, etc.) │   │   │     ││
│  │   │   - Malformed JSON Single-Retry Repair Loop           │   │   │     ││
│  │   ├───────────────────────────────────────────────────────┤   │   │     ││
│  │   │  Tag Extraction & JSON Recovery (tag_extractor.py)   │   │   │     ││
│  │   ├───────────────────────────────────────────────────────┤   │   │     ││
│  │   │  Super-Resolution Upscaler (upscaler.py)              │   │   │     ││
│  │   │   - Swin2SR x2 / x4 (Quality & Balanced Workflows)    │   │   │     ││
│  │   │   - Fast Lanczos Resize & Denoise Blending            │   │   │     ││
│  │   └───────────────────────────────────────────────────────┘   │   │     ││
│  └───────────────────────────────────────────────────────────────┘   │     ││
│                                                                      └─────┘│
└─────────────────────────────────────────────────────────────────────────────┘
```

### Process Lifecycle Rules
1. **Launch**: Avalonia starts the sidecar executable with `--port=0`.
2. **IPC Port Announcement**: The sidecar pre-binds an available OS port, then writes `port\npid\n` to the file specified by the `SYNAPIC_PORT_FILE` environment variable (typically `%TEMP%/synapic_port_{pid}.txt`).
3. **Readiness Probe**: The frontend reads the port, verifies the sidecar PID is alive, and polls `GET /health` until `{"status": "ready"}` (up to 120s timeout).
4. **Execution**: The host routes inference (`POST /tag`), upscaling (`POST /upscale`), and cache/config queries over HTTP.
5. **Shutdown**: On exit, Avalonia issues `POST /shutdown` for graceful termination, falling back to process termination if unacknowledged within 5 seconds.

---

## 2. Key Responsibilities & Capabilities

- **Zero User Python Dependency**: Packaged as a self-contained PyInstaller bundle incorporating PyTorch, Hugging Face Transformers, FastAPI, and dependencies.
- **Vision-Language Inference**: Runs state-of-the-art vision-language models (e.g. `LiquidAI/LFM2.5-VL-450M`, `Qwen/Qwen2-VL-2B-Instruct`), captions, zero-shot classification, and standard image classification models.
- **Multimodal Output Formatting**: Converts vision model text generation into standardized JSON containing `category`, `keywords`, and `description`.
- **JSON Recovery & Retry Engine**: Automatically repairs unclosed JSON strings, missing member separators, or malformed syntax. Automatically retries VLM generation once with explicit re-formatting instructions if the first response is unparseable.
- **Tiered Keyword Probability Scoring**: Calculates keyword confidence through a multi-tier pipeline:
  - **Tier 1 / 2 (Label Confidence)**: Local classification model pipeline + fuzzy label matching (`difflib`).
  - **Tier 2.5 (Embedding Rescue)**: CLIP cosine similarity scoring via `openai/clip-vit-base-patch32`.
- **Super-Resolution Image Upscaling**: Uses Swin2SR models (`caidas/swin2SR-classical-sr-x2-64`, `caidas/swin2SR-classical-sr-x4-64`, `caidas/swin2SR-realworld-sr-x4-64-bsrgan-psnr`) and Lanczos algorithms with denoise blending and dimension capping.
- **CPU Precision Optimization**: Enforces `float32` precision on CPU builds to prevent 1000× speed regressions caused by bfloat16 emulation on mainstream x86 CPUs without AVX512-BF16 hardware instructions.

---

## 3. Module Breakdown & Architecture

The codebase lives in `src/Synapic.Inference/` and uses **flat absolute imports** (`import config`, `import model_loader`, etc.) to match the PyInstaller root execution context.

| Module | Purpose & Functionality |
|--------|-------------------------|
| `service.py` | FastAPI web service, route handlers, Lifespan background threads (warmup and default model download), IPC port-file writer, Pydantic validation schemas. |
| `model_loader.py` | Device detection (CPU/CUDA/MPS), Hugging Face Hub download & cache management (`HF_HOME`), single-model residency cache with thread locking, precision policy, and multi-tier keyword scoring. |
| `inference_engine.py` | Unified `run_inference(...)` pipeline for all model tasks, generation argument normalization (`_generation_kwargs`), VLM prompt formatting, and malformed output single-retry logic (`RETRY_INSTRUCTION`). |
| `tag_extractor.py` | Raw output parsing, VLM JSON extraction, reply quality classification (`REPLY_CLEAN`, `REPLY_REPAIRED`, `REPLY_UNREADABLE`), title-casing rules, and repair tracking (`reply_repairs`). |
| `upscaler.py` | `Swin2SRUpscaler` engine supporting `quality` (Swin2SR x2/x4), `balanced` (Swin2SR x4 + Lanczos denoise blending), and `fast` (pure Lanczos) workflows with input capping (`max_dimension`) and sharpening. |
| `keyword_scoring.py` | Pure functional math module for probability distributions: softmax calculation from logprobs or cosine similarities, thresholding, and float sum-to-one normalization. |
| `json_utils.py` | Robust string parsing utilities: fenced code block stripping, balanced brace scanning, unclosed quote repair, and missing member separator restoration (`_repair_missing_member_separators`). |
| `config.py` | Central system constants, default model ID (`LiquidAI/LFM2.5-VL-450M`), task mapping, stop words, file exclusions, and environment variable keys. |

---

## 4. In-Depth Module Details

### 4.1 `service.py` — FastAPI HTTP Application
- **Pre-bound Socket Server**: Binds the socket before starting Uvicorn, eliminating port race conditions when OS-assigning `--port=0`. Writes `port\npid\n` to `$SYNAPIC_PORT_FILE`.
- **Background Startup Threads**:
  - `_ensure_default_model`: Downloads `DEFAULT_MODEL_ID` into HF cache in the background if absent (skipped if `SYNAPIC_DISABLE_AUTO_DOWNLOAD=1`).
  - `_warm_up_model`: Pre-loads the default model after a short grace period (`WARMUP_GRACE_SECONDS=2`) to eliminate first-request latency (skipped if `SYNAPIC_DISABLE_WARMUP=1`).
- **Endpoint Route Handlers**:
  - `GET /health`: Returns status (`loading`, `ready`, `error`), currently loaded model, active device, RSS/VRAM usage in MB, and background download progress.
  - `GET /models/list`: Scans local Hugging Face cache directory (`HF_HOME`) and lists downloaded models with their sizes and tasks.
  - `POST /models/download`: Triggers an asynchronous background download of a model repository from Hugging Face Hub.
  - `POST /tag`: Executes image inference. Waits up to 240s (`MODEL_LOAD_WAIT_SECONDS`) if a model load is in-flight before returning `503 Service Unavailable`.
  - `POST /upscale`: Runs AI or Lanczos image upscaling.
  - `GET /config` / `PUT /config`: Reads or updates runtime session configuration (e.g., model ID, task, device, thresholds). Updating `model_id` automatically unloads the previous model.
  - `GET /prompt`: Returns the default VLM prompt template.
  - `POST /shutdown`: Initiates an asynchronous process shutdown via `os._exit(0)`.

### 4.2 `model_loader.py` — Model Loading, Precision & Scoring
- **Single-Model Residency**: Keeps at most **one** Hugging Face pipeline in memory at a time to prevent system Out-Of-Memory (OOM) errors. Builds are synchronized using `_model_build_lock`.
- **Device Resolution**: Auto-probes CUDA, Apple Silicon MPS, and CPU. Allows overriding via `SYNAPIC_DEVICE` or `PUT /config`.
- **CPU Precision Policy (`_load_dtype`)**:
  - On **CUDA/MPS**, returns `"auto"` (preserves bfloat16 / float16 weights).
  - On **CPU**, returns `"float32"`. Checkpoints in bfloat16 are forced to float32 because x86 CPUs lacking AVX512-BF16 or AMX hardware instructions emulate bfloat16 via software, causing a 1000× GEMM slowdown (e.g. 75s CPU time per image down to 4.7s on Intel 14th gen). The static verification script `build/check-load-dtype.py` checks this policy against compiled binaries.
- **Incompatible Formats**: Rejects unbundled quantization formats (e.g., GPTQ, AWQ, GGUF, EXL2, bitsandbytes, int4, int8) with descriptive 422 errors.
- **Multi-Tier Keyword Scoring**:
  - `score_keywords`: Executes the appropriate scoring tier depending on model capability and user preferences.
  - `run_local_label_confidence_inference`: Runs an `image-classification` model across candidates using exact and fuzzy matching (`difflib.SequenceMatcher`).
  - `TransformersCLIPScorer`: Uses `openai/clip-vit-base-patch32` to compute cosine similarities between image embeddings and text candidate embeddings.

### 4.3 `inference_engine.py` — Vision-Language Inference & Retry Engine
- **Task Support**:
  - `image-text-to-text`: Vision-Language Models (VLMs) using chat templates.
  - `image-to-text`: Image captioning models (e.g. BLIP).
  - `zero-shot-image-classification`: Zero-shot classification models (e.g. CLIP).
  - `image-classification`: Standard image classifiers.
- **VLM Prompt Instructions**: Instructs VLM models to output a single-line JSON object containing `description`, `category`, and `keywords`.
- **Generation Argument Normalization (`_generation_kwargs`)**: Clones the model's generation config, sets `max_new_tokens`, and removes `max_length` to suppress Hugging Face `transformers` duplicate kwarg warnings.
- **Single-Retry Recovery Loop**:
  - Evaluates the generated VLM reply with `tag_extractor.reply_is_malformed`.
  - If unreadable, triggers **one retry pass** sending the original image, system prompt, `RETRY_INSTRUCTION` (re-formatting rules), and the verbatim first reply.
  - Adopts the second reply only if its quality grade strictly improves upon the first (`reads_better_than`).

### 4.4 `tag_extractor.py` & `json_utils.py` — JSON Parsing & Auto-Repair
- **Extraction Logic**: Extracts `category`, `keywords`, and `description` from raw model output or parsed JSON dicts.
- **Reply Quality Grades**:
  - `REPLY_CLEAN`: Parsed cleanly on arrival.
  - `REPLY_REPAIRED`: Required JSON syntax repair pass.
  - `REPLY_UNREADABLE`: Could not be parsed as JSON; fell back to raw text.
- **JSON Repair Pass (`json_utils.py`)**:
  - Strips Markdown code blocks (```json ... ```).
  - Performs balanced brace matching to extract embedded JSON objects.
  - `_repair_missing_member_separators`: Detects missing commas or unclosed quotes at key boundaries (e.g., `...\n  "category": ...`) and inserts necessary quotes/commas.
- **Title-Casing & Stop Words**: Applies title-case formatting to keywords and filters out common stop words (`config.STOP_WORDS`).

### 4.5 `upscaler.py` — Super-Resolution & Fast Resizing
- **Supported Workflows**:
  - `quality`: Uses `caidas/swin2SR-classical-sr-x2-64` (2x) or `caidas/swin2SR-classical-sr-x4-64` (4x).
  - `balanced`: Uses `caidas/swin2SR-realworld-sr-x4-64-bsrgan-psnr` (4x) blended with Lanczos resize based on `denoise_strength`. Downsamples to 2x if 2x was requested.
  - `fast`: Pure Lanczos resizing without PyTorch requirements.
- **Input Dimension Capping (`_cap_input_size`)**: Shrinks oversized input images to `options.max_dimension` before feeding them into Swin2SR (which processes 64px patches) to prevent VRAM explosion and huge output file sizes.
- **Post-Processing**: Applies Unsharp Mask sharpening (`sharpen_amount`) and exports in requested formats (`JPEG`, `PNG`, `WEBP`, `TIFF`, `BMP`).

---

## 5. HTTP API Specification

For full detailed schema contracts, refer to [`docs/sidecar-protocol.md`](sidecar-protocol.md).

### Endpoint Overview

| Endpoint | Method | Description | Primary Response Code |
|----------|--------|-------------|-----------------------|
| `/health` | `GET` | Health, readiness probe, active download, memory metrics. | `200 OK` |
| `/models/list` | `GET` | Scans and returns locally cached models in `HF_HOME`. | `200 OK` |
| `/models/download` | `POST` | Triggers background download of a Hugging Face repo. | `200 OK` / `422 Unprocessable` |
| `/tag` | `POST` | Runs image tagging/inference on `image_path`. | `200 OK` / `404 Not Found` / `503 Service Unavailable` |
| `/upscale` | `POST` | Runs image upscaling on `image_path`. | `200 OK` / `404 Not Found` / `422 Unprocessable` |
| `/config` | `GET` | Gets current session inference configuration. | `200 OK` |
| `/config` | `PUT` | Updates session configuration (unloads model if ID changed). | `200 OK` / `422 Unprocessable` |
| `/prompt` | `GET` | Gets default VLM system/user prompt template. | `200 OK` |
| `/shutdown` | `POST` | Triggers graceful sidecar process termination. | `200 OK` |

---

## 6. Environment Variables

The Python server behavior can be configured using environment variables:

| Variable | Description | Default |
|----------|-------------|---------|
| `SYNAPIC_PORT_FILE` | File path where the server writes `port\npid\n` on startup. | None (Required for IPC) |
| `SYNAPIC_DEVICE` | Hardware compute device override (`cpu`, `cuda`, `mps`). | Auto-detected |
| `SYNAPIC_DISABLE_AUTO_DOWNLOAD` | Set to `1` to disable background auto-download of default model on startup. | `0` |
| `SYNAPIC_DISABLE_WARMUP` | Set to `1` to disable pre-loading/warming up the default model on startup. | `0` |
| `HF_HOME` | Directory path for Hugging Face Hub cache. | System default or `%LOCALAPPDATA%/Synapic/models` |

---

## 7. Packaging & Build Pipeline

The server is packaged into a standalone single-file binary using PyInstaller.

### Spec File Configuration (`synapic-inference.spec`)
- **Hidden Imports**: Explicitly includes `torch`, `transformers`, `accelerate`, `fastapi`, `uvicorn`, `pydantic`, `PIL`, `tqdm`, `psutil`, and related libraries.
- **Exclusions**: Strips heavy unnecessary packages (`tkinter`, `matplotlib`, `jupyter`, `pytest`, `torch.testing`).
- **Data Files**: Collects necessary data files from `transformers` and `tokenizers`.

### Build Verification Scripts (`build/`)
- `build/check-load-dtype.py`: Inspects the compiled PyInstaller binary to verify CPU precision is locked to `float32`.
- `build/check-sidecar-variant.py`: Verifies CPU vs CUDA binary variants.
- `build/check-lfm2vl-tie.py`: Ensures `transformers` version compatibility for LFM2.5-VL models before packaging.
- `build/generate-protocol-doc.py`: Generates and cross-checks `sidecar-protocol.md` against FastAPI OpenAPI models and C# DTOs.

---

## 8. Development & Testing Guide

### Running from Source
```bash
# 1. Create and activate virtual environment
python -m venv .venv
source .venv/bin/activate  # Windows: .venv\Scripts\activate

# 2. Install dependencies
pip install -r src/Synapic.Inference/requirements.txt

# 3. Change directory to src/Synapic.Inference (required for flat absolute imports)
cd src/Synapic.Inference

# 4. Set port file environment variable and run
export SYNAPIC_PORT_FILE=/tmp/synapic_port.txt  # Windows: set SYNAPIC_PORT_FILE=%TEMP%\synapic_port.txt
python service.py --port=0
```

### Running Tests
Python unit and contract tests live in `tests/Synapic.Inference.Tests`:
```bash
# Run all sidecar tests
pytest tests/Synapic.Inference.Tests

# Verify HTTP protocol documentation generator
python build/generate-protocol-doc.py --check
```
