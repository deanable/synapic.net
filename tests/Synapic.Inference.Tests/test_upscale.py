"""Contract tests for POST /upscale (the original app's upscaler, ported).

The fast (Lanczos) workflow runs end-to-end offline. The AI workflows never
download weights here: ``Swin2SRUpscaler._load_model`` is monkeypatched with a
deterministic fake so the orchestration around the model — factor mapping,
balanced resize/denoise blending, alpha handling, sharpening, save options —
is exercised without touching the network.
"""

import importlib.util
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


def _has_torch() -> bool:
    """True when torch is importable (the CI test job installs none)."""

    try:
        return importlib.util.find_spec("torch") is not None
    except ImportError:
        return False


def _make_image(path: Path, size=(16, 12), mode="RGB", color=(10, 120, 200)):
    Image.new(mode, size, color if mode != "RGBA" else (10, 120, 200, 200)).save(path)
    return path


class _PurePythonTensor:
    """Minimal tensor stand-in backed by pure Python + PIL so AI workflow
    orchestration tests run in lightweight CI environments without torch or numpy."""

    def __init__(self, shape, fill_value=0.0):
        self._shape = tuple(shape)
        self._fill = float(fill_value)

    @property
    def shape(self):
        return self._shape

    def to(self, *args, **kwargs):
        return self

    def squeeze(self, axis=0):
        new_shape = tuple(d for i, d in enumerate(self._shape) if i != axis or d != 1)
        return _PurePythonTensor(new_shape, self._fill)

    def clamp(self, low, high):
        return _PurePythonTensor(self._shape, max(low, min(high, self._fill)))

    def cpu(self):
        return self

    def mul(self, factor):
        return _PurePythonTensor(self._shape, self._fill * factor)

    def round(self):
        return _PurePythonTensor(self._shape, round(self._fill))

    def permute(self, *axes):
        new_shape = tuple(self._shape[a] for a in axes)
        return _PurePythonTensor(new_shape, self._fill)

    def to_pil(self):
        # HWC shape after permute(1, 2, 0)
        h, w, c = self._shape
        val = max(0, min(255, int(self._fill)))
        if c == 1:
            return Image.new("L", (w, h), val)
        return Image.new("RGB", (w, h), (val, val, val))


class _FakeTorch:
    uint8 = "uint8"
    float16 = "float16"

    @staticmethod
    def device(name):
        return SimpleNamespace(type=str(name))

    class cuda:
        @staticmethod
        def is_available():
            return False

        @staticmethod
        def empty_cache():
            pass

    @staticmethod
    def inference_mode():
        from contextlib import nullcontext

        return nullcontext()

    @staticmethod
    def autocast(*args, **kwargs):
        from contextlib import nullcontext

        return nullcontext()

    @staticmethod
    def zeros(*shape):
        return _PurePythonTensor(shape, 0.0)

    @staticmethod
    def full(shape, fill_value):
        return _PurePythonTensor(shape, fill_value)


class _FakeProcessor:
    """Stand-in for AutoImageProcessor: any image -> a zero pixel_values tensor."""

    def __call__(self, images, return_tensors=None):
        width, height = images.size
        return {"pixel_values": _FakeTorch.zeros(1, 3, height, width)}


class _FakeModel:
    """Stand-in for Swin2SRForImageSuperResolution: uniform gray at `factor`x."""

    def __init__(self, factor):
        self._factor = factor

    def __call__(self, **kwargs):
        pixel_values = kwargs["pixel_values"]
        _, _, height, width = pixel_values.shape
        reconstruction = _FakeTorch.full(
            (1, 3, height * self._factor, width * self._factor), 0.5
        )
        return SimpleNamespace(reconstruction=reconstruction)


@pytest.fixture()
def fake_swin2sr(monkeypatch):
    """Route every AI model load to the deterministic fake (no network or torch required)."""

    def fake_load(self, workflow, factor=None, status_callback=None, device=None):
        if self._torch is None:
            self._torch = _FakeTorch
            self.device = _FakeTorch.device("cpu")
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
        assert Path(second.json()["output_path"]).name == "photo_upscaled_v2.png"

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

    def test_max_dimension_caps_the_input_before_upscaling(self, client, tmp_path):
        """A source larger than max_dimension is capped first: the output is
        (capped input) x factor, while the reported original stays the source size."""
        image = _make_image(tmp_path / "huge.png", size=(64, 32))
        resp = client.post(
            "/upscale",
            json={
                "image_path": str(image),
                "options": {"workflow": "fast", "factor": 2, "max_dimension": 16},
            },
        )
        assert resp.status_code == 200, resp.text
        body = resp.json()
        assert (body["original_width"], body["original_height"]) == (64, 32)
        assert (body["width"], body["height"]) == (32, 16)

    def test_max_dimension_zero_disables_the_cap(self, client, tmp_path):
        image = _make_image(tmp_path / "photo.png", size=(64, 32))
        resp = client.post(
            "/upscale",
            json={
                "image_path": str(image),
                "options": {"workflow": "fast", "factor": 2, "max_dimension": 0},
            },
        )
        assert resp.status_code == 200, resp.text
        assert (resp.json()["width"], resp.json()["height"]) == (128, 64)

    def test_input_within_max_dimension_is_not_resampled(self, client, tmp_path):
        image = _make_image(tmp_path / "photo.png", size=(64, 32))
        resp = client.post(
            "/upscale",
            json={
                "image_path": str(image),
                "options": {"workflow": "fast", "factor": 2, "max_dimension": 2048},
            },
        )
        assert resp.status_code == 200, resp.text
        assert (resp.json()["width"], resp.json()["height"]) == (128, 64)

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

    def test_rgba_to_jpeg_composites_cleanly(self, client, tmp_path):
        image = _make_image(tmp_path / "cutout.png", size=(16, 12), mode="RGBA")
        resp = client.post(
            "/upscale",
            json={
                "image_path": str(image),
                "options": {
                    "workflow": "fast",
                    "factor": 2,
                    "output_format": "JPEG",
                    "sharpen_amount": 0.8,
                },
            },
        )
        assert resp.status_code == 200, resp.text
        output = Path(resp.json()["output_path"])
        assert output.suffix == ".jpg"
        with Image.open(output) as converted:
            assert converted.format == "JPEG"
            assert converted.mode == "RGB"
            assert converted.size == (32, 24)

    def test_explicit_output_path_respects_overwrite_false(self, client, tmp_path):
        image = _make_image(tmp_path / "photo.png", size=(16, 12))
        existing = _make_image(tmp_path / "target.png", size=(4, 4))
        resp = client.post(
            "/upscale",
            json={
                "image_path": str(image),
                "output_path": str(existing),
                "options": {
                    "workflow": "fast",
                    "factor": 2,
                    "overwrite_existing": False,
                },
            },
        )
        assert resp.status_code == 200, resp.text
        body = resp.json()
        assert Path(body["output_path"]).name == "target_v2.png"
        # Existing file must remain 4x4 untouched
        with Image.open(existing) as preserved:
            assert preserved.size == (4, 4)

    def test_in_place_source_overwrite_is_rejected(self, client, tmp_path):
        image = _make_image(tmp_path / "photo.png", size=(16, 12))
        resp = client.post(
            "/upscale",
            json={
                "image_path": str(image),
                "output_path": str(image),
                "options": {"workflow": "fast", "factor": 2},
            },
        )
        assert resp.status_code == 422
        assert "overwrite" in resp.json()["detail"].lower()

    def test_exif_orientation_transposed_and_reset_to_normal(self, client, tmp_path):
        # Create a 20x10 image with EXIF Orientation = 6 (90 CW -> displayed 10x20)
        image = tmp_path / "rotated.jpg"
        raw = Image.new("RGB", (20, 10), (100, 150, 200))
        exif = raw.getexif()
        exif[0x0112] = 6  # Rotate 90 CW
        raw.save(image, format="JPEG", exif=exif.tobytes())

        resp = client.post(
            "/upscale",
            json={
                "image_path": str(image),
                "options": {"workflow": "fast", "factor": 2},
            },
        )
        assert resp.status_code == 200, resp.text
        body = resp.json()
        assert (body["original_width"], body["original_height"]) == (10, 20)
        assert (body["width"], body["height"]) == (20, 40)
        with Image.open(body["output_path"]) as out_img:
            assert out_img.size == (20, 40)
            out_exif = out_img.getexif()
            assert out_exif.get(0x0112, 1) == 1


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

    def test_max_dimension_caps_the_model_input(self, client, tmp_path, fake_swin2sr):
        image = _make_image(tmp_path / "big.png", size=(64, 32))
        resp = client.post(
            "/upscale",
            json={
                "image_path": str(image),
                "options": {"workflow": "quality", "factor": 2, "max_dimension": 16},
            },
        )
        assert resp.status_code == 200, resp.text
        body = resp.json()
        assert (body["original_width"], body["original_height"]) == (64, 32)
        assert (body["width"], body["height"]) == (32, 16)

    def test_balanced_and_quality_agree_on_capped_output_size(
        self, client, tmp_path, fake_swin2sr
    ):
        """Both AI workflows must size their output from the capped model input,
        not from the original source."""
        image = _make_image(tmp_path / "big.png", size=(64, 32))
        resp = client.post(
            "/upscale",
            json={
                "image_path": str(image),
                "options": {"workflow": "balanced", "factor": 2, "max_dimension": 16},
            },
        )
        assert resp.status_code == 200, resp.text
        assert (resp.json()["width"], resp.json()["height"]) == (32, 16)

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

    def test_ai_rgba_to_jpeg_composites_cleanly(self, client, tmp_path, fake_swin2sr):
        image = _make_image(tmp_path / "cutout.png", mode="RGBA", size=(16, 12))
        resp = client.post(
            "/upscale",
            json={
                "image_path": str(image),
                "options": {
                    "workflow": "quality",
                    "factor": 2,
                    "output_format": "JPEG",
                },
            },
        )
        assert resp.status_code == 200, resp.text
        with Image.open(resp.json()["output_path"]) as output:
            assert output.format == "JPEG"
            assert output.mode == "RGB"
            assert output.size == (32, 24)

    def test_non_multiple_of_8_dimensions_cropped_to_exact_factor(
        self, client, tmp_path, monkeypatch
    ):
        """Swin2SR pads non-8-multiple inputs; quality workflow must crop padding back to exact target size."""

        class _PaddedModel:
            def __call__(self, **kwargs):
                pixel_values = kwargs["pixel_values"]
                _, _, height, width = pixel_values.shape
                # Simulate Swin2SR padding input up to next multiple of 8 before 2x upscale
                pad_h = ((height + 7) // 8) * 8
                pad_w = ((width + 7) // 8) * 8
                reconstruction = _FakeTorch.full((1, 3, pad_h * 2, pad_w * 2), 0.5)
                return SimpleNamespace(reconstruction=reconstruction)

        def fake_load_padded(self, wf, factor=None, status_callback=None, device=None):
            if self._torch is None:
                self._torch = _FakeTorch
                self.device = _FakeTorch.device("cpu")
            return _FakeProcessor(), _PaddedModel()

        monkeypatch.setattr(
            upscaler.Swin2SRUpscaler,
            "_load_model",
            fake_load_padded,
        )
        image = _make_image(tmp_path / "odd.png", size=(15, 13))
        resp = client.post(
            "/upscale",
            json={
                "image_path": str(image),
                "options": {"workflow": "quality", "factor": 2},
            },
        )
        assert resp.status_code == 200, resp.text
        body = resp.json()
        assert (body["width"], body["height"]) == (30, 26)


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
        assert instance._next_available_path(target).name == "x_upscaled_v2.png"
