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
