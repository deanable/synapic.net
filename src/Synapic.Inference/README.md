# Synapic.Inference — Python Inference Server

The **Python Inference Server** (`Synapic.Inference`) is the machine learning sidecar engine for Synapic.NET. It is packaged as a single PyInstaller executable (`synapic-inference` / `synapic-inference.exe`) and driven over loopback HTTP (`127.0.0.1:{port}`) by the C#/Avalonia desktop frontend (`Synapic.Main`).

> **Full Documentation**: For a comprehensive technical reference covering module mechanics, JSON recovery engines, upscaling workflows, CPU precision policies, and build verification, see [`docs/python-inference-server.md`](../../docs/python-inference-server.md).
> **Protocol Specification**: For the generated HTTP/JSON OpenAPI wire contract between C# and Python, see [`docs/sidecar-protocol.md`](../../docs/sidecar-protocol.md).

---

## Key Modules

| Module | Purpose & Functionality |
|--------|-------------------------|
| `service.py` | FastAPI application, HTTP routes (`/health`, `/tag`, `/upscale`, `/models/*`, `/config`, `/shutdown`), pre-bound socket IPC port writer (`SYNAPIC_PORT_FILE`), Lifespan background tasks (warmup & auto-download). |
| `model_loader.py` | Device detection (CPU/CUDA/MPS), Hugging Face Hub download & cache management (`HF_HOME`), single-model residency cache with thread locking, `float32` CPU precision policy, and multi-tier keyword scoring (Label match / CLIP). |
| `inference_engine.py` | Unified `run_inference(...)` pipeline for VLMs, captions, and classification models. Normalizes generation kwargs and executes malformed JSON single-retry recovery (`RETRY_INSTRUCTION`). |
| `tag_extractor.py` | Raw output parsing, VLM JSON extraction, reply quality classification (`REPLY_CLEAN`, `REPLY_REPAIRED`, `REPLY_UNREADABLE`), title-casing, and repair tracking (`reply_repairs`). |
| `upscaler.py` | `Swin2SRUpscaler` super-resolution engine supporting `quality` (Swin2SR x2/x4), `balanced` (Swin2SR x4 + Lanczos denoise blending), and `fast` (pure Lanczos) workflows with input dimension capping (`max_dimension`). |
| `keyword_scoring.py` | Functional math module for probability distributions: softmax from logprobs or cosine similarities, candidate thresholding, and float sum-to-one normalization. |
| `json_utils.py` | Robust string parsing utilities: fenced code block stripping, balanced brace scanning, unclosed quote repair, and missing member separator restoration (`_repair_missing_member_separators`). |
| `config.py` | Central system constants, default model ID (`LiquidAI/LFM2.5-VL-450M`), task mapping, stop words, file exclusions, and environment variable keys. |

---

## Quick Start (Development)

Modules use **flat absolute imports** (`import config`, `import model_loader`, etc.) because `service.py` is the PyInstaller entry script and has no parent package. The sidecar **must be launched from its own directory** (`src/Synapic.Inference`).

```bash
# 1. Create and activate virtual environment
python -m venv .venv
source .venv/bin/activate          # Windows: .venv\Scripts\activate

# 2. Install dependencies
pip install -r requirements.txt

# 3. Set IPC port file variable and launch
export SYNAPIC_PORT_FILE=/tmp/synapic_port.txt   # Windows: set SYNAPIC_PORT_FILE=%TEMP%\synapic_port.txt
python service.py --port=0
```

The sidecar writes `port\npid\n` to `$SYNAPIC_PORT_FILE` once Uvicorn has bound its OS-assigned port. The C# host reads this file and polls `/health` until `{"status": "ready"}`.

---

## Building the Standalone Executable

```bash
# Build via PyInstaller spec file
pyinstaller synapic-inference.spec
# → Output artifact: dist/synapic-inference(.exe)

# Or use the workspace build scripts (from repo root):
./build/build-sidecar.sh linux-x64        # Linux / macOS
# build/build-sidecar.ps1 win-x64          # Windows
```

---

## Testing

Python unit, contract, and verification tests live in `tests/Synapic.Inference.Tests`:

```bash
# Run pytest suite
pytest ../../tests/Synapic.Inference.Tests

# Verify protocol documentation consistency
python ../../build/generate-protocol-doc.py --check
```
