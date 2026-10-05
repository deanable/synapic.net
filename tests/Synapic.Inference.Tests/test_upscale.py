"""Contract tests for POST /upscale (the original app's upscaler, ported).

The fast (Lanczos) workflow runs end-to-end offline. The AI workflows never
download weights here: ``Swin2SRUpscaler._load_model`` is monkeypatched with a
deterministic fake so the orchestration around the model — factor mapping,
balanced resize/denoise blending, alpha handling, sharpening, save options —
is exercised without touching the network.
"""

import sys
from pathlib import Path
from types import SimpleNamespace

import pytest

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / "src" / "Synapic.Inference"))

from fastapi.testclient import TestClient  # noqa: E402
from PIL import Image  # noqa: E402

import service  # noqa: E402
import upscaler  # noqa: E402


@pytest.fixture()
def client():
    with TestClient(service.app) as c:
        yield c


def _make_image(path: Path, size=(16, 12), mode="RGB", color=(10, 120, 200)):
    Image.new(mode, size, color if mode != "RGBA" else (10, 120, 200, 200)).save(path)
    return path


class _FakeProcessor:
    """Stand-in for AutoImageProcessor: any image -> a zero pixel_values tensor."""

    def __call__(self, images, return_tensors=None):
        import torch

        width, height = images.size
        return {"pixel_values": torch.zeros(1, 3, height, width)}


class _FakeModel:
    """Stand-in for Swin2SRForImageSuperResolution: uniform gray at `factor`x."""

    def __init__(self, factor):
        self._factor = factor

    def __call__(self, **kwargs):
        import torch

        pixel_values = kwargs["pixel_values"]
        _, _, height, width = pixel_values.shape
        reconstruction = torch.full(
            (1, 3, height * self._factor, width * self._factor), 0.5
        )
        return SimpleNamespace(reconstruction=reconstruction)


@pytest.fixture()
def fake_swin2sr(monkeypatch):
    """Route every AI model load to the deterministic fake (no network)."""

    def fake_load(self, workflow, factor=None, status_callback=None):
        return _FakeProcessor(), _FakeModel(factor)

    monkeypatch.setattr(upscaler.Swin2SRUpscaler, "_load_model", fake_load)


class TestUpscaleValidation:
    def test_blank_image_path_rejected(self, client):
        resp = client.post("/upscale", json={"image_path": ""})
        assert resp.status_code == 422

    def test_missing_file_returns_404(self, client, tmp_path):
        resp = client.post("/upscale", json={"image_path": str(tmp_path / "nope.png")})
        assert resp.status_code == 404
        assert "not found" in resp.json()["detail"].lower()

    def test_non_image_file_rejected(self, client, tmp_path):
        junk = tmp_path / "junk.png"
        junk.write_text("definitely not an image")
        resp = client.post("/upscale", json={"image_path": str(junk)})
        assert resp.status_code == 422

    def test_unsupported_workflow_rejected(self, client, tmp_path):
        image = _make_image(tmp_path / "a.png")
        resp = client.post(
            "/upscale", json={"image_path": str(image), "options": {"workflow": "magic"}}
        )
        assert resp.status_code == 422

    def test_unsupported_factor_for_quality_rejected(self, client, tmp_path):
        image = _make_image(tmp_path / "a.png")
        resp = client.post(
            "/upscale",
            json={
                "image_path": str(image),
                "options": {"workflow": "quality", "factor": 3},
            },
        )
        assert resp.status_code == 422
        assert "3x" in resp.json()["detail"]

    def test_out_of_range_denoise_rejected(self, client, tmp_path):
        image = _make_image(tmp_path / "a.png")
        resp = client.post(
            "/upscale",
            json={"image_path": str(image), "options": {"denoise_strength": 4.0}},
        )
        assert resp.status_code == 422


class TestUpscaleFastWorkflow:
    def test_fast_doubles_dimensions(self, client, tmp_path):
        image = _make_image(tmp_path / "photo.png", size=(16, 12))
        resp = client.post(
            "/upscale",
            json={
                "image_path": str(image),
                "options": {"workflow": "fast", "factor": 2},
            },
        )
        assert resp.status_code == 200, resp.text
        body = resp.json()
        assert body["workflow"] == "fast"
        assert body["factor"] == 2
        assert body["model_used"] is None
        assert (body["original_width"], body["original_height"]) == (16, 12)
        assert (body["width"], body["height"]) == (32, 24)
        assert body["inference_ms"] >= 0

        output = Path(body["output_path"])
        assert output.name == "photo_upscaled.png"
        assert output.parent == tmp_path
        assert output.is_file() and output.stat().st_size > 0
        # The original is untouched.
        with Image.open(image) as original:
            assert original.size == (16, 12)

    def test_fast_output_format_jpeg(self, client, tmp_path):
        image = _make_image(tmp_path / "photo.png")
        resp = client.post(
            "/upscale",
            json={
                "image_path": str(image),
                "options": {"workflow": "fast", "factor": 2, "output_format": "JPEG"},
            },
        )
        assert resp.status_code == 200, resp.text
        output = Path(resp.json()["output_path"])
        assert output.suffix == ".jpg"
        with Image.open(output) as converted:
            assert converted.format == "JPEG"

    def test_overwrite_false_picks_next_free_name(self, client, tmp_path):
        image = _make_image(tmp_path / "photo.png")
        payload = {
            "image_path": str(image),
            "options": {"workflow": "fast", "factor": 2, "overwrite_existing": False},
        }
        first = client.post("/upscale", json=payload)
        assert first.status_code == 200
        second = client.post("/upscale", json=payload)
        assert second.status_code == 200
        assert Path(second.json()["output_path"]).name == "photo_upscaled_1.png"

    def test_explicit_output_path_honored(self, client, tmp_path):
        image = _make_image(tmp_path / "photo.png")
        target = tmp_path / "elsewhere" / "out.png"
        target.parent.mkdir()
        resp = client.post(
            "/upscale",
            json={
                "image_path": str(image),
                "output_path": str(target),
                "options": {"workflow": "fast", "factor": 4},
            },
        )
        assert resp.status_code == 200, resp.text
        assert Path(resp.json()["output_path"]) == target
        assert target.is_file()

    def test_sharpen_amount_applies_without_error(self, client, tmp_path):
        image = _make_image(tmp_path / "photo.png")
        resp = client.post(
            "/upscale",
            json={
                "image_path": str(image),
                "options": {"workflow": "fast", "factor": 2, "sharpen_amount": 1.5},
            },
        )
        assert resp.status_code == 200, resp.text


class TestUpscaleAiWorkflows:
    def test_quality_x2_uses_classical_model(self, client, tmp_path, fake_swin2sr):
        image = _make_image(tmp_path / "photo.png", size=(16, 12))
        resp = client.post(
            "/upscale",
            json={
                "image_path": str(image),
                "options": {"workflow": "quality", "factor": 2},
            },
        )
        assert resp.status_code == 200, resp.text
        body = resp.json()
        assert body["model_used"] == "caidas/swin2SR-classical-sr-x2-64"
        assert (body["width"], body["height"]) == (32, 24)

    def test_balanced_x2_runs_4x_model_then_downsamples(self, client, tmp_path, fake_swin2sr):
        image = _make_image(tmp_path / "photo.png", size=(16, 12))
        resp = client.post(
            "/upscale",
            json={
                "image_path": str(image),
                "options": {"workflow": "balanced", "factor": 2},
            },
        )
        assert resp.status_code == 200, resp.text
        body = resp.json()
        assert body["model_used"] == "caidas/swin2SR-realworld-sr-x4-64-bsrgan-psnr"
        # Requested 2x, not the model's native 4x.
        assert (body["width"], body["height"]) == (32, 24)

    def test_balanced_zero_denoise_is_pure_lanczos(self, client, tmp_path, fake_swin2sr):
        """denoise_strength=0 blends fully to the Lanczos reference (alpha=0)."""
        image = _make_image(tmp_path / "photo.png", size=(16, 12))
        resp = client.post(
            "/upscale",
            json={
                "image_path": str(image),
                "options": {
                    "workflow": "balanced",
                    "factor": 2,
                    "denoise_strength": 0.0,
                },
            },
        )
        assert resp.status_code == 200, resp.text

        with Image.open(image) as source:
            expected = source.convert("RGB").resize((32, 24), Image.Resampling.LANCZOS)
        with Image.open(resp.json()["output_path"]) as actual:
            assert actual.convert("RGB").tobytes() == expected.tobytes()

    def test_alpha_channel_survives(self, client, tmp_path, fake_swin2sr):
        image = _make_image(tmp_path / "cutout.png", mode="RGBA", size=(16, 12))
        resp = client.post(
            "/upscale",
            json={
                "image_path": str(image),
                "options": {"workflow": "quality", "factor": 2},
            },
        )
        assert resp.status_code == 200, resp.text
        with Image.open(resp.json()["output_path"]) as output:
            assert output.mode == "RGBA"
            assert output.size == (32, 24)


class TestSwin2SRUpscalerInternals:
    def test_load_model_rejects_unknown_workflow(self, tmp_path):
        instance = upscaler.Swin2SRUpscaler()
        with pytest.raises(ValueError, match="Unsupported upscale workflow"):
            instance._load_model("nope", 2)

    def test_load_model_rejects_missing_factor(self):
        instance = upscaler.Swin2SRUpscaler()
        with pytest.raises(ValueError, match="factor must be provided"):
            instance._load_model("quality", None)

    def test_next_available_path_counts_up(self, tmp_path):
        instance = upscaler.Swin2SRUpscaler()
        target = tmp_path / "x_upscaled.png"
        assert instance._next_available_path(target) == target
        target.write_bytes(b"x")
        assert instance._next_available_path(target).name == "x_upscaled_1.png"
