"""Synapic inference sidecar — FastAPI application.

Entry point for PyInstaller (spec §4). Implements the HTTP contract from
``docs/sidecar-protocol.md`` / README §4.2:

- ``GET  /health``          → HealthResponse (loading | ready | error)
- ``GET  /models/list``     → ModelInfo[] (local HF cache scan)
- ``POST /models/download`` → DownloadRequest (202 if already downloading)
- ``POST /tag``             → TagRequest → TagResponse
- ``POST /upscale``         → UpscaleRequest → UpscaleResponse (Swin2SR/Lanczos)
- ``GET/PUT /config``       → inference ConfigDto
- ``GET  /prompt``          → the built-in tag instruction (PromptDefaultsDto)
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
import config, inference_engine, model_loader, upscaler  # noqa: E402

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
    # The tag instruction. Editable in Step 2; absent/blank means the sidecar's
    # own DEFAULT_VLM_USER_PROMPT. Bounded so a runaway edit cannot turn every
    # request into a huge prompt (the model's context is finite).
    user_prompt: str | None = Field(default=None, max_length=4000)
    max_new_tokens: int = Field(default=512, ge=1, le=4096)


class TagRequestModel(BaseModel):
    image_path: str
    model_id: str | None = None
    task: str | None = None
    options: TagOptionsModel | None = None


class DownloadRequestModel(BaseModel):
    model_id: str
    revision: str = "main"


class UpscaleOptionsModel(BaseModel):
    """Parameters of the upscale run — mirrors the original app's UpscaleOptions."""

    workflow: str = Field(default="quality", pattern="^(quality|balanced|fast)$")
    factor: int = Field(default=2, ge=2, le=8)
    precision: str = Field(default="auto", pattern="^(auto|fp16|fp32)$")
    denoise_strength: float = Field(default=1.0, ge=0.0, le=1.0)
    sharpen_amount: float = Field(default=0.0, ge=0.0, le=2.0)
    max_dimension: int = Field(default=2048, ge=0, le=10000)
    output_format: str = Field(default="keep", pattern="^(keep|JPEG|PNG|WEBP)$")
    jpeg_quality: int = Field(default=95, ge=70, le=100)
    overwrite_existing: bool = True


class UpscaleRequestModel(BaseModel):
    image_path: str
    output_path: str | None = None
    options: UpscaleOptionsModel | None = None


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
    # Pre-warm the Swin2SR upscaler on the first /upscale — without this the
    # first upscale item pays the model load cost inline (there is no 503
    # retry path for /upscale like there is for /tag). The upscaler imports
    # torch, so this stays lazy and off the import path of /tag-only tests.
    threading.Thread(target=_warm_up_upscaler, name="upscaler-warmup", daemon=True).start()
    yield
    logger.info("Synapic inference sidecar shutting down")


def apply_launch_device(device: str | None) -> None:
    """Set the session device from the launch environment / ``--device``.

    The device lives in the session config (``PUT /config``) and ``/tag``
    builds its pipeline from it, so the host has to say which device it wants
    before the first tag. Saying it at launch rather than only over HTTP has
    two consequences that matter:

    * the boot warm-up, which starts ``WARMUP_GRACE_SECONDS`` after this
      returns, loads the model on the requested device — otherwise it loads
      the CPU default and the first tag of a CUDA run pays a second, full
      model load on the GPU while the CPU copy is still resident;
    * an installed app whose bundled executable is the CUDA build gets GPU
      inference without any host-side configuration at all.

    The host passes the device in ``config.DEVICE_ENV_VAR``; an environment
    variable is compatible with every build, an unknown CLI flag is not.

    Unknown values are ignored rather than fatal: the host validates before it
    launches, and a stale or hand-edited setting must not stop the server.
    """
    requested = (device or "").strip().lower()
    if not requested or requested == _session_config["device"]:
        return
    if requested not in ("cpu", "cuda", "mps"):
        logger.warning(
            f"Ignoring unknown launch device '{device}' - keeping "
            f"'{_session_config['device']}'"
        )
        return
    with _config_lock:
        _session_config["device"] = requested
    logger.info(f"Session device set to '{requested}' by the launch environment")


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

    The device warmed here is whatever the session is already configured for:
    the host passes its Step 2 selection in ``config.DEVICE_ENV_VAR`` at launch
    (``apply_launch_device``, which runs before this thread exists), and
    ``PUT /config`` can change it later. The pipeline cache is keyed by device,
    so a change made after warm-up simply rebuilds the pipeline on the first
    tag and evicts the previous one.
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


def _warm_up_upscaler() -> None:
    """Touch the Swin2SR upscaler singleton at boot so the first /upscale
    does not pay the model load cost inline.

    Unlike /tag, /upscale has no 503 retry path, so a cold first upscale
    just waits with no backoff. Warming here moves that cost to boot.

    Skipped when ``SYNAPIC_DISABLE_WARMUP`` is set, or when torch is not
    available (CPU-only / no-AI machines — the fast workflow needs no model).
    The upscaler is lazy and imports torch on construction, so this stays off
    the import path of /tag-only test runs.
    """
    if os.environ.get(config.WARMUP_DISABLE_ENV, "").lower() in ("1", "true", "yes"):
        logger.info(f"Upscaler warm-up disabled via {config.WARMUP_DISABLE_ENV}")
        return

    try:
        import torch  # noqa: F401 - warm-up is skipped on ImportError below
    except ImportError:
        logger.info("Upscaler warm-up skipped - torch not available")
        return

    try:
        from pathlib import Path

        # Touch the singleton so it imports torch and constructs the lock.
        # Model loading happens on first /upscale (workflow/factor-specific),
        # so this only warms the wrapper, not any SR weights — which is fine,
        # because the weights are workflow/factor-specific and we do not know
        # which the host will ask for.
        _get_upscaler()
        logger.info("Upscaler singleton warmed up at boot")
    except Exception:
        logger.exception("Upscaler warm-up failed - the first /upscale will load on demand")


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
                "model_used, reply_repairs (the rewrites the JSON hunt needed "
                "before the reply could be read; empty for a clean reply), "
                "reply_retried (the first reply could not be read as JSON, so "
                "the model was asked once more)."
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
            user_prompt=options.user_prompt or "",
        )
    except FileNotFoundError as e:
        raise HTTPException(status_code=404, detail=str(e)) from e
    except Exception as e:
        logger.exception("Inference failed")
        raise HTTPException(status_code=500, detail=f"Inference failed: {e}") from e

    return response


# ---------------------------------------------------------------------------
# Upscaling (port of the original app's Swin2SR upscaler)
# ---------------------------------------------------------------------------

_upscaler_instance: "upscaler.Swin2SRUpscaler | None" = None
_upscaler_init_lock = threading.Lock()
_upscale_lock = threading.Lock()


def _get_upscaler() -> "upscaler.Swin2SRUpscaler":
    """Lazy singleton: constructing Swin2SRUpscaler imports torch, which must
    stay off the import path of every test that only exercises /tag.

    Guarded by its own lock (not ``_upscale_lock``): the boot-time warm-up
    thread calls this without holding ``_upscale_lock``, and the request path
    calls it *while* holding that lock, so reusing it here would self-deadlock.
    """
    global _upscaler_instance
    if _upscaler_instance is None:
        with _upscaler_init_lock:
            if _upscaler_instance is None:
                _upscaler_instance = upscaler.Swin2SRUpscaler()
    return _upscaler_instance


@app.post(
    "/upscale",
    responses={
        200: {
            "description": (
                "Upscale outcome (UpscaleResponse): output_path, before/after "
                "dimensions, workflow, factor, model_used, inference_ms."
            )
        },
        404: {"description": "Image not found."},
        422: {
            "description": (
                "Validation error (blank image_path, unsupported workflow/factor "
                "combination, out-of-range option)."
            )
        },
        500: {"description": "Upscale failed or produced no output."},
    },
)
def upscale(body: UpscaleRequestModel) -> dict:
    """Upscale one image with the original app's workflows (quality/balanced
    Swin2SR models, fast Lanczos) and save the result next to the input.

    Requests are serialized on purpose: the original app ran one upscale
    worker thread, and two concurrent CPU inferences would double the model
    resident set for no throughput gain.
    """
    from pathlib import Path

    from PIL import Image

    if not body.image_path:
        raise HTTPException(status_code=422, detail="image_path is required")
    if not os.path.isfile(body.image_path):
        raise HTTPException(status_code=404, detail=f"Image not found: {body.image_path}")

    raw = body.options or UpscaleOptionsModel()
    resolved = upscaler.UpscaleOptions(
        workflow=raw.workflow.strip().lower(),
        precision=raw.precision.strip().lower(),
        denoise_strength=float(raw.denoise_strength),
        sharpen_amount=float(raw.sharpen_amount),
        max_dimension=int(raw.max_dimension),
        output_format=raw.output_format.strip(),
        jpeg_quality=int(raw.jpeg_quality),
        overwrite_existing=bool(raw.overwrite_existing),
    )
    factor = int(raw.factor)

    input_path = Path(body.image_path)
    try:
        with Image.open(input_path) as source:
            original_width, original_height = source.size
    except Exception as e:
        raise HTTPException(
            status_code=422, detail=f"Not a readable image: {body.image_path} ({e})"
        ) from e

    output_target = Path(body.output_path) if body.output_path else None
    started = time.monotonic()
    try:
        # Serialized on purpose: the original app ran a single upscale worker
        # thread, and two concurrent CPU inferences would double the model's
        # resident set for no throughput gain (the host already runs one item
        # at a time). The lock covers the whole call, PIL open/save included,
        # so a request never observes another one's half-written output file.
        with _upscale_lock:
            result_path = _get_upscaler().upscale(
                input_path=input_path,
                factor=factor,
                output_path=output_target,
                options=resolved,
                status_callback=lambda message: logger.info("upscale: %s", message),
            )
    except ValueError as e:
        # Unsupported workflow / factor combination — a client bug, not a failure.
        raise HTTPException(status_code=422, detail=str(e)) from e
    except FileNotFoundError as e:
        raise HTTPException(status_code=404, detail=str(e)) from e
    except Exception as e:
        logger.exception("Upscale failed")
        raise HTTPException(status_code=500, detail=f"Upscale failed: {e}") from e
    elapsed_ms = int((time.monotonic() - started) * 1000)

    if not result_path.is_file() or result_path.stat().st_size <= 0:
        raise HTTPException(
            status_code=500, detail=f"Upscale produced no output at {result_path}"
        )

    with Image.open(result_path) as output_image:
        width, height = output_image.size

    model_used = None
    if resolved.workflow != upscaler.WORKFLOW_FAST:
        model_factor = (
            4
            if resolved.workflow == upscaler.WORKFLOW_BALANCED and factor == 2
            else factor
        )
        model_used = upscaler._WORKFLOW_MODEL_IDS[resolved.workflow][model_factor]

    return {
        "output_path": str(result_path),
        "width": width,
        "height": height,
        "original_width": original_width,
        "original_height": original_height,
        "workflow": resolved.workflow,
        "factor": factor,
        "model_used": model_used,
        "inference_ms": elapsed_ms,
    }


@app.get(
    "/prompt",
    responses={
        200: {
            "description": (
                "The built-in tag instruction (PromptDefaultsDto): what /tag sends "
                "when the request has no user_prompt."
            )
        }
    },
)
def get_prompt_defaults() -> dict:
    """Hand the host the exact instruction built into this sidecar.

    Step 2 loads it into the editable box so a user can tweak the shipped
    wording instead of retyping it, and so the box can always show what a blank
    field actually means. The sidecar stays the single source of truth - a copy
    in the host would silently drift the moment the built-in prompt changes.
    """
    return {"default_user_prompt": inference_engine.DEFAULT_VLM_USER_PROMPT}


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
    # Scheduling the exit is the whole job: nothing waits on a flag, and the hard
    # os._exit() below is what actually stops the server.
    threading.Thread(target=_delayed_exit, name="shutdown", daemon=True).start()
    return {"status": "shutting_down"}


def _delayed_exit() -> None:
    import time

    time.sleep(0.5)  # allow the response to flush
    os._exit(0)


# ---------------------------------------------------------------------------
# Entry point
# ---------------------------------------------------------------------------


def _build_arg_parser() -> "argparse.ArgumentParser":
    """The sidecar's command line. Split out so the arguments the host passes
    can be asserted without starting a server.

    ``--port`` is the port to bind; 0 lets the OS pick one and the host reads it
    from the port file. ``--device`` is the compute device for the tag pipeline
    (manual runs; the host passes ``config.DEVICE_ENV_VAR`` instead), so the boot
    warm-up loads on it rather than on the CPU default.
    """
    import argparse

    parser = argparse.ArgumentParser(description="Synapic inference sidecar")
    parser.add_argument("--port", type=int, default=0, help="0 = OS-assigned free port")
    parser.add_argument("--host", default="127.0.0.1")
    parser.add_argument(
        "--device",
        default="",
        choices=("cpu", "cuda", "mps"),
        help=(
            "Compute device for the tag pipeline (cpu, cuda or mps). Overrides "
            f"{config.DEVICE_ENV_VAR}, which is how the host passes the Step 2 "
            "selection so the boot warm-up loads on it instead of the CPU "
            "default."
        ),
    )
    return parser


def main() -> None:
    global _uvicorn_server

    import socket as _socket

    args = _build_arg_parser().parse_args()

    # The flag is explicit when given, otherwise the host's environment
    # variable (see config.DEVICE_ENV_VAR). Before the port file and before
    # uvicorn's lifespan: the warm-up thread is started there and must already
    # see the requested device.
    requested_device = args.device or os.environ.get(config.DEVICE_ENV_VAR, "")
    apply_launch_device(requested_device)

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
