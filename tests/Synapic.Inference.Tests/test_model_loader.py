"""Tests for model_loader: cache heuristics, compatibility filters, task
suggestion, fuzzy matching — no network or torch downloads required.
"""

import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / "src" / "Synapic.Inference"))

from synapic_inference import model_loader  # noqa: E402
from synapic_inference.config import (  # noqa: E402
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
