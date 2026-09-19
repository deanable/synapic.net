"""Contract tests for the FastAPI sidecar service (spec §4.2 API surface).

Runs the app in-process with FastAPI's TestClient — no torch download needed
(/tag is exercised only for validation errors; model loading paths are mocked).
"""

import sys
from pathlib import Path

import pytest

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / "src" / "Synapic.Inference"))

from fastapi.testclient import TestClient  # noqa: E402

import service  # noqa: E402
import model_loader  # noqa: E402


@pytest.fixture()
def client():
    with TestClient(service.app) as c:
        yield c


class TestHealth:
    def test_health_shape(self, client):
        resp = client.get("/health")
        assert resp.status_code == 200
        body = resp.json()
        assert set(body.keys()) == {"status", "model", "device", "vram_used_mb", "error"}
        assert body["status"] in {"loading", "ready", "error"}

    def test_health_includes_download_when_active(self, client):
        model_loader.mark_download_started("LiquidAI/LFM2.5-VL-450M", expected_total=1000)
        try:
            body = client.get("/health").json()
            download = body["download"]
            assert download["model_id"] == "LiquidAI/LFM2.5-VL-450M"
            assert download["status"] == "downloading"
            assert download["done_bytes"] == 0
            assert download["total_bytes"] == 1000
        finally:
            model_loader.reset_download_state()

    def test_default_session_model_is_lfm25_450m(self, client):
        body = client.get("/config").json()
        assert body["model_id"] == "LiquidAI/LFM2.5-VL-450M"


class TestModels:
    def test_models_list_returns_array(self, client):
        resp = client.get("/models/list")
        assert resp.status_code == 200
        assert isinstance(resp.json(), list)

    def test_download_rejects_incompatible_model(self, client):
        resp = client.post("/models/download", json={"model_id": "some-gptq-model", "revision": "main"})
        assert resp.status_code == 422
        assert "not supported" in resp.json()["detail"].lower()

    def test_download_accepts_compatible_model(self, client):
        resp = client.post("/models/download", json={"model_id": "prajjwal1/bert-tiny", "revision": "main"})
        assert resp.status_code == 200
        assert resp.json()["status"] in {"download_started", "already_downloading", "downloaded"}


class TestConfig:
    def test_get_config(self, client):
        resp = client.get("/config")
        assert resp.status_code == 200
        body = resp.json()
        for key in ("model_id", "task", "device", "confidence_threshold", "probability_mode", "probability_threshold"):
            assert key in body

    def test_put_config_updates_values(self, client):
        resp = client.put("/config", json={"device": "cpu", "confidence_threshold": 0.45})
        assert resp.status_code == 200
        body = resp.json()
        assert body["confidence_threshold"] == 0.45

    def test_put_config_rejects_bad_task(self, client):
        resp = client.put("/config", json={"task": "not-a-task"})
        assert resp.status_code == 422


class TestTagValidation:
    def test_missing_image_404(self, client):
        resp = client.post("/tag", json={"image_path": "/nonexistent/image.jpg"})
        assert resp.status_code == 404

    def test_blank_path_422(self, client):
        resp = client.post("/tag", json={"image_path": ""})
        assert resp.status_code == 422


class TestShutdown:
    def test_shutdown_returns_json(self, client):
        resp = client.post("/shutdown")
        assert resp.status_code == 200
        assert resp.json()["status"] == "shutting_down"
