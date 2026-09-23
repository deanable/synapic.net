"""Synapic inference sidecar — FastAPI application.

Entry point for PyInstaller (spec §4). Implements the HTTP contract from
``docs/sidecar-protocol.md`` / README §4.2:

- ``GET  /health``          → HealthResponse (loading | ready | error)
- ``GET  /models/list``     → ModelInfo[] (local HF cache scan)
- ``POST /models/download`` → DownloadRequest (202 if already downloading)
- ``POST /tag``             → TagRequest → TagResponse
- ``GET/PUT /config``       → inference ConfigDto
- ``POST /shutdown``        → graceful exit

Port-file protocol (spec §2): launched with ``--port=0``, the OS assigns a
free port and the sidecar writes ``port\\npid\\n`` to the file named by the
``SYNAPIC_PORT_FILE`` environment variable (the Avalonia host sets it to
``%TEMP%/synapic_port_{pid}.txt``).
"""

from __future__ import annotations

import logging
import os
import sys
import threading
import time
from contextlib import asynccontextmanager

# ---------------------------------------------------------------------------
# PYTHONW / PACKAGED CONSOLE SAFETY (mirrors main.py of the original app)
# ---------------------------------------------------------------------------
# When stdout/stderr are None (pythonw) or unusable, uvicorn's logging setup
# can fail. Replace with devnull before any logging configuration runs.
if sys.stdout is None:
    sys.stdout = open(os.devnull, "w")
if sys.stderr is None:
    sys.stderr = open(os.devnull, "w")

os.environ.setdefault("HF_HUB_DISABLE_SYMLINKS_WARNING", "1")
# Keep hub progress bars enabled so our tqdm subclass always receives
# byte-level updates (bars are disabled automatically at WARNING+ log level,
# which would silence the /health download progress).
os.environ.setdefault("HF_HUB_DISABLE_PROGRESS_BARS", "0")

import uvicorn
from fastapi import FastAPI, HTTPException
from pydantic import BaseModel, Field

# Flat absolute imports: required because service.py is the PyInstaller entry
# script (no parent package) and keeps pytest imports identical.
import config, inference_engine, model_loader  # noqa: E402

logging.basicConfig(
    level=logging.INFO,
    format="%(asctime)s [%(levelname)s] %(name)s: %(message)s",
)
logger = logging.getLogger("synapic.inference")

# ---------------------------------------------------------------------------
# Request/response models (mirror the OpenAPI schemas in README §4.2)
# ---------------------------------------------------------------------------


class TagOptionsModel(BaseModel):
    confidence_threshold: float = Field(default=0.3, ge=0.0, le=1.0)
    probability_mode: str = Field(default="both", pattern="^(llm|probability|both)$")
    probability_threshold: float = Field(default=0.5, ge=0.0, le=1.0)
    candidate_labels: list[str] | None = None
    system_prompt: str | None = None
    max_new_tokens: int = Field(default=512, ge=1, le=4096)


class TagRequestModel(BaseModel):
    image_path: str
    model_id: str | None = None
    task: str | None = None
    options: TagOptionsModel | None = None


class DownloadRequestModel(BaseModel):
    model_id: str
    revision: str = "main"


class ConfigModel(BaseModel):
    model_id: str | None = None
    task: str | None = None
    device: str | None = None
    confidence_threshold: float | None = Field(default=None, ge=0.0, le=1.0)
    probability_mode: str | None = Field(default=None, pattern="^(llm|probability|both)$")
    probability_threshold: float | None = Field(default=None, ge=0.0, le=1.0)


# ---------------------------------------------------------------------------
# Session inference config (GET/PUT /config)
# ---------------------------------------------------------------------------

_session_config = {
    "model_id": config.DEFAULT_MODEL_ID,
    "task": config.MODEL_TASK_IMAGE_TEXT_TO_TEXT,
    "device": "cpu",
    "confidence_threshold": 0.3,
    "probability_mode": "both",
    "probability_threshold": 0.5,
}
_config_lock = threading.Lock()

# ---------------------------------------------------------------------------
# Background download registry
# ---------------------------------------------------------------------------

_download_threads: dict[str, threading.Thread] = {}
_download_lock = threading.Lock()
_shutdown_event = threading.Event()

_uvicorn_server: uvicorn.Server | None = None


@asynccontextmanager
async def lifespan(_app: FastAPI):
    logger.info("Synapic inference sidecar started")
    # Ensure the default vision model (LiquidAI/LFM2.5-VL-450M) is present:
    # check the HF cache and download it in the background when absent.
    # Byte-level progress is surfaced via /health's "download" field.
    threading.Thread(
        target=_ensure_default_model, name="default-model-download", daemon=True
    ).start()
    # Load the default model now rather than on the first /tag (see
    # _warm_up_model). Both stay off the startup path: uvicorn must begin
    # serving before either finishes, and warm-up simply skips itself when the
    # weights are not in the cache yet - downloading them is the other
    # thread's job.
    threading.Thread(target=_warm_up_model, name="model-warmup", daemon=True).start()
    yield
    logger.info("Synapic inference sidecar shutting down")


def _ensure_default_model() -> None:
    """Make sure the default vision model is available locally.

    The model is deliberately NOT baked into the application bundle (that
    would inflate every installer by ~1 GB). The sidecar checks the HF cache
    (HF_HOME, set by the Avalonia host) and downloads it in the background
    when absent.
    """
    if os.environ.get(config.AUTO_DOWNLOAD_DISABLE_ENV, "").lower() in ("1", "true", "yes"):
        logger.info(
            f"Auto-download disabled via {config.AUTO_DOWNLOAD_DISABLE_ENV} — skipping default model check"
        )
        return
    try:
        if model_loader.is_model_downloaded(config.DEFAULT_MODEL_ID):
            logger.info(
                f"Default model {config.DEFAULT_MODEL_ID} already present — no download needed"
            )
            return
        logger.info(
            f"Default model {config.DEFAULT_MODEL_ID} not found — downloading in background"
        )
        model_loader.download_model(config.DEFAULT_MODEL_ID)
    except Exception:
        logger.exception("Default model auto-download failed")


def _warm_up_model() -> None:
    """Load the default model at boot instead of on the first ``/tag``.

    The first batch of a run fans out four parallel ``/tag`` requests, and
    they used to hit a cold sidecar together: racing transformers' lazy import
    inside the frozen PyInstaller bundle raised ``ImportError: cannot import
    name 'pipeline'`` and 503'd three of the four, and the survivor paid the
    whole ~15s load inline. Importing and loading once, here, means the host's
    readiness poll only turns ``ready`` once the model can actually serve -
    so the first item neither errors nor stalls.

    Skipped when the weights are not in HF_HOME (downloading is
    ``_ensure_default_model``'s job) or when ``SYNAPIC_DISABLE_WARMUP`` is set.
    It waits ``WARMUP_GRACE_SECONDS`` first so the host's readiness poll sees
    idle "ready" rather than a load in progress. A failure restores the
    previous status: warm-up must never surface as
    ``/health`` "error", which the host treats as a fatal startup failure -
    the first real ``/tag`` retries the load and reports it properly.

    Note the pipeline cache is keyed by device and the session device arrives
    via ``PUT /config``, so a host configured for CUDA rebuilds it on the first
    tag; warming on the boot default is still correct for the CPU path.
    """
    if os.environ.get(config.WARMUP_DISABLE_ENV, "").lower() in ("1", "true", "yes"):
        logger.info(f"Warm-up disabled via {config.WARMUP_DISABLE_ENV}")
        return

    # Let the host see "ready" first: see config.WARMUP_GRACE_SECONDS.
    if config.WARMUP_GRACE_SECONDS:
        time.sleep(config.WARMUP_GRACE_SECONDS)

    before = model_loader.get_state_snapshot()
    try:
        if not model_loader.is_model_downloaded(config.DEFAULT_MODEL_ID):
            logger.info("Warm-up skipped - default model is not cached locally")
            return
        started = time.monotonic()
        model_loader.load_model(
            config.DEFAULT_MODEL_ID,
            _session_config["task"],
            device=_session_config["device"],
        )
        logger.info(
            f"Warmed up {config.DEFAULT_MODEL_ID} in "
            f"{time.monotonic() - started:.1f}s - the first tag pays no model load"
        )
    except Exception:
        logger.exception("Model warm-up failed - the first /tag will retry the load")
        try:
            model_loader.set_status(before.get("status", "ready"), before.get("error"))
        except Exception:
            logger.exception("Could not restore status after warm-up failure")


def _wait_for_model_ready(timeout: float) -> bool:
    """Block while a model load is already in flight; True when free to proceed.

    Polls the shared state without holding any lock the loader needs, so the
    in-flight load can finish. Returns False only when a load is still running
    after ``timeout`` seconds - that is the one case left that answers 503.
    """
    deadline = time.monotonic() + timeout
    while model_loader.get_state_snapshot().get("status") == "loading":
        if time.monotonic() >= deadline:
            return False
        time.sleep(0.2)
    return True


app = FastAPI(title="Synapic Inference API", version="1.0.0", lifespan=lifespan, docs_url=None, redoc_url=None)


def _write_port_file(port: int) -> None:
    """Write 'port\\npid\\n' to the port file (C# host reads this)."""
    port_file = os.environ.get(config.PORT_FILE_ENV_VAR)
    if not port_file:
        logger.warning(
            f"{config.PORT_FILE_ENV_VAR} not set — port file protocol disabled"
        )
        return
    try:
        with open(port_file, "w", encoding="ascii") as f:
            f.write(f"{port}\n{os.getpid()}\n")
        logger.info(f"Port file written: {port_file} (port {port}, pid {os.getpid()})")
    except Exception:
        logger.exception("Failed to write port file")


def _effective_task(requested: str | None) -> str:
    task = requested or _session_config["task"]
    if task not in config.VALID_TASKS:
        raise HTTPException(status_code=422, detail=f"Unsupported task '{task}'")
    return task


# ---------------------------------------------------------------------------
# Routes
# ---------------------------------------------------------------------------


@app.get(
    "/health",
    responses={
        200: {
            "description": (
                "Readiness, loaded model/device and any active model download "
                "(HealthResponse)."
            )
        }
    },
)
def health() -> dict:
    snapshot = model_loader.get_state_snapshot()
    payload = {
        "status": snapshot.get("status", "loading"),
        "model": snapshot.get("model"),
        "device": snapshot.get("device"),
        "vram_used_mb": snapshot.get("vram_used_mb"),
        "error": snapshot.get("error"),
    }
    download = model_loader.get_active_download()
    if download is not None:
        payload["download"] = download
    return payload


@app.get(
    "/models/list",
    responses={
        200: {"description": "Models cached in HF_HOME (ModelInfo[])."},
        500: {"description": "Cache scan failed."},
    },
)
def models_list() -> list[dict]:
    try:
        return model_loader.find_local_models()
    except Exception as e:
        logger.exception("models/list failed")
        raise HTTPException(status_code=500, detail=str(e)) from e


@app.post(
    "/models/download",
    responses={
        200: {
            "description": (
                "Download outcome: status is download_started | "
                "already_downloading | downloaded, plus model_id."
            )
        },
        422: {"description": "Model is an unsupported (quantized) format."},
    },
)
def models_download(body: DownloadRequestModel):
    if not model_loader.is_model_compatible(body.model_id):
        reason = model_loader.get_incompatibility_reason(body.model_id) or "incompatible model"
        raise HTTPException(status_code=422, detail=f"Model not supported: {reason}")

    if model_loader.is_model_downloaded(body.model_id):
        return {"status": "downloaded", "model_id": body.model_id}

    with _download_lock:
        existing = _download_threads.get(body.model_id)
        if existing is not None and existing.is_alive():
            return {"status": "already_downloading", "model_id": body.model_id}

        def _run() -> None:
            try:
                model_loader.download_model(body.model_id, revision=body.revision)
            except Exception:
                logger.exception(f"Background download failed for {body.model_id}")

        thread = threading.Thread(target=_run, name=f"download-{body.model_id}", daemon=True)
        _download_threads[body.model_id] = thread
        thread.start()

    return {"status": "download_started", "model_id": body.model_id}


@app.post(
    "/tag",
    responses={
        200: {
            "description": (
                "Inference result (TagResponse): category, keywords, "
                "description, probabilities, optional scoring, inference_ms, "
                "model_used."
            )
        },
        404: {"description": "Image not found."},
        422: {"description": "Validation error (blank image_path, bad task)."},
        503: {
            "description": (
                "Timed out waiting for an in-flight model load; the host "
                "retries once."
            )
        },
    },
)
def tag(body: TagRequestModel) -> dict:
    if not body.image_path:
        raise HTTPException(status_code=422, detail="image_path is required")
    if not os.path.isfile(body.image_path):
        raise HTTPException(status_code=404, detail=f"Image not found: {body.image_path}")

    requested_model = body.model_id or _session_config["model_id"]
    session_model = _session_config["model_id"]

    # Wait out a load that is already in flight (boot warm-up, or another
    # request of the same cold batch) instead of 503-ing: the host retries once
    # after 3s, which cannot outlast a ~15s cold load, so a 503 here used to
    # fail the first items of the first batch. 503 is now reserved for a load
    # that never finishes (the host still retries it once).
    if not _wait_for_model_ready(config.MODEL_LOAD_WAIT_SECONDS):
        raise HTTPException(status_code=503, detail="Model loading — retry shortly")

    task = _effective_task(body.task)
    options = body.options or TagOptionsModel()

    try:
        model, resolved_task = model_loader.load_model(
            requested_model, task, device=_session_config["device"]
        )
    except Exception as e:
        logger.exception("Model load failed")
        raise HTTPException(status_code=503, detail=f"Model not loaded: {e}") from e

    if requested_model != session_model:
        with _config_lock:
            _session_config["model_id"] = requested_model

    try:
        response = inference_engine.run_inference(
            model,
            body.image_path,
            resolved_task,
            confidence_threshold=options.confidence_threshold,
            probability_mode=options.probability_mode,
            probability_threshold=options.probability_threshold,
            candidate_labels=options.candidate_labels,
            system_prompt=options.system_prompt or "",
            max_new_tokens=options.max_new_tokens,
        )
    except FileNotFoundError as e:
        raise HTTPException(status_code=404, detail=str(e)) from e
    except Exception as e:
        logger.exception("Inference failed")
        raise HTTPException(status_code=500, detail=f"Inference failed: {e}") from e

    return response


@app.get(
    "/config",
    responses={
        200: {"description": "Current session inference config (ConfigDto)."}
    },
)
def get_config() -> dict:
    with _config_lock:
        return dict(_session_config)


@app.put(
    "/config",
    responses={
        200: {"description": "Updated session inference config (ConfigDto)."},
        422: {"description": "Validation error (unsupported task)."},
    },
)
def put_config(body: ConfigModel) -> dict:
    with _config_lock:
        updates = body.model_dump(exclude_none=True)
        if "model_id" in updates and updates["model_id"] != _session_config["model_id"]:
            model_loader.unload_model()
        if "task" in updates and updates["task"] not in config.VALID_TASKS:
            raise HTTPException(status_code=422, detail=f"Unsupported task '{updates['task']}'")
        _session_config.update(updates)
        return dict(_session_config)


@app.post(
    "/shutdown",
    responses={
        200: {"description": "`{status: shutting_down}` - the process exits."}
    },
)
def shutdown() -> dict:
    _shutdown_event.set()
    threading.Thread(target=_delayed_exit, name="shutdown", daemon=True).start()
    return {"status": "shutting_down"}


def _delayed_exit() -> None:
    import time

    time.sleep(0.5)  # allow the response to flush
    os._exit(0)


# ---------------------------------------------------------------------------
# Entry point
# ---------------------------------------------------------------------------


def main() -> None:
    global _uvicorn_server

    import argparse
    import socket as _socket

    parser = argparse.ArgumentParser(description="Synapic inference sidecar")
    parser.add_argument("--port", type=int, default=0, help="0 = OS-assigned free port")
    parser.add_argument("--host", default="127.0.0.1")
    args = parser.parse_args()

    # Pre-bind the socket so the actual OS-assigned port is known BEFORE the
    # server starts. Uvicorn >=0.38 no longer exposes its bound sockets via the
    # Server object, and the C# host needs the port immediately at launch.
    sock = _socket.socket(_socket.AF_INET, _socket.SOCK_STREAM)
    sock.bind((args.host, args.port))
    sock.listen(1)
    actual_port = sock.getsockname()[1]

    _write_port_file(actual_port)

    uvicorn_config = uvicorn.Config(
        app,
        host=args.host,
        port=args.port,
        log_level="info",
    )
    _uvicorn_server = uvicorn.Server(uvicorn_config)
    _uvicorn_server.run(sockets=[sock])


if __name__ == "__main__":
    main()
