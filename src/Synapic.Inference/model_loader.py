"""Hugging Face model loading, downloading, and cache management.

Port of the Python Synapic app's ``src/core/huggingface_utils.py`` — the
functions the sidecar needs for the HTTP API: device detection, download with
progress, cached model loading, task suggestion, model compatibility checks,
and the label-probability inference path used by the scoring tier 2. Only
I/O boundaries changed: instead of posting progress tuples into a UI queue,
state is exposed through a thread-safe status snapshot the service layer
serializes into ``/health``.
"""

from __future__ import annotations

import difflib
import logging
import os
import platform
import sys
import threading
import time
from typing import Any, Dict, List, Optional, Tuple

import config  # noqa: E402 (flat imports: PyInstaller entry compatibility)

logger = logging.getLogger(__name__)

# ============================================================================
# RUNTIME STATE (read by /health and /models/list)
# ============================================================================

_state_lock = threading.Lock()
_state: Dict[str, Any] = {
    "status": "ready",        # ready | loading | error — server is up; the
                               # model loads lazily on the first /tag
    "model": None,
    "device": None,
    "vram_used_mb": None,
    "error": None,
}

# Session model cache: (model_id, task, device) -> pipeline
_model_cache: Dict[Tuple[str, str, str], Any] = {}
_model_cache_lock = threading.Lock()
# Serializes pipeline construction (see load_model).
_model_build_lock = threading.Lock()

# Background download state for /health: model_id ->
# {"status": "downloading"|"complete"|"failed", "done": int, "total": int,
#  "expected_total": int|None, "error": str|None, "finished_at": float|None}
_DOWNLOAD_PROGRESS: Dict[str, Dict[str, Any]] = {}
_DOWNLOAD_PROGRESS_LOCK = threading.Lock()

# How long a finished download (complete/failed) stays visible in /health so
# the UI can show the outcome before it disappears.
_DOWNLOAD_COMPLETE_TTL_SECONDS = 30.0


def set_status(status: str, error: Optional[str] = None) -> None:
    with _state_lock:
        _state["status"] = status
        _state["error"] = error


def set_loaded_model(model_id: str, device: str) -> None:
    with _state_lock:
        _state["status"] = "ready"
        _state["model"] = model_id
        _state["device"] = device
        _state["error"] = None
        _state["vram_used_mb"] = _read_vram_used_mb()


def _read_vram_used_mb() -> Optional[int]:
    try:
        import torch

        if torch.cuda.is_available():
            used, _total = torch.cuda.mem_get_info(0)
            free_total = torch.cuda.get_device_properties(0).total_memory
            return int((free_total - used) / (1024 * 1024))
    except Exception:  # pragma: no cover - CUDA not present / driver issue
        pass
    try:
        import psutil

        return int(psutil.Process().memory_info().rss / (1024 * 1024))
    except Exception:
        return None


def get_state_snapshot() -> Dict[str, Any]:
    with _state_lock:
        snapshot = dict(_state)
    return snapshot


def get_download_progress(model_id: str) -> Tuple[int, int]:
    with _DOWNLOAD_PROGRESS_LOCK:
        entry = _DOWNLOAD_PROGRESS.get(model_id)
        if not entry:
            return (0, 0)
        return (int(entry.get("done", 0)), int(entry.get("total", 0)))


def _set_download_progress(model_id: str, done: int, total: int) -> None:
    with _DOWNLOAD_PROGRESS_LOCK:
        entry = _DOWNLOAD_PROGRESS.setdefault(
            model_id, {"status": "downloading", "done": 0, "total": 0}
        )
        entry["done"] = max(int(done), 0)
        entry["total"] = max(int(total), 0)


def reset_download_state() -> None:
    """Clear all download state (test helper)."""
    with _DOWNLOAD_PROGRESS_LOCK:
        _DOWNLOAD_PROGRESS.clear()


def mark_download_started(model_id: str, expected_total: Optional[int] = None) -> None:
    with _DOWNLOAD_PROGRESS_LOCK:
        _DOWNLOAD_PROGRESS[model_id] = {
            "status": "downloading",
            "done": 0,
            "total": 0,
            "expected_total": int(expected_total) if expected_total else None,
            "error": None,
            "finished_at": None,
        }


def mark_download_complete(model_id: str, done: int = 0, total: int = 0) -> None:
    import time as _time

    with _DOWNLOAD_PROGRESS_LOCK:
        entry = _DOWNLOAD_PROGRESS.get(model_id) or {}
        _DOWNLOAD_PROGRESS[model_id] = {
            "status": "complete",
            "done": max(int(done), 0),
            "total": max(int(total), 0),
            "expected_total": entry.get("expected_total"),
            "error": None,
            "finished_at": _time.time(),
        }


def mark_download_failed(model_id: str, error: str) -> None:
    import time as _time

    with _DOWNLOAD_PROGRESS_LOCK:
        entry = _DOWNLOAD_PROGRESS.get(model_id) or {}
        _DOWNLOAD_PROGRESS[model_id] = {
            "status": "failed",
            "done": int(entry.get("done", 0) or 0),
            "total": int(entry.get("total", 0) or 0),
            "expected_total": entry.get("expected_total"),
            "error": error,
            "finished_at": _time.time(),
        }


def get_active_download() -> Optional[Dict[str, Any]]:
    """Snapshot of the download to surface via /health, or None.

    Prefers an in-flight download; otherwise a download that finished
    (complete/failed) within the TTL, most recent first.
    """
    import time as _time

    now = _time.time()
    with _DOWNLOAD_PROGRESS_LOCK:
        active = None
        finished = None
        for model_id, entry in _DOWNLOAD_PROGRESS.items():
            status = entry.get("status")
            if status == "downloading":
                active = (model_id, entry)
                break
            if (
                status in ("complete", "failed")
                and entry.get("finished_at")
                and now - float(entry["finished_at"]) <= _DOWNLOAD_COMPLETE_TTL_SECONDS
            ):
                if finished is None or float(entry["finished_at"]) > float(
                    finished[1].get("finished_at") or 0
                ):
                    finished = (model_id, entry)

        chosen = active or finished
        if chosen is None:
            return None

        model_id, entry = chosen
        total = int(entry.get("total") or entry.get("expected_total") or 0)
        return {
            "model_id": model_id,
            "status": entry.get("status"),
            "done_bytes": int(entry.get("done") or 0),
            "total_bytes": total,
            "error": entry.get("error"),
        }


# ============================================================================
# DEVICE DETECTION
# ============================================================================


def get_device_info() -> Dict[str, Any]:
    """Probe available compute devices (CPU, CUDA, MPS)."""
    info: Dict[str, Any] = {
        "devices": ["CPU"],
        "default": "CPU",
        "debug_info": {
            "platform": platform.platform(),
            "python_version": sys.version.split()[0],
        },
    }
    try:
        import torch

        info["debug_info"]["torch_version"] = torch.__version__
        info["debug_info"]["cuda_available"] = torch.cuda.is_available()
        info["debug_info"]["mps_available"] = (
            hasattr(torch.backends, "mps") and torch.backends.mps.is_available()
        )

        if torch.cuda.is_available():
            info["devices"].append("CUDA")
            info["default"] = "CUDA"
            info["debug_info"]["cuda_version"] = torch.version.cuda
            info["debug_info"]["cuda_device_count"] = torch.cuda.device_count()
            info["debug_info"]["cuda_device_name"] = torch.cuda.get_device_name(0)

        if hasattr(torch.backends, "mps") and torch.backends.mps.is_available():
            info["devices"].append("MPS")
            if info["default"] == "CPU":
                info["default"] = "MPS"
    except ImportError:
        info["debug_info"]["torch_available"] = False

    return info


def _resolve_device_str(device: str) -> str:
    """Normalize a requested device; fall back to CPU when unavailable."""
    requested = (device or "cpu").lower()
    info = get_device_info()
    available = [d.lower() for d in info["devices"]]
    if requested in available:
        return requested
    logger.warning(
        f"Requested device '{device}' not available (have {info['devices']}); falling back to CPU"
    )
    return "cpu"


def _torch_device_arg(device_str: str):
    """Map 'cpu'/'cuda'/'mps' to the pipeline device argument."""
    if device_str == "cpu":
        return -1
    return device_str  # 'cuda' or 'mps' accepted by transformers


# ============================================================================
# MODEL COMPATIBILITY
# ============================================================================


def is_model_compatible(model_id: str) -> bool:
    """Filter models requiring special quantization libraries we don't bundle."""
    model_id_lower = model_id.lower()
    for pattern in config.INCOMPATIBLE_MODEL_PATTERNS:
        if pattern.lower() in model_id_lower:
            logger.debug(f"Model {model_id} is incompatible (matched pattern: {pattern})")
            return False
    return True


def get_incompatibility_reason(model_id: str) -> Optional[str]:
    """Human-readable reason why a model is incompatible, or None."""
    model_id_lower = model_id.lower()
    reasons = {
        "-gptq": "GPTQ quantized (requires auto-gptq library)",
        "int4": "GPTQ/Int4 quantized (requires auto-gptq library)",
        "int8": "Int8 quantized (requires bitsandbytes library)",
        "-awq": "AWQ quantized (requires autoawq library)",
        "-gguf": "GGUF format (requires llama-cpp-python)",
        "-ggml": "GGML format (requires llama-cpp-python)",
        "-exl2": "EXL2 format (requires exllamav2)",
        "-bnb": "BitsAndBytes quantized",
        "-4bit": "4-bit quantized (requires special library)",
        "-8bit": "8-bit quantized (requires special library)",
    }
    for pattern, reason in reasons.items():
        if pattern.lower() in model_id_lower:
            return reason
    return None


# ============================================================================
# CACHE PATHS
# ============================================================================


def get_model_cache_dir(model_id: str) -> str:
    """Return the HF cache directory for a model id (repo folder)."""
    safe = model_id.replace("/", "--")
    return os.path.join(config.HF_CACHE_DIR, f"models--{safe}")


def _get_latest_snapshot_path(model_id: str) -> Optional[str]:
    """Path of the newest snapshot in the model's cache, or None."""
    model_cache_dir = get_model_cache_dir(model_id)
    snapshot_dir = os.path.join(model_cache_dir, "snapshots")
    if not os.path.isdir(snapshot_dir):
        return None
    snapshots = sorted(os.listdir(snapshot_dir))
    for snap_name in reversed(snapshots):
        snapshot_path = os.path.join(snapshot_dir, snap_name)
        if os.path.isdir(snapshot_path):
            return snapshot_path
    return None


_WEIGHT_EXTENSIONS = (".safetensors", ".bin", ".pt", ".pth", ".onnx", ".gguf")


def is_model_downloaded(model_id: str, token: Optional[str] = None) -> bool:
    """Check if a model is fully downloaded (offline heuristic, no network).

    A fully downloaded model has a snapshot containing config.json AND at
    least one weight file. Config-only repos (from Hub browsing) are not
    usable for inference.
    """
    model_cache_dir = get_model_cache_dir(model_id)
    snapshot_dir = os.path.join(model_cache_dir, "snapshots")

    if not os.path.exists(snapshot_dir):
        return False

    snapshots = os.listdir(snapshot_dir)
    if not snapshots:
        return False

    for snap_name in sorted(snapshots):
        snapshot_path = os.path.join(snapshot_dir, snap_name)
        if not os.path.isfile(os.path.join(snapshot_path, "config.json")):
            continue
        has_weights = any(
            f.endswith(_WEIGHT_EXTENSIONS) for f in os.listdir(snapshot_path)
        )
        if has_weights:
            return True

    logger.debug(
        f"Model {model_id} has no snapshot with weight files — treating as not downloaded"
    )
    return False


# ============================================================================
# TASK SUGGESTION
# ============================================================================


def get_suggested_task(model_config: dict) -> str:
    """Suggest a pipeline task from model config (architectures, pipeline_tag)."""
    if "pipeline_tag" in model_config:
        return model_config["pipeline_tag"]

    archs = model_config.get("architectures", [])
    for arch in archs:
        arch_lower = arch.lower()
        if "forimageclassification" in arch_lower:
            return config.MODEL_TASK_IMAGE_CLASSIFICATION
        if "forconditionalgeneration" in arch_lower:
            return config.MODEL_TASK_IMAGE_TO_TEXT
        if "visionencoderdecoder" in arch_lower:
            return config.MODEL_TASK_IMAGE_TO_TEXT
        if "clipmodel" in arch_lower or "siglipmodel" in arch_lower:
            return config.MODEL_TASK_ZERO_SHOT

    mtype = model_config.get("model_type", "").lower()
    if mtype in [
        "blip", "blip-2", "git", "qwen2_vl", "qwen2_5_vl", "qwen3_vl", "llava",
    ]:
        return config.MODEL_TASK_IMAGE_TO_TEXT

    return ""


def get_model_capability(task: str) -> str:
    """Human-readable capability string for a task."""
    return config.CAPABILITY_MAP.get(task, "Unknown")


# ============================================================================
# LOCAL MODEL DISCOVERY (/models/list)
# ============================================================================


def find_local_models() -> List[Dict[str, Any]]:
    """Scan the local HF cache for downloaded models.

    Returns a list of ModelInfo-shaped dicts sorted by id.
    """
    models: List[Dict[str, Any]] = []
    cache_root = config.HF_CACHE_DIR
    if not os.path.isdir(cache_root):
        return models

    for entry in sorted(os.listdir(cache_root)):
        if not entry.startswith("models--"):
            continue
        model_id = entry[len("models--"):].replace("--", "/")
        if not is_model_compatible(model_id):
            continue

        snapshot_path = _get_latest_snapshot_path(model_id)
        if snapshot_path is None:
            continue

        task = ""
        size_mb = 0.0
        downloaded = is_model_downloaded(model_id)
        try:
            cfg_path = os.path.join(snapshot_path, "config.json")
            if os.path.isfile(cfg_path):
                import json

                with open(cfg_path, "r", encoding="utf-8") as cf:
                    cfg = json.load(cf)
                task = get_suggested_task(cfg)

            total = 0
            for root, _dirs, files in os.walk(snapshot_path):
                for f in files:
                    try:
                        total += os.path.getsize(os.path.join(root, f))
                    except OSError:
                        pass
            size_mb = round(total / (1024 * 1024), 1)
        except Exception as e:
            logger.debug(f"Could not inspect cached model {model_id}: {e}")

        models.append(
            {
                "id": model_id,
                "task": task,
                "size_mb": size_mb,
                "path": snapshot_path,
                "downloaded": downloaded,
            }
        )

    return models


# ============================================================================
# DOWNLOAD (background thread; progress surfaced via /health polling)
# ============================================================================


def _total_repo_bytes(
    model_id: str, revision: str = "main", token: Optional[str] = None
) -> Optional[int]:
    """Best-effort total payload size of a repo (for a real % display).

    Queries the Hub for per-file sizes. Returns None when offline or on any
    API failure — snapshot_download's running total is used as fallback.
    """
    try:
        from huggingface_hub import HfApi

        info = HfApi(token=token).model_info(
            model_id, revision=revision, files_metadata=True
        )
        excluded = set(config.MODEL_FILE_EXCLUSIONS)
        total = 0
        for sibling in info.siblings or []:
            filename = (sibling.rfilename or "").rsplit("/", 1)[-1]
            if filename in excluded:
                continue
            size = getattr(sibling, "size", None)
            if size:
                total += int(size)
        return total or None
    except Exception as e:
        logger.debug(f"Could not pre-compute repo size for {model_id}: {e}")
        return None


def download_model(model_id: str, revision: str = "main", token: Optional[str] = None) -> None:
    """Download a model in this thread (service runs it as a daemon job).

    Progress is tracked at byte level: snapshot_download aggregates every
    file's chunk downloads into one shared bar built from our tqdm subclass,
    whose (done, total) we mirror into the /health download state.
    """
    logger.info(f"[Download] Starting download for: {model_id} (revision {revision})")

    if is_model_downloaded(model_id, token=token):
        logger.info(f"[Download] Model {model_id} already fully downloaded — skipping.")
        return

    try:
        from huggingface_hub import snapshot_download
        from tqdm import tqdm

        # Pre-compute the expected payload size so the UI can show a real
        # percentage up front (best effort; falls back to the running total
        # that snapshot_download accumulates as files register).
        expected_total = _total_repo_bytes(model_id, revision=revision, token=token)
        mark_download_started(model_id, expected_total=expected_total)
        if expected_total:
            logger.info(
                f"[Download] {model_id}: ~{expected_total / (1024 * 1024):.0f} MB expected"
            )

        class _ProgressTqdm(tqdm):
            """Mirrors snapshot_download's shared bytes bar into /health state.

            snapshot_download builds exactly one byte-level bar from this class
            (per-file downloads aggregate into it), so self.n / self.total are
            the overall bytes done / expected.
            """

            def __init__(self, *args, **kwargs):
                # hub passes name="huggingface_hub.snapshot_download" which
                # plain tqdm rejects (TqdmKeyError) — drop it.
                kwargs.pop("name", None)
                super().__init__(*args, **kwargs)

            def update(self, n=1):
                super().update(n)
                try:
                    _set_download_progress(model_id, int(self.n or 0), int(self.total or 0))
                except Exception:
                    pass

        logger.info(f"[Download] Calling snapshot_download for {model_id}")
        snapshot_download(
            repo_id=model_id,
            revision=revision,
            token=token,
            tqdm_class=_ProgressTqdm,
            max_workers=4,
        )
        done, total = get_download_progress(model_id)
        mark_download_complete(model_id, done=done, total=total)
        logger.info(f"[Download] Download complete for {model_id}!")
    except Exception as e:
        logger.exception(f"[Download] Failed to download model: {model_id}")
        mark_download_failed(model_id, str(e))
        raise


# ============================================================================
# MODEL LOADING
# ============================================================================


_hf_pipeline: Optional[Any] = None
_hf_pipeline_import_lock = threading.Lock()


def _get_hf_pipeline() -> Any:
    """Import and cache ``transformers.pipeline`` once, under a lock.

    Concurrent first calls race the frozen PyInstaller import otherwise:
    several threads saw a partially-initialized transformers module and
    ``ImportError: cannot import name 'pipeline'`` 503'd the first batch
    (observed with a 4-way parallel run against a cold sidecar).
    """
    global _hf_pipeline
    if _hf_pipeline is None:
        with _hf_pipeline_import_lock:
            if _hf_pipeline is None:
                from transformers import pipeline as hf_pipeline
                _hf_pipeline = hf_pipeline
    return _hf_pipeline


def load_model(
    model_id: str,
    task: str,
    device: str = "cpu",
    token: Optional[str] = None,
) -> Tuple[Any, str]:
    """Load (or reuse) a transformers pipeline for the given model.

    Returns (pipeline, resolved_task). The resolved task may differ from the
    requested one when the model config suggests a compatible swap
    (classification <-> image-to-text), mirroring the original behavior.
    """
    device_str = _resolve_device_str(device)
    cache_key = (model_id, task, device_str)

    with _model_cache_lock:
        cached = _model_cache.get(cache_key)
    if cached is not None:
        set_loaded_model(model_id, device_str)
        return cached, task

    set_status("loading")

    # Construct at most one pipeline at a time. The host fans out parallel
    # /tag requests, so a cold sidecar used to build one pipeline per thread:
    # N redundant copies of the same weights, and - because transformers
    # materializes checkpoint tensors into the instance while loading - some
    # of those concurrent instances kept float32 parameters that no checkpoint
    # tensor ever overwrote. A float32 RMSNorm weight promotes the normalized
    # activations to float32, and the next bfloat16 linear then dies with
    # "expected m1 and m2 to have the same dtype, but got: float != BFloat16",
    # failing that first batch with HTTP 500.
    with _model_build_lock:
        # Another thread may have finished loading while we waited.
        with _model_cache_lock:
            cached = _model_cache.get(cache_key)
        if cached is not None:
            set_loaded_model(model_id, device_str)
            return cached, task

        return _construct_model(model_id, task, device_str, token, cache_key)


def _construct_model(
    model_id: str,
    task: str,
    device_str: str,
    token: Optional[str],
    cache_key: Tuple[str, str, str],
) -> Tuple[Any, str]:
    """Build a single pipeline instance. Caller must hold _model_build_lock."""
    try:
        hf_pipeline = _get_hf_pipeline()

        local_model_path: Optional[str] = None
        if is_model_downloaded(model_id, token=token):
            local_model_path = _get_latest_snapshot_path(model_id)

        if local_model_path is None:
            # Not cached: download first (blocking here is fine — /health
            # reports "loading" while this runs).
            download_model(model_id, token=token)
            local_model_path = _get_latest_snapshot_path(model_id)

        kwargs: Dict[str, Any] = {}
        if local_model_path:
            kwargs["model"] = local_model_path

        resolved_task = task

        # Task suggestion swap (classification <-> image-to-text), as original.
        if local_model_path:
            cfg_path = os.path.join(local_model_path, "config.json")
            if os.path.exists(cfg_path):
                try:
                    import json

                    with open(cfg_path, "r", encoding="utf-8") as cf:
                        cfg = json.load(cf)
                    suggested = get_suggested_task(cfg)
                    if suggested and suggested != task:
                        if (
                            task == config.MODEL_TASK_IMAGE_CLASSIFICATION
                            and suggested == config.MODEL_TASK_IMAGE_TO_TEXT
                        ) or (
                            task == config.MODEL_TASK_IMAGE_TO_TEXT
                            and suggested == config.MODEL_TASK_IMAGE_CLASSIFICATION
                        ):
                            resolved_task = suggested
                except Exception:
                    pass

        logger.info(
            f"Loading pipeline ({resolved_task}) for {model_id} on {device_str}"
        )
        model = hf_pipeline(
            resolved_task,
            device=_torch_device_arg(device_str),
            dtype="auto",
            model_kwargs={"low_cpu_mem_usage": True},
            **kwargs,
        )

        with _model_cache_lock:
            # Evict other entries to keep at most one model resident.
            _model_cache.clear()
            _model_cache[cache_key] = model

        set_loaded_model(model_id, device_str)
        logger.info(f"Model pipeline ({resolved_task}) loaded successfully for: {model_id}")
        return model, resolved_task

    except Exception as e:
        logger.exception(f"Failed to load model: {model_id}")
        set_status("error", f"Failed to load model: {e}")
        raise


def unload_model() -> None:
    """Drop the cached model (frees VRAM/RAM on next GC)."""
    with _model_cache_lock:
        _model_cache.clear()
    set_status("loading", None)


# ============================================================================
# LABEL-PROBABILITY INFERENCE (scoring tier 2)
# ============================================================================


def _get_pipeline_labels(pipe) -> list:
    """Return the model's ordered label set, if the config exposes one."""
    model_config = getattr(getattr(pipe, "model", None), "config", None)
    id2label = getattr(model_config, "id2label", None) if model_config is not None else None
    if isinstance(id2label, dict) and id2label:
        return list(id2label.values())
    if isinstance(id2label, (list, tuple)) and id2label:
        return list(id2label)
    return []


def _fuzzy_match_label(
    candidate: str,
    labels: list,
    cutoff: float = 0.6,
    partial_cutoff: float = 0.45,
) -> Optional[str]:
    """Best model label for a candidate via fuzzy string similarity, or None.

    Case-insensitive and whitespace-insensitive. A candidate that is a
    prefix/substring of a compound model label qualifies when it is at least
    3 characters. String similarity only — not semantic similarity.
    """
    pairs = [(label, label.strip().lower()) for label in labels if label and label.strip()]
    if not pairs:
        return None

    best_label = None
    best_normalized = None
    best_ratio = 0.0
    for label, normalized in pairs:
        ratio = difflib.SequenceMatcher(None, candidate, normalized).ratio()
        if ratio > best_ratio:
            best_label, best_normalized, best_ratio = label, normalized, ratio

    if best_label is None or best_normalized is None:
        return None
    if best_ratio >= cutoff:
        return best_label
    is_substring_match = (
        len(candidate) >= 3
        and (candidate in best_normalized or best_normalized in candidate)
        and best_ratio >= partial_cutoff
    )
    return best_label if is_substring_match else None


def _format_label_sample(model_labels: list, scored_labels: list, n: int = 8) -> str:
    """Readable sample of the model's labels for error text."""
    combined = list(dict.fromkeys(model_labels + scored_labels))
    if not combined:
        return "(label list unavailable)"
    sample = combined[:n]
    if len(combined) > len(sample):
        return ", ".join(sample) + "..."
    return ", ".join(sample)


def run_local_label_confidence_inference(
    model,
    image_path: str,
    candidates: list,
) -> Dict[str, float]:
    """Run local inference with per-label probabilities.

    Reuses the already-loaded pipeline (weights are never reloaded per image).
    Candidates that cannot be matched to a model label (after normalization
    and fuzzy fallback) map to 0.0. Raises ValueError when the pipeline task
    is not image-classification or when no candidate matches any label.
    """
    from transformers import Pipeline

    if not candidates:
        return {}

    if isinstance(model, Pipeline):
        pipe = model
        task = getattr(pipe, "task", "") or ""
        if task != config.MODEL_TASK_IMAGE_CLASSIFICATION:
            raise ValueError(
                "Probability scoring requires an image-classification pipeline, "
                f"but the loaded pipeline task is '{task}'. Only "
                "image-classification models expose per-label probabilities."
            )
    else:
        pipe = _get_hf_pipeline()(config.MODEL_TASK_IMAGE_CLASSIFICATION, model=model)

    # Request every label so candidates are never silently dropped by the
    # pipeline's default top_k=5 truncation.
    pipe_config = getattr(getattr(pipe, "model", None), "config", None)
    num_labels = getattr(pipe_config, "num_labels", None) if pipe_config is not None else None
    top_k = num_labels if isinstance(num_labels, int) and num_labels > 0 else 10_000

    results = pipe(image_path, top_k=top_k)

    score_map = {item["label"]: item["score"] for item in results}
    scored_labels = list(score_map.keys())

    normalized_index = {label.strip().lower(): label for label in scored_labels}

    probabilities: Dict[str, float] = {}
    for candidate in candidates:
        normalized = candidate.strip().lower()
        matched_label = normalized_index.get(normalized)
        if matched_label is None:
            matched_label = _fuzzy_match_label(normalized, scored_labels)
        probabilities[candidate] = (
            score_map[matched_label] if matched_label is not None else 0.0
        )

    if probabilities and all(score == 0.0 for score in probabilities.values()):
        model_labels = _get_pipeline_labels(pipe) or scored_labels
        raise ValueError(
            "Probability scoring: none of the candidate tokens matched the selected model's labels. "
            "This usually means the candidate list is not compatible with this image-classification model "
            "(for example placeholder tokens such as 'A,B,C,D' against an ImageNet model). "
            "Choose candidates that correspond to the model's actual classification labels. "
            f"Model labels include: {_format_label_sample(model_labels, scored_labels, n=8)}. "
            f"Candidates: {sorted(candidates)}."
        )

    return probabilities


# ============================================================================
# CLIP EMBEDDING SCORER (scoring tier 2.5)
# ============================================================================

EMBEDDING_MODEL_ID = "openai/clip-vit-base-patch32"
EMBEDDING_TEMPERATURE = 0.01


class TransformersCLIPScorer:
    """True CLIP image-text cosine similarity scorer using transformers.

    Lazy, cached loading; text embeddings cached per candidate set so batch
    runs neither reload weights nor re-encode identical prompts per image.
    """

    _loaded_models: Dict[str, Dict[str, Any]] = {}
    _text_features_cache: Dict[Tuple[str, str], Any] = {}

    def __init__(self, model_id: str = EMBEDDING_MODEL_ID):
        self.model_id = model_id

    def _load_model_and_processor(self) -> Dict[str, Any]:
        cached = TransformersCLIPScorer._loaded_models.get(self.model_id)
        if cached is not None:
            return cached

        import torch
        from transformers import CLIPModel, CLIPProcessor

        model = CLIPModel.from_pretrained(self.model_id)
        processor = CLIPProcessor.from_pretrained(self.model_id)
        model.eval()

        device = self._resolve_device(torch)
        if device is not None:
            model.to(device)

        entry = {"model": model, "processor": processor, "device": device}
        TransformersCLIPScorer._loaded_models[self.model_id] = entry
        return entry

    @staticmethod
    def _resolve_device(torch):
        if torch.cuda.is_available():
            return "cuda"
        if hasattr(torch.backends, "mps") and torch.backends.mps.is_available():
            return "mps"
        return None

    def _text_features(self, cache_entry: Dict[str, Any], candidates: List[str]):
        import torch

        cache_key = (self.model_id, "|".join(candidates))
        cached = TransformersCLIPScorer._text_features_cache.get(cache_key)
        if cached is not None:
            return cached

        model = cache_entry["model"]
        processor = cache_entry["processor"]
        device = cache_entry["device"]

        with torch.no_grad():
            text_inputs = processor(text=list(candidates), return_tensors="pt", padding=True)
            if device is not None:
                text_inputs = {
                    k: v.to(device) if hasattr(v, "to") else v
                    for k, v in text_inputs.items()
                }
            text_features = model.get_text_features(**text_inputs)
            text_features = text_features / text_features.norm(dim=-1, keepdim=True)

        TransformersCLIPScorer._text_features_cache[cache_key] = text_features
        return text_features

    def cosine_similarities(self, image_path: str, candidates: List[str]) -> Dict[str, float]:
        """Return one true cosine similarity per candidate in [-1, 1]."""
        import torch

        if not candidates:
            return {}

        cache_entry = self._load_model_and_processor()
        model = cache_entry["model"]
        processor = cache_entry["processor"]
        device = cache_entry["device"]

        with torch.no_grad():
            image_inputs = processor(images=image_path, return_tensors="pt")
            if device is not None:
                image_inputs = {
                    k: v.to(device) if hasattr(v, "to") else v
                    for k, v in image_inputs.items()
                }
            image_features = model.get_image_features(**image_inputs)
            image_features = image_features / image_features.norm(dim=-1, keepdim=True)

        text_features = self._text_features(cache_entry, candidates)

        cos = (image_features * text_features).sum(dim=-1)
        values = cos.tolist() if hasattr(cos, "tolist") else [float(cos)]
        return {candidate: float(v) for candidate, v in zip(candidates, values)}


# ============================================================================
# SCORING ADAPTERS (tier selector + ladder)
# ============================================================================


def pick_scoring_tier(
    provider: str,
    task: str,
    candidates: List[str],
    mode: str,
    probability_enabled: bool = False,
) -> Optional["SCORING_TIER_LIKE"]:
    """Return the highest available scoring tier for this request config.

    Returns None when no scoring pass runs at all (LLM-only mode, no
    candidates, or a non-classification local model).
    """
    del probability_enabled  # kept for signature parity with the original

    provider = (provider or "local").lower()
    if provider != "local":
        return None

    if mode == "llm" or not candidates:
        return None

    if task == config.MODEL_TASK_IMAGE_CLASSIFICATION:
        from keyword_scoring import SCORING_TIER

        return SCORING_TIER.LABEL_CONFIDENCE

    return None


def score_keywords(
    model,
    image_path: str,
    candidates: List[str],
    provider: str = "local",
    task: str = "",
    mode: str = "llm",
    embedding_rescue_enabled: bool = False,
):
    """Run the tier ladder (2 -> 2.5 -> 0) for the request's candidate set.

    Never raises for provider/parse failures — degrades to a tier-0
    unavailable_result with the reason recorded ("hide on failure").
    """
    import time as _time

    from keyword_scoring import (
        SCORING_TIER,
        ScoreResult,
        build_score_result,
        softmax_from_similarities,
        unavailable_result,
    )

    del _time

    candidate_list = list(candidates or [])
    tier = pick_scoring_tier(provider, task, candidate_list, mode)

    if tier is None:
        return unavailable_result(
            "Scoring not available for this engine configuration (mode, "
            "provider, or candidate set).",
            candidate_list,
        )

    if tier == SCORING_TIER.LABEL_CONFIDENCE:
        if model is None:
            return unavailable_result(
                "No local pipeline loaded for label-confidence scoring.",
                candidate_list,
            )
        try:
            return _score_local_label_confidence(model, image_path, candidate_list)
        except Exception as exc:
            logger.warning(
                "Label-confidence scoring failed (%s: %s).", type(exc).__name__, exc
            )
            if embedding_rescue_enabled:
                embedding_result = _try_embedding_tier(image_path, candidate_list)
                if embedding_result is not None:
                    return embedding_result
            return unavailable_result(
                f"Label-confidence scoring failed: {type(exc).__name__}: {exc}",
                candidate_list,
            )

    return unavailable_result(f"Unhandled scoring tier: {tier}", candidate_list)


def _score_local_label_confidence(
    model, image_path: str, candidate_list: List[str]
) -> "ScoreResultLike":
    from keyword_scoring import SCORING_TIER, build_score_result

    score_map = run_local_label_confidence_inference(model, image_path, candidate_list)

    match_types: Dict[str, str] = {}
    fuzzy_hits: List[str] = []
    unmatched: List[str] = []
    try:
        labels = _get_pipeline_labels(model) or list(score_map.keys())
    except Exception:
        labels = list(score_map.keys())
    normalized_label_index = {
        str(lbl).strip().lower() for lbl in labels if lbl and str(lbl).strip()
    }
    for candidate in candidate_list:
        normalized = candidate.strip().lower()
        if normalized in normalized_label_index:
            match_types[candidate] = "exact"
            continue
        fuzzy_target = None
        try:
            fuzzy_target = _fuzzy_match_label(normalized, labels)
        except Exception:
            logger.debug("Fuzzy re-derivation failed", exc_info=True)
        if fuzzy_target is not None:
            match_types[candidate] = "fuzzy"
            fuzzy_hits.append(candidate)
        else:
            match_types[candidate] = "none"
            unmatched.append(candidate)

    notes: List[str] = []
    if fuzzy_hits:
        notes.append(
            "Fuzzy-matched candidates score the nearest model label "
            "(string similarity, not the candidate concept): "
            + ", ".join(fuzzy_hits)
        )
    if unmatched:
        notes.append("Unmatched candidates score 0.0: " + ", ".join(unmatched))

    return build_score_result(
        candidate_list,
        score_map,
        SCORING_TIER.LABEL_CONFIDENCE,
        match_types=match_types,
        notes=notes,
    )


def score_keywords_embedding(
    image_path: str,
    candidates: List[str],
    temperature: float = EMBEDDING_TEMPERATURE,
):
    """Tier 2.5: softmaxed cosine similarities between image and prompts."""
    from keyword_scoring import SCORING_TIER, build_score_result, softmax_from_similarities, unavailable_result

    if not candidates:
        return unavailable_result("No candidate keywords supplied.", candidates)

    scorer = TransformersCLIPScorer(EMBEDDING_MODEL_ID)
    sims = scorer.cosine_similarities(image_path, candidates)
    distribution = softmax_from_similarities(sims, candidates, temperature=temperature)

    raw_line = "; ".join(
        f"{candidate}={sims.get(candidate, float('nan')):.3f}" for candidate in candidates
    )
    notes = [
        f"Raw CLIP cosine similarities (image vs prompt): {raw_line}.",
        "Distribution scores are CLIP similarities softmaxed over THIS "
        "candidate set; adding or removing candidates shifts every score "
        "without the image changing (not calibrated).",
        "Only rank ordering within one candidate set is meaningful for the "
        "distribution; the raw cosine values above are the comparable signal "
        "across runs.",
    ]

    return build_score_result(
        candidates,
        distribution,
        SCORING_TIER.EMBEDDING,
        match_types={c: "semantic" for c in candidates},
        notes=notes,
    )


def _try_embedding_tier(image_path: str, candidate_list: List[str]):
    """Attempt tier 2.5; return None when the CLIP backend is unavailable."""
    try:
        return score_keywords_embedding(image_path, candidate_list, temperature=EMBEDDING_TEMPERATURE)
    except Exception as exc:
        logger.warning("Embedding scoring failed (%s: %s).", type(exc).__name__, exc)
        return None
