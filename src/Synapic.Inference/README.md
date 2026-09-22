# Synapic.Inference — Python Sidecar

Python inference engine for Synapic.NET, packaged as a single PyInstaller
executable (`synapic-inference` / `synapic-inference.exe`) and driven over
local HTTP by the Avalonia frontend. Implements migration spec §4.

## Modules

| Module | Responsibility | Ported from (original repo) |
|--------|----------------|------------------------------|
| `service.py` | FastAPI app, routing, port-file write, shutdown | New (thin wrapper) |
| `model_loader.py` | Device detect, download/load/cache, label-probability + CLIP scoring | `src/core/huggingface_utils.py` + `keyword_scoring_adapters.py` |
| `inference_engine.py` | `run_inference(...)` for all model types | `src/core/processing.py` (inference section) |
| `tag_extractor.py` | Raw output → (category, keywords, description) | `src/core/image_processing.py` (extraction section) |
| `keyword_scoring.py` | Tier contract + softmax math | `src/core/keyword_scoring.py` |
| `json_utils.py` | JSON/literal extraction from LLM text | `src/utils/json_utils.py` |
| `config.py` | Constants (tasks, limits, stop words) | `src/core/config.py` |

Inference logic is ported with minimal changes — only I/O boundaries changed
(file paths / UI queues → HTTP requests and response dicts).

## Running from source

```bash
python -m venv .venv
source .venv/bin/activate          # Windows: .venv\Scripts\activate
pip install -r src/Synapic.Inference/requirements.txt

# Flat absolute imports (import config, import model_loader, ...) mean the
# sidecar must be launched from its own directory.
cd src/Synapic.Inference
export SYNAPIC_PORT_FILE=/tmp/synapic_port.txt   # Windows: set SYNAPIC_PORT_FILE=%TEMP%\synapic_port.txt
python service.py --port=0
```

The sidecar writes `port\npid\n` to `$SYNAPIC_PORT_FILE` once uvicorn has
bound its (OS-assigned) port; the C# host polls `/health` until `ready`.

## Building the executable

```bash
pyinstaller src/Synapic.Inference/synapic-inference.spec
# → dist/synapic-inference(.exe)
```

## Tests

```bash
pytest tests/Synapic.Inference.Tests
```
