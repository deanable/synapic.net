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


class TestDeviceSelection:
    """The selected device has to reach the pipeline that /tag builds.

    Regression guard: the host used to keep the Step 2 device in its own config
    and never send it, so ``_session_config["device"]`` stayed at its "cpu" boot
    default and every tag ran on the CPU however the UI was set. The host now
    passes the device at launch (``--device``) and pushes a change with
    ``PUT /config``; both have to reach ``load_model``.
    """

    def test_launch_device_becomes_the_session_device(self, monkeypatch):
        monkeypatch.setitem(service._session_config, "device", "cpu")

        service.apply_launch_device("cuda")

        assert service._session_config["device"] == "cuda"

    def test_device_is_accepted_on_the_command_line(self):
        args = service._build_arg_parser().parse_args(["--port=0", "--device=cuda"])
        assert args.device == "cuda"

    def test_the_host_launches_with_an_environment_variable(self):
        # StartArguments in InferenceSidecarService is only "--port=0": the
        # device travels in the environment, because a sidecar built before the
        # flag existed would refuse to start on an unknown argument (the app and
        # the server are updated independently). Pinned here so the two sides
        # cannot drift apart silently.
        import config

        assert config.DEVICE_ENV_VAR == "SYNAPIC_DEVICE"

    def test_unknown_launch_device_is_ignored(self, monkeypatch):
        monkeypatch.setitem(service._session_config, "device", "cpu")

        # A hand-edited config must not take the server down or reach torch.
        service.apply_launch_device("gpu")  # type: ignore[arg-type]
        service.apply_launch_device(None)

        assert service._session_config["device"] == "cpu"

    def test_tag_builds_the_pipeline_on_the_configured_device(self, client, monkeypatch, tmp_path):
        import inference_engine
        import model_loader
        from PIL import Image

        image = tmp_path / "one.png"
        Image.new("RGB", (16, 16), (1, 2, 3)).save(image)

        seen: list[str] = []

        def fake_load(model_id, task, device="cpu", token=None):
            seen.append(device)
            return ("FAKE_PIPELINE", task)

        monkeypatch.setattr(model_loader, "load_model", fake_load)
        monkeypatch.setattr(
            inference_engine,
            "run_inference",
            lambda *a, **k: {
                "category": None,
                "keywords": [],
                "description": None,
                "probabilities": None,
                "scoring": None,
                "inference_ms": 0,
                "model_used": "fake",
            },
        )
        monkeypatch.setitem(service._session_config, "device", "cpu")

        # Exactly what the host sends: no device field anywhere in the request.
        assert client.post("/tag", json={"image_path": str(image), "options": {}}).status_code == 200
        assert seen == ["cpu"]

        assert client.put("/config", json={"device": "cuda"}).json()["device"] == "cuda"
        assert client.post("/tag", json={"image_path": str(image), "options": {}}).status_code == 200
        assert seen[-1] == "cuda"


class TestPromptDefaults:
    """GET /prompt: what Step 2 loads into the editable tag-instruction box."""

    def test_returns_the_instruction_tag_actually_falls_back_to(self, client):
        import inference_engine

        resp = client.get("/prompt")
        assert resp.status_code == 200
        # Equality with the module constant is the whole point: the host must not
        # keep its own copy of the built-in prompt, or the two drift apart.
        assert resp.json()["default_user_prompt"] == inference_engine.DEFAULT_VLM_USER_PROMPT

    def test_instruction_is_not_empty(self, client):
        assert client.get("/prompt").json()["default_user_prompt"].strip()


class TestTagValidation:
    def test_missing_image_404(self, client):
        resp = client.post("/tag", json={"image_path": "/nonexistent/image.jpg"})
        assert resp.status_code == 404

    def test_blank_path_422(self, client):
        resp = client.post("/tag", json={"image_path": ""})
        assert resp.status_code == 422

    def test_user_prompt_is_an_accepted_option(self, client):
        # Rejected only for the missing image, i.e. the option itself is valid.
        resp = client.post(
            "/tag",
            json={
                "image_path": "/nonexistent/image.jpg",
                "options": {"user_prompt": "Describe this image as JSON."},
            },
        )
        assert resp.status_code == 404

    def test_overlong_user_prompt_422(self, client):
        # A runaway edit must not turn every request into a huge prompt.
        resp = client.post(
            "/tag",
            json={
                "image_path": "/nonexistent/image.jpg",
                "options": {"user_prompt": "x" * 4001},
            },
        )
        assert resp.status_code == 422

    def test_blank_user_prompt_is_accepted_as_the_default(self, client):
        # Blank is how the UI says "built-in instruction", not an error.
        resp = client.post(
            "/tag",
            json={
                "image_path": "/nonexistent/image.jpg",
                "options": {"user_prompt": "   "},
            },
        )
        assert resp.status_code == 404


class TestShutdown:
    def test_shutdown_returns_json(self, client, monkeypatch):
        # The route schedules a hard process exit 0.5s later via
        # service._delayed_exit -> os._exit(0). In-process that thread takes the
        # whole interpreter down mid-run: pytest then exits 0 with no summary
        # while everything after this point silently never runs. Stub the exit
        # here; test_delayed_exit_hard_exits_the_process covers the real effect
        # without killing the test run.
        monkeypatch.setattr(service, "_delayed_exit", lambda: None)

        resp = client.post("/shutdown")

        assert resp.status_code == 200
        assert resp.json()["status"] == "shutting_down"

    def test_delayed_exit_hard_exits_the_process(self, monkeypatch):
        """What the route is actually for: os._exit(0) once the response flushed."""
        exits = []
        monkeypatch.setattr(service.os, "_exit", exits.append)

        service._delayed_exit()

        assert exits == [0]
