"""Tests for model_loader: cache heuristics, compatibility filters, task
suggestion, fuzzy matching — no network or torch downloads required.
"""

import sys
from pathlib import Path

import pytest

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / "src" / "Synapic.Inference"))

import model_loader  # noqa: E402
from config import (  # noqa: E402
    MODEL_TASK_IMAGE_CLASSIFICATION,
    MODEL_TASK_IMAGE_TO_TEXT,
    MODEL_TASK_ZERO_SHOT,
)


class TestCompatibility:
    def test_standard_model_compatible(self):
        assert model_loader.is_model_compatible("LiquidAI/LFM2.5-VL-1.6B")

    def test_gguf_incompatible(self):
        assert not model_loader.is_model_compatible("someone/model-gguf")

    def test_gptq_incompatible(self):
        assert not model_loader.is_model_compatible("TheBloke/foo-7B-GPTQ")

    def test_reason_reported(self):
        reason = model_loader.get_incompatibility_reason("m/model-awq")
        assert reason is not None
        assert "autoawq" in reason.lower()


class TestTaskSuggestion:
    def test_pipeline_tag_wins(self):
        assert model_loader.get_suggested_task({"pipeline_tag": "image-classification"}) == MODEL_TASK_IMAGE_CLASSIFICATION

    def test_vlm_architecture(self):
        cfg = {"architectures": ["Qwen2_5_VLForConditionalGeneration"]}
        assert model_loader.get_suggested_task(cfg) == MODEL_TASK_IMAGE_TO_TEXT

    def test_classification_architecture(self):
        cfg = {"architectures": ["ViTForImageClassification"]}
        assert model_loader.get_suggested_task(cfg) == MODEL_TASK_IMAGE_CLASSIFICATION

    def test_clip_architecture(self):
        cfg = {"architectures": ["CLIPModel"]}
        assert model_loader.get_suggested_task(cfg) == MODEL_TASK_ZERO_SHOT

    def test_model_type_fallback(self):
        assert model_loader.get_suggested_task({"model_type": "llava"}) == MODEL_TASK_IMAGE_TO_TEXT

    def test_unknown_returns_empty(self):
        assert model_loader.get_suggested_task({}) == ""


class TestFuzzyMatchLabel:
    def test_exact_normalized_match(self):
        assert model_loader._fuzzy_match_label("dog", ["Dog"]) == "Dog"

    def test_substring_match(self):
        assert model_loader._fuzzy_match_label("indoor", ["indoor office"]) == "indoor office"

    def test_single_char_never_matches(self):
        assert model_loader._fuzzy_match_label("A", ["art"]) is None

    def test_no_labels(self):
        assert model_loader._fuzzy_match_label("dog", []) is None


class TestCacheHeuristics:
    def test_missing_cache_dir_not_downloaded(self, tmp_path, monkeypatch):
        monkeypatch.setattr(model_loader.config, "HF_CACHE_DIR", str(tmp_path / "missing"))
        assert not model_loader.is_model_downloaded("org/model")

    def test_config_only_snapshot_not_downloaded(self, tmp_path, monkeypatch):
        cache = tmp_path / "models--org--model" / "snapshots" / "abc123"
        cache.mkdir(parents=True)
        (cache / "config.json").write_text("{}")
        monkeypatch.setattr(model_loader.config, "HF_CACHE_DIR", str(tmp_path))
        assert not model_loader.is_model_downloaded("org/model")

    def test_snapshot_with_weights_is_downloaded(self, tmp_path, monkeypatch):
        cache = tmp_path / "models--org--model" / "snapshots" / "abc123"
        cache.mkdir(parents=True)
        (cache / "config.json").write_text("{}")
        (cache / "model.safetensors").write_text("weights")
        monkeypatch.setattr(model_loader.config, "HF_CACHE_DIR", str(tmp_path))
        assert model_loader.is_model_downloaded("org/model")

    def test_latest_snapshot_path_found(self, tmp_path, monkeypatch):
        (tmp_path / "models--org--model" / "snapshots" / "aaa").mkdir(parents=True)
        (tmp_path / "models--org--model" / "snapshots" / "bbb").mkdir(parents=True)
        monkeypatch.setattr(model_loader.config, "HF_CACHE_DIR", str(tmp_path))
        path = model_loader._get_latest_snapshot_path("org/model")
        assert path is not None and path.endswith("bbb")


class TestState:
    def test_initial_state_is_loading(self):
        snapshot = model_loader.get_state_snapshot()
        assert snapshot["status"] in {"loading", "ready", "error"}

    def test_set_and_clear_error(self):
        model_loader.set_status("error", "boom")
        assert model_loader.get_state_snapshot()["error"] == "boom"
        model_loader.set_status("loading", None)
        assert model_loader.get_state_snapshot()["error"] is None


class TestDownloadProgress:
    """Byte-level download progress surfaced via /health (hub calls mocked).

    snapshot_download aggregates every file's chunk downloads into one shared
    bytes bar built from our tqdm_class, whose (n, total) we mirror into the
    download state.
    """

    def _fake_snapshot(self, total, chunks):
        def fake(**kwargs):
            bar = kwargs["tqdm_class"](
                total=0, desc="Downloading (incomplete total...)",
                disable=False, unit="B", unit_scale=True, name="test",
            )
            bar.total = total  # files register their sizes into the shared bar
            for chunk in chunks:
                bar.update(chunk)
            return "/fake/snapshot"

        return fake

    def test_progress_lifecycle(self, monkeypatch):
        model_loader.reset_download_state()
        monkeypatch.setattr(model_loader, "_total_repo_bytes", lambda *a, **k: 500)
        monkeypatch.setattr(model_loader, "is_model_downloaded", lambda *a, **k: False)
        monkeypatch.setattr(
            "huggingface_hub.snapshot_download", self._fake_snapshot(500, [200, 300])
        )

        model_loader.download_model("LiquidAI/LFM2.5-VL-450M")

        snap = model_loader.get_active_download()
        assert snap is not None
        assert snap["model_id"] == "LiquidAI/LFM2.5-VL-450M"
        assert snap["status"] == "complete"
        assert snap["done_bytes"] == 500
        assert snap["total_bytes"] == 500

    def test_mid_download_partial_progress(self, monkeypatch):
        model_loader.reset_download_state()
        monkeypatch.setattr(model_loader, "is_model_downloaded", lambda *a, **k: False)
        monkeypatch.setattr(model_loader, "_total_repo_bytes", lambda *a, **k: 400)

        captured = {}
        real_set = model_loader._set_download_progress

        def spy(model_id, done, total):
            real_set(model_id, done, total)
            captured["last"] = (done, total)

        monkeypatch.setattr(model_loader, "_set_download_progress", spy)
        monkeypatch.setattr(
            "huggingface_hub.snapshot_download", self._fake_snapshot(400, [150])
        )

        model_loader.download_model("org/model")

        assert captured["last"] == (150, 400)
        done, total = model_loader.get_download_progress("org/model")
        assert (done, total) == (150, 400)

    def test_already_downloaded_skips_network(self, monkeypatch):
        model_loader.reset_download_state()
        monkeypatch.setattr(model_loader, "is_model_downloaded", lambda *a, **k: True)
        calls = []

        def fail(**kwargs):
            calls.append(True)
            raise AssertionError("snapshot_download must not be called")

        monkeypatch.setattr("huggingface_hub.snapshot_download", fail)
        model_loader.download_model("org/model")
        assert calls == []
        assert model_loader.get_active_download() is None

    def test_failure_recorded_not_fatal_to_server(self, monkeypatch):
        model_loader.reset_download_state()
        monkeypatch.setattr(model_loader, "is_model_downloaded", lambda *a, **k: False)
        monkeypatch.setattr(model_loader, "_total_repo_bytes", lambda *a, **k: None)

        def boom(**kwargs):
            raise RuntimeError("hub down")

        monkeypatch.setattr("huggingface_hub.snapshot_download", boom)
        with pytest.raises(RuntimeError):
            model_loader.download_model("org/model")

        snap = model_loader.get_active_download()
        assert snap is not None
        assert snap["status"] == "failed"
        assert "hub down" in snap["error"]
        # A download failure must NOT flip the server into the error state.
        assert model_loader.get_state_snapshot()["status"] != "error"

    def test_finished_hidden_after_ttl(self, monkeypatch):
        model_loader.reset_download_state()
        model_loader.mark_download_started("org/model")
        assert model_loader.get_active_download() is not None

        monkeypatch.setattr(model_loader, "_DOWNLOAD_COMPLETE_TTL_SECONDS", 0)
        model_loader.mark_download_complete("org/model", done=10, total=10)
        assert model_loader.get_active_download() is None

    def test_active_download_preferred_over_finished(self):
        model_loader.reset_download_state()
        model_loader.mark_download_complete("a/model", done=10, total=10)
        model_loader.mark_download_started("b/model")
        snap = model_loader.get_active_download()
        assert snap is not None
        assert snap["model_id"] == "b/model"
        assert snap["status"] == "downloading"


class TestConcurrentModelLoads:
    """A cold sidecar used to build one pipeline per parallel /tag request.

    The host fans out its first batch, so four threads could all miss the
    cache and each construct the same model. Besides loading several
    redundant copies of the weights, transformers materializes checkpoint
    tensors into the instance while loading, so a concurrently-built
    instance could keep a float32 parameter that no checkpoint tensor ever
    overwrote. A float32 RMSNorm weight promotes the normalized activations
    to float32 and the next bfloat16 linear dies with
    "expected m1 and m2 to have the same dtype, but got: float != BFloat16"
    - an HTTP 500 on the first batch of an otherwise healthy run.
    """

    MODEL = "org/model"
    TASK = MODEL_TASK_IMAGE_TO_TEXT

    @staticmethod
    def _patch_pipeline(monkeypatch, construct):
        monkeypatch.setattr(model_loader, "_get_hf_pipeline", lambda: construct)
        monkeypatch.setattr(model_loader, "is_model_downloaded", lambda *a, **k: False)
        monkeypatch.setattr(model_loader, "download_model", lambda *a, **k: None)
        monkeypatch.setattr(
            model_loader, "_get_latest_snapshot_path", lambda *a, **k: None
        )
        model_loader._model_cache.clear()

    def test_parallel_cold_loads_construct_one_pipeline(self, monkeypatch):
        import threading
        import time

        calls = []
        calls_lock = threading.Lock()

        def construct(task, **kwargs):
            with calls_lock:
                calls.append(task)
            time.sleep(0.05)  # widen the window the old race needed
            return object()

        self._patch_pipeline(monkeypatch, construct)

        results = []
        barrier = threading.Barrier(4)

        def worker():
            barrier.wait()
            results.append(
                model_loader.load_model(self.MODEL, self.TASK, device="cpu")[0]
            )

        threads = [threading.Thread(target=worker) for _ in range(4)]
        for thread in threads:
            thread.start()
        for thread in threads:
            thread.join()

        assert len(calls) == 1
        assert len(results) == 4
        assert len({id(model) for model in results}) == 1
        assert len(model_loader._model_cache) == 1

    def test_second_call_reuses_the_cached_pipeline(self, monkeypatch):
        calls = []

        def construct(task, **kwargs):
            calls.append(task)
            return object()

        self._patch_pipeline(monkeypatch, construct)

        first, _ = model_loader.load_model(self.MODEL, self.TASK, device="cpu")
        second, _ = model_loader.load_model(self.MODEL, self.TASK, device="cpu")

        assert first is second
        assert len(calls) == 1

    def test_state_reports_ready_after_a_load(self, monkeypatch):
        self._patch_pipeline(monkeypatch, lambda task, **kwargs: object())

        model_loader.load_model(self.MODEL, self.TASK, device="cpu")

        assert model_loader.get_state_snapshot()["status"] == "ready"


class TestLoadDtype:
    """The CPU load dtype is a performance cliff, not a detail.

    ``dtype="auto"`` keeps the checkpoint's bfloat16 on CPU (transformers does
    not upcast it). x86 parts without AVX512-BF16/AMX - every mainstream
    12th-14th gen Core part - have no native bfloat16 GEMM, so oneDNN falls
    back to an emulated path ~1000x slower than the fp32 kernel. LFM2.5-VL
    ships bf16 weights, so the vision tower and prefill ran on that path:
    4.7s per image instead of 2.0s, and 75 CPU-seconds of work on the fixed
    block instead of 20. Pin the split so nobody simplifies it back.
    """

    MODEL = "org/model"
    TASK = MODEL_TASK_IMAGE_TO_TEXT

    def test_cpu_loads_float32(self):
        # A string, not torch.float32: CI's contract tests run with no torch,
        # and transformers turns the string back via getattr(torch, dtype).
        assert model_loader._load_dtype("cpu") == "float32"

    def test_gpu_keeps_auto(self):
        # bfloat16 is native on CUDA/MPS - "auto" lets the checkpoint pick it.
        assert model_loader._load_dtype("cuda") == "auto"
        assert model_loader._load_dtype("mps") == "auto"

    def test_load_model_passes_the_resolved_dtype_to_the_pipeline(self, monkeypatch):
        seen = {}

        def construct(task, **kwargs):
            seen.update(kwargs)
            return object()

        monkeypatch.setattr(model_loader, "_get_hf_pipeline", lambda: construct)
        monkeypatch.setattr(model_loader, "is_model_downloaded", lambda *a, **k: False)
        monkeypatch.setattr(model_loader, "download_model", lambda *a, **k: None)
        monkeypatch.setattr(
            model_loader, "_get_latest_snapshot_path", lambda *a, **k: None
        )
        model_loader._model_cache.clear()

        model_loader.load_model(self.MODEL, self.TASK, device="cpu")

        assert seen["dtype"] == "float32"
        assert seen["model_kwargs"] == {"low_cpu_mem_usage": True}
