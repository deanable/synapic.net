"""
Image upscaling utilities powered by Swin2SR models from Hugging Face.

Ported verbatim from the original application's ``src/core/upscaler.py`` so
the sidecar serves the same algorithms the desktop app shipped with:

- ``quality``    — classical Swin2SR x2/x4 models
- ``balanced``   — real-world Swin2SR x4 with Lanczos denoise blending
- ``fast``       — plain Lanczos resize, no model

Exposed to the host through ``POST /upscale`` (service.py).
"""

from __future__ import annotations

from dataclasses import dataclass
import os
from pathlib import Path
from threading import Lock
from typing import Any, Callable, Dict, Optional, Tuple
import uuid

from PIL import Image, ImageFilter, ImageOps

StatusCallback = Optional[Callable[[str], None]]

WORKFLOW_QUALITY = "quality"
WORKFLOW_BALANCED = "balanced"
WORKFLOW_FAST = "fast"

_WORKFLOW_MODEL_IDS: Dict[str, Dict[int, str]] = {
    WORKFLOW_QUALITY: {
        2: "caidas/swin2SR-classical-sr-x2-64",
        4: "caidas/swin2SR-classical-sr-x4-64",
    },
    WORKFLOW_BALANCED: {
        4: "caidas/swin2SR-realworld-sr-x4-64-bsrgan-psnr",
    },
}

_FORMAT_TO_EXT = {
    "JPEG": ".jpg",
    "PNG": ".png",
    "WEBP": ".webp",
    "TIFF": ".tif",
    "BMP": ".bmp",
}


@dataclass
class UpscaleOptions:
    workflow: str = WORKFLOW_QUALITY
    precision: str = "auto"  # auto | fp16 | fp32
    denoise_strength: float = 1.0  # Used by balanced workflow (0.0..1.0)
    sharpen_amount: float = 0.0  # 0.0 = off
    max_dimension: int = 2048  # 0 = disable; cap input dim before Swin2SR (model works on 64px patches)
    output_format: str = "keep"  # keep | JPEG | PNG | WEBP
    jpeg_quality: int = 95
    overwrite_existing: bool = True


class Swin2SRUpscaler:
    """Thread-safe wrapper around Swin2SR super-resolution models."""

    def __init__(self) -> None:
        # torch is only needed by the quality/balanced AI workflows. The fast
        # workflow, the workflow/factor validation and the output-path helpers
        # must run without it (the CI test job installs no torch).
        try:
            import torch
        except ImportError:
            torch = None

        self._torch = torch
        self.device = (
            torch.device("cuda" if torch.cuda.is_available() else "cpu")
            if torch is not None
            else None
        )
        self._lock = Lock()
        self._loaded: Dict[Tuple[str, int], Tuple[Any, Any]] = {}

    def _emit(self, callback: StatusCallback, message: str) -> None:
        if callback:
            callback(message)

    def _resolve_device(self, requested_device: Optional[str] = None) -> Any:
        if self._torch is None:
            return None
        req = (requested_device or "").strip().lower()
        if req == "cpu":
            return self._torch.device("cpu")
        if req == "mps" and hasattr(self._torch.backends, "mps") and self._torch.backends.mps.is_available():
            return self._torch.device("mps")
        if req in ("cuda", "auto", ""):
            if self._torch.cuda.is_available():
                return self._torch.device("cuda")
            if req == "cuda":
                return self._torch.device("cpu")
        return self.device

    def _load_model(
        self,
        workflow_or_factor: Any,
        factor: Optional[int] = None,
        status_callback: StatusCallback = None,
        device: Optional[str] = None,
    ) -> Tuple[Any, Any]:
        # Backward compatibility: _load_model(factor, status_callback=...)
        if isinstance(workflow_or_factor, int) and factor is None:
            workflow = WORKFLOW_QUALITY
            factor = workflow_or_factor
        else:
            workflow = str(workflow_or_factor)

        if factor is None:
            raise ValueError("Upscale factor must be provided.")

        workflow_models = _WORKFLOW_MODEL_IDS.get(workflow)
        if workflow_models is None:
            raise ValueError(f"Unsupported upscale workflow: {workflow}")
        if factor not in workflow_models:
            raise ValueError(
                f"Unsupported factor {factor}x for workflow '{workflow}'."
            )

        # Validation above must stay torch-free so a bad request is rejected
        # the same way on machines without torch.
        if self._torch is None:
            raise RuntimeError(
                "PyTorch is required for AI upscale workflows; "
                "only the fast workflow runs without it."
            )

        target_device = self._resolve_device(device)
        device_str = str(target_device) if target_device is not None else "cpu"

        with self._lock:
            cache_key = (workflow, factor, device_str)
            cached = self._loaded.get(cache_key)
            if cached is not None:
                return cached

            # Bound memory residency: evict previously cached SR models before
            # loading a different workflow/factor/device combination.
            if self._loaded:
                self._loaded.clear()
                if self._torch.cuda.is_available():
                    try:
                        self._torch.cuda.empty_cache()
                    except Exception:
                        pass

            model_id = workflow_models[factor]
            self._emit(
                status_callback,
                f"Loading {workflow} super-resolution model ({factor}x): {model_id}",
            )
            from transformers import AutoImageProcessor, Swin2SRForImageSuperResolution

            processor = AutoImageProcessor.from_pretrained(model_id)
            model = Swin2SRForImageSuperResolution.from_pretrained(model_id)
            model.to(target_device)
            model.eval()

            self._loaded[cache_key] = (processor, model)
            return processor, model

    def upscale(
        self,
        input_path: Path,
        factor: int,
        output_path: Optional[Path] = None,
        status_callback: StatusCallback = None,
        options: Optional[UpscaleOptions] = None,
        workflow: Optional[str] = None,
        device: Optional[str] = None,
    ) -> Path:
        """
        Upscale an image using Swin2SR and return the saved output path.
        """
        resolved_options = options or UpscaleOptions()
        if workflow is not None:
            resolved_options.workflow = workflow

        if factor < 2:
            raise ValueError(f"Upscale factor must be >= 2 (got {factor}).")

        if resolved_options.workflow == WORKFLOW_FAST:
            return self._upscale_fast(
                input_path=input_path,
                factor=factor,
                output_path=output_path,
                options=resolved_options,
                status_callback=status_callback,
            )

        model_factor = factor
        if resolved_options.workflow == WORKFLOW_BALANCED and factor == 2:
            # Real-world model is 4x; run 4x then downsample to requested size.
            model_factor = 4

        target_device = self._resolve_device(device)
        try:
            processor, model = self._load_model(
                resolved_options.workflow, model_factor, status_callback, device=device
            )
        except TypeError:
            # Support monkeypatched _load_model in tests with the 3-arg signature
            processor, model = self._load_model(
                resolved_options.workflow, model_factor, status_callback
            )

        # Cap an oversized input before the model: Swin2SR works on 64 px
        # patches, so a source wider (or taller) than max_dimension only spends
        # memory and time on the pre/post-processing without adding detail. The
        # saved output is always the (possibly capped) input size x factor, so
        # an oversized source yields a proportionally smaller output instead of
        # a multi-hundred-megapixel one. max_dimension <= 0 disables the cap.
        with Image.open(input_path) as raw_source:
            source_format = raw_source.format
            source_image = ImageOps.exif_transpose(raw_source)
            icc_profile, exif_bytes = self._extract_metadata(source_image, fallback_source=raw_source)
            source_mode = source_image.mode
            source_image = self._cap_input_size(
                source_image, factor, resolved_options, status_callback
            )
            rgb_image = source_image.convert("RGB")
            alpha_channel = source_image.getchannel("A") if "A" in source_mode else None

        self._emit(
            status_callback,
            f"Running {resolved_options.workflow} AI upscaling ({factor}x)...",
        )
        run_device = target_device or self.device
        model_inputs = processor(images=rgb_image, return_tensors="pt")
        if run_device is not None:
            model_inputs = {
                key: value.to(run_device) for key, value in model_inputs.items()
            }

        with self._torch.inference_mode():
            if (
                run_device is not None
                and resolved_options.precision in {"auto", "fp16"}
                and run_device.type == "cuda"
            ):
                with self._torch.autocast(device_type="cuda", dtype=self._torch.float16):
                    output = model(**model_inputs).reconstruction
            else:
                output = model(**model_inputs).reconstruction

        output = output.squeeze(0).clamp(0, 1).cpu()
        output_image = self._tensor_to_pil(output)

        # Balanced workflow can blend AI output with smoother interpolation for denoise
        # control. The target is the size of the image actually fed to the model
        # (capped when the input was oversized), so the quality and balanced
        # workflows agree on output dimensions.
        target_size = (
            int(rgb_image.width * factor),
            int(rgb_image.height * factor),
        )
        if resolved_options.workflow == WORKFLOW_BALANCED:
            if output_image.size != target_size:
                output_image = output_image.resize(target_size, Image.Resampling.LANCZOS)
            if resolved_options.denoise_strength < 1.0:
                lanczos_ref = rgb_image.resize(target_size, Image.Resampling.LANCZOS)
                alpha = max(0.0, min(1.0, resolved_options.denoise_strength))
                output_image = Image.blend(lanczos_ref, output_image, alpha=alpha)
        elif output_image.size != target_size:
            # Swin2SR pads inputs to a multiple of window_size (8px), which can
            # leave extra border pixels on non-multiple dimensions; crop back to
            # the exact target size.
            output_image = output_image.crop((0, 0, target_size[0], target_size[1]))

        if alpha_channel is not None:
            upscaled_alpha = alpha_channel.resize(
                output_image.size, Image.Resampling.LANCZOS
            )
            output_image = output_image.convert("RGBA")
            output_image.putalpha(upscaled_alpha)

        output_image = self._apply_sharpen(output_image, resolved_options.sharpen_amount)
        resolved_target, save_kwargs, final_fmt = self._resolve_output_target(
            input_path=input_path,
            output_path=output_path,
            source_format=source_format,
            options=resolved_options,
            icc_profile=icc_profile,
            exif_bytes=exif_bytes,
        )

        self._save_image_safely(output_image, resolved_target, final_fmt, save_kwargs)
        return resolved_target

    def _upscale_fast(
        self,
        input_path: Path,
        factor: int,
        output_path: Optional[Path],
        options: UpscaleOptions,
        status_callback: StatusCallback,
    ) -> Path:
        self._emit(status_callback, f"Running fast Lanczos upscale ({factor}x)...")
        with Image.open(input_path) as raw_source:
            source_format = raw_source.format
            source_image = ImageOps.exif_transpose(raw_source)
            icc_profile, exif_bytes = self._extract_metadata(source_image, fallback_source=raw_source)
            source_image = self._cap_input_size(
                source_image, factor, options, status_callback
            )
            new_size = (
                int(source_image.width * factor),
                int(source_image.height * factor),
            )
            if source_image.mode == "P":
                source_image = source_image.convert("RGBA" if "transparency" in source_image.info else "RGB")
            output_image = source_image.resize(new_size, Image.Resampling.LANCZOS)
            output_image = self._apply_sharpen(output_image, options.sharpen_amount)

        resolved_target, save_kwargs, final_fmt = self._resolve_output_target(
            input_path=input_path,
            output_path=output_path,
            source_format=source_format,
            options=options,
            icc_profile=icc_profile,
            exif_bytes=exif_bytes,
        )
        self._save_image_safely(output_image, resolved_target, final_fmt, save_kwargs)
        return resolved_target

    def _cap_input_size(
        self,
        image: Image.Image,
        factor: int,
        options: UpscaleOptions,
        status_callback: StatusCallback,
    ) -> Image.Image:
        """Shrink an oversized input to ``options.max_dimension`` (longest edge).

        Returns the image to upscale from: the same one when the cap is
        disabled (``max_dimension <= 0``) or the source already fits, otherwise
        a LANCZOS-downscaled copy. Because the model upscales whatever it is
        handed, capping the input bounds the model's memory/time cost and the
        saved output size together.
        """
        max_dim = options.max_dimension
        if max_dim <= 0:
            return image

        width, height = image.size
        if max(width, height) <= max_dim:
            return image

        scale = max_dim / max(width, height)
        new_size = (
            max(1, int(round(width * scale))),
            max(1, int(round(height * scale))),
        )
        self._emit(
            status_callback,
            f"Capping {width}x{height} input to {new_size[0]}x{new_size[1]} "
            f"(output {new_size[0] * factor}x{new_size[1] * factor})",
        )
        return image.resize(new_size, Image.Resampling.LANCZOS)

    def _apply_sharpen(self, image: Image.Image, amount: float) -> Image.Image:
        if amount <= 0:
            return image
        percent = int(max(20, min(300, amount * 100)))
        if "A" in image.mode:
            alpha = image.getchannel("A")
            rgb = image.convert("RGB").filter(
                ImageFilter.UnsharpMask(radius=1.6, percent=percent, threshold=2)
            )
            out = rgb.convert("RGBA")
            out.putalpha(alpha)
            return out
        if image.mode not in ("RGB", "L"):
            image = image.convert("RGB")
        return image.filter(
            ImageFilter.UnsharpMask(radius=1.6, percent=percent, threshold=2)
        )

    def _extract_metadata(
        self, image: Image.Image, fallback_source: Optional[Image.Image] = None
    ) -> Tuple[Optional[bytes], Optional[bytes]]:
        icc_profile = image.info.get("icc_profile") or (
            fallback_source.info.get("icc_profile") if fallback_source is not None else None
        )
        exif_bytes = None
        try:
            exif = image.getexif()
            if exif:
                # Reset Orientation to 1 (normal) because pixels are transposed
                if 0x0112 in exif:
                    exif[0x0112] = 1
                exif_bytes = exif.tobytes()
        except Exception:
            pass
        return icc_profile, exif_bytes

    def _prepare_for_save(
        self, image: Image.Image, final_fmt: str
    ) -> Image.Image:
        """Normalize image mode for the target format (e.g., composite RGBA onto
        white for JPEG/BMP which do not support alpha)."""
        if final_fmt in ("JPEG", "BMP"):
            if image.mode in ("RGBA", "LA") or (
                image.mode == "P" and "transparency" in image.info
            ):
                rgba = image.convert("RGBA")
                background = Image.new("RGB", rgba.size, (255, 255, 255))
                background.paste(rgba, mask=rgba.getchannel("A"))
                return background
            if image.mode not in ("RGB", "L"):
                return image.convert("RGB")
        return image

    def _save_image_safely(
        self,
        image: Image.Image,
        output_path: Path,
        final_fmt: str,
        save_kwargs: Dict[str, Any],
    ) -> None:
        prepared = self._prepare_for_save(image, final_fmt)
        output_path.parent.mkdir(parents=True, exist_ok=True)
        staging_path = output_path.with_name(
            f".{output_path.stem}.tmp-{uuid.uuid4().hex[:8]}{output_path.suffix}"
        )
        try:
            if final_fmt:
                prepared.save(staging_path, format=final_fmt, **save_kwargs)
            else:
                prepared.save(staging_path, **save_kwargs)
            os.replace(staging_path, output_path)
        except Exception:
            try:
                if staging_path.exists():
                    staging_path.unlink()
            except OSError:
                pass
            raise

    def _resolve_output_target(
        self,
        input_path: Path,
        output_path: Optional[Path],
        source_format: Optional[str],
        options: UpscaleOptions,
        icc_profile: Optional[bytes] = None,
        exif_bytes: Optional[bytes] = None,
    ) -> Tuple[Path, Dict[str, Any], str]:
        if output_path is None:
            if options.output_format.lower() == "keep":
                ext = input_path.suffix.lower() or _FORMAT_TO_EXT.get(source_format or "", ".png")
            else:
                ext = _FORMAT_TO_EXT.get(options.output_format.upper(), ".png")

            output_path = input_path.with_name(f"{input_path.stem}_upscaled{ext}")
        else:
            try:
                if output_path.resolve() == input_path.resolve():
                    raise ValueError(
                        "Output path must not overwrite the source input file in-place."
                    )
            except OSError:
                pass

        if not options.overwrite_existing and output_path.exists():
            output_path = self._next_available_path(output_path)

        save_kwargs: Dict[str, Any] = {}
        final_fmt = options.output_format.upper()
        if options.output_format.lower() == "keep":
            final_fmt = (source_format or "").upper()
            if not final_fmt:
                ext_lookup = {v: k for k, v in _FORMAT_TO_EXT.items()}
                final_fmt = ext_lookup.get(output_path.suffix.lower(), "PNG")

        if final_fmt == "JPEG":
            save_kwargs["quality"] = max(70, min(100, int(options.jpeg_quality)))
            save_kwargs["optimize"] = True
        elif final_fmt == "WEBP":
            save_kwargs["quality"] = max(70, min(100, int(options.jpeg_quality)))
            save_kwargs["method"] = 6

        if icc_profile and final_fmt in ("JPEG", "PNG", "WEBP", "TIFF"):
            save_kwargs["icc_profile"] = icc_profile
        if exif_bytes and final_fmt in ("JPEG", "PNG", "WEBP", "TIFF"):
            save_kwargs["exif"] = exif_bytes

        return output_path, save_kwargs, final_fmt

    def _next_available_path(self, output_path: Path) -> Path:
        base = output_path.with_suffix("")
        ext = output_path.suffix
        idx = 1
        candidate = output_path
        while candidate.exists():
            candidate = Path(f"{base}_{idx}{ext}")
            idx += 1
        return candidate

    def _tensor_to_pil(self, tensor: Any) -> Image.Image:
        """
        Convert a CHW float tensor in [0, 1] to a PIL image without torchvision.
        """
        scaled = tensor.mul(255).round().to(dtype=self._torch.uint8).permute(1, 2, 0)
        if hasattr(scaled, "to_pil"):
            return scaled.to_pil()
        array = scaled.numpy()
        if array.shape[2] == 1:
            return Image.fromarray(array[:, :, 0], mode="L")
        return Image.fromarray(array)
