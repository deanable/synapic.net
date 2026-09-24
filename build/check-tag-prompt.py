"""Measure how often each tag-prompt wording gets a parseable reply (manual).

Run this after changing ``inference_engine.DEFAULT_VLM_USER_PROMPT`` or the
message shape: the prompt is what makes the model answer in JSON, and the
wording it replaced produced **zero** strictly valid replies out of 13.

    # Needs the default model in the HF cache and torch + transformers;
    # use the bundled interpreter or any venv with the sidecar's deps.
    ./build/_python/win-x64/python/python.exe build/check-tag-prompt.py

It runs the real pipeline over generated images - exactly the call shape
``inference_engine`` uses, including ``build_vlm_messages``, so it measures the
message shape as well as the wording - and reports per reply:

* ``strict_json``  - the reply parsed with no repair at all
* ``clean``        - that, plus a usable description and keyword list
* ``dumped``       - ``tag_extractor`` gave up and wrote the raw text out
* ``hit_cap``      - generation stopped at ``max_new_tokens`` (so: truncated)

Filter with ``AB_VARIANTS``/``AB_IMAGES`` (comma-separated names) to keep a run
short; a full sweep is 13 images x N variants at ~17s each on CPU. This is not a
CI check - it needs the model weights - which is why the shipped prompt is
guarded in ``tests/Synapic.Inference.Tests/test_tag_prompt.py`` instead.
"""

from __future__ import annotations

import json
import logging
import os
import re
import sys
import time
from pathlib import Path

REPO = Path(__file__).resolve().parent.parent
sys.path.insert(0, str(REPO / "src" / "Synapic.Inference"))

os.environ.setdefault("SYNAPIC_DISABLE_AUTO_DOWNLOAD", "1")
os.environ.setdefault("SYNAPIC_DISABLE_WARMUP", "1")
os.environ.setdefault("HF_HUB_OFFLINE", "1")
os.environ.setdefault(
    "HF_HOME", os.path.join(os.environ["LOCALAPPDATA"], "Synapic", "models")
)

import torch  # noqa: E402

torch.set_num_threads(int(os.environ.get("AB_THREADS", "4")))

from transformers.utils import logging as tf_logging  # noqa: E402

tf_logging.disable_progress_bar()
tf_logging.set_verbosity_error()

import config  # noqa: E402
import model_loader  # noqa: E402
from inference_engine import (  # noqa: E402
    DEFAULT_VLM_USER_PROMPT,
    _generation_kwargs,
    build_vlm_messages,
)
from tag_extractor import extract_tags_from_result  # noqa: E402

MAX_NEW_TOKENS = 512

SPEC = (
    "Describe this image and reply with exactly one JSON object, starting with "
    "an opening brace and ending with a closing brace, and nothing else — no "
    "commentary before or after, no markdown, no code fences.\n"
    "The object has exactly these three keys, spelled exactly like this:\n"
    '  "description": one or two sentences describing the image, written on a '
    "single line with no line breaks and no quotation marks inside it;\n"
    '  "category": one broad category as a single short string;\n'
    '  "keywords": an array of 5 to 10 short tags.\n'
    "Keep the whole reply under 120 words."
)

SPEC_EXAMPLE = (
    SPEC
    + '\nHere is the shape to copy: {"description": "words here", '
    '"category": "word", "keywords": ["tag", "tag"]}'
)

# The prompt this sidecar shipped before the rewrite (kept as the control).
OLD_PROMPT = (
    "Analyze the image and return a JSON object with keys: "
    "'description' (detailed caption), 'category' (single broad category), "
    "and 'keywords' (list of 5-10 tags). Return ONLY the raw JSON string."
)

# (user turn, extra generate kwargs, optional system prompt)
SYSTEM_PROMPT = (
    "You are a tagging assistant for a photographic archive. Prefer British "
    "spelling and keep categories broad."
)

VARIANTS = {
    "A_old": (OLD_PROMPT, {}, ""),
    "S_shipped": (DEFAULT_VLM_USER_PROMPT, {}, ""),
    "S_with_system": (DEFAULT_VLM_USER_PROMPT, {}, SYSTEM_PROMPT),
    "B_spec": (SPEC, {}, ""),
    "C_spec_example": (SPEC_EXAMPLE, {}, ""),
    "D_spec_stop": (SPEC, {"stop_strings": ["}\n"], "tokenizer": True}, ""),
}


def make_images(out_dir: Path) -> list[Path]:
    from PIL import Image, ImageDraw

    out_dir.mkdir(parents=True, exist_ok=True)
    paths: list[Path] = []

    def save(img, name):
        p = out_dir / name
        img.save(p, "JPEG", quality=88)
        paths.append(p)

    # 1. Landscape: sky, sun, hills, lake.
    im = Image.new("RGB", (448, 288), (120, 170, 220))
    d = ImageDraw.Draw(im)
    d.ellipse([320, 30, 380, 90], fill=(255, 226, 140))
    d.polygon([(0, 190), (120, 110), (240, 200)], fill=(76, 110, 72))
    d.polygon([(150, 200), (300, 120), (448, 205)], fill=(58, 92, 60))
    d.rectangle([0, 205, 448, 288], fill=(58, 106, 150))
    save(im, "landscape.jpg")

    # 2. City skyline at dusk.
    im = Image.new("RGB", (448, 288), (60, 70, 120))
    d = ImageDraw.Draw(im)
    for x, h, w in ((20, 150, 60), (95, 210, 70), (180, 120, 55), (250, 180, 80), (345, 140, 70)):
        d.rectangle([x, 288 - h, x + w, 288], fill=(35, 38, 52))
        for wy in range(288 - h + 12, 270, 26):
            for wx in range(x + 8, x + w - 10, 18):
                d.rectangle([wx, wy, wx + 8, wy + 10], fill=(250, 222, 130))
    save(im, "skyline.jpg")

    # 3. Food-ish: table with plates and cups.
    im = Image.new("RGB", (448, 288), (196, 176, 146))
    d = ImageDraw.Draw(im)
    d.ellipse([50, 150, 210, 250], fill=(238, 236, 228), outline=(180, 178, 170), width=3)
    d.ellipse([95, 178, 170, 226], fill=(196, 122, 62))
    d.ellipse([230, 60, 320, 150], fill=(240, 238, 230), outline=(180, 178, 170), width=3)
    d.rectangle([350, 150, 400, 250], fill=(232, 230, 220))
    for i in range(6):
        d.ellipse([370, 165 + i * 2, 398, 175 + i * 2], fill=(120, 80, 40))
    save(im, "tabletop.jpg")

    # 4. Portrait-ish: person against a plain wall.
    im = Image.new("RGB", (448, 288), (208, 200, 188))
    d = ImageDraw.Draw(im)
    d.ellipse([190, 40, 258, 108], fill=(224, 182, 148))
    d.polygon([(160, 288), (224, 120), (288, 288)], fill=(70, 92, 130))
    save(im, "portrait.jpg")

    # 5. Document/whiteboard: lines of text-like strokes.
    im = Image.new("RGB", (448, 288), (250, 250, 246))
    d = ImageDraw.Draw(im)
    y = 30
    for i in range(11):
        x = 30
        while x < 400:
            w = 14 + (i * 7 + x) % 40
            d.rectangle([x, y, min(x + w, 400), y + 8], fill=(52, 52, 58))
            x += w + 12
        y += 22
    save(im, "document.jpg")

    # 7. Beach with umbrella.
    im = Image.new("RGB", (448, 288), (150, 200, 235))
    d = ImageDraw.Draw(im)
    d.rectangle([0, 180, 448, 288], fill=(228, 208, 160))
    d.rectangle([0, 170, 448, 190], fill=(90, 150, 200))
    d.polygon([(300, 110), (380, 110), (340, 250)], fill=(210, 90, 80))
    d.line([(340, 110), (340, 250)], fill=(120, 120, 120), width=4)
    save(im, "beach.jpg")

    # 8. Desk with laptop and cup.
    im = Image.new("RGB", (448, 288), (120, 96, 78))
    d = ImageDraw.Draw(im)
    d.polygon([(80, 110), (300, 110), (330, 200), (50, 200)], fill=(60, 62, 70))
    d.polygon([(60, 118), (290, 118), (315, 190), (40, 190)], fill=(28, 30, 36))
    d.rectangle([350, 140, 410, 200], fill=(238, 238, 232))
    d.arc([345, 160, 420, 200], 300, 60, fill=(238, 238, 232), width=8)
    save(im, "desk.jpg")

    # 9. Flower close-up on a stem.
    im = Image.new("RGB", (448, 288), (86, 128, 72))
    d = ImageDraw.Draw(im)
    d.line([(224, 288), (224, 120)], fill=(58, 100, 54), width=6)
    for ang in range(0, 360, 45):
        import math

        cx = 224 + 55 * math.cos(math.radians(ang))
        cy = 110 + 55 * math.sin(math.radians(ang))
        d.ellipse([cx - 45, cy - 30, cx + 45, cy + 30], fill=(236, 196, 90))
    d.ellipse([194, 80, 254, 140], fill=(180, 70, 60))
    save(im, "flower.jpg")

    # 10. Two people side by side.
    im = Image.new("RGB", (448, 288), (196, 200, 196))
    d = ImageDraw.Draw(im)
    for cx, colour in ((160, (70, 92, 130)), (290, (130, 70, 80))):
        d.ellipse([cx - 34, 60, cx + 34, 128], fill=(224, 182, 148))
        d.polygon([(cx - 66, 288), (cx, 132), (cx + 66, 288)], fill=colour)
    save(im, "people.jpg")

    # 11. Bar chart.
    im = Image.new("RGB", (448, 288), (252, 252, 250))
    d = ImageDraw.Draw(im)
    d.line([(50, 40), (50, 240)], fill=(60, 60, 60), width=3)
    d.line([(50, 240), (400, 240)], fill=(60, 60, 60), width=3)
    for i, h in enumerate((80, 130, 60, 170, 110)):
        x = 70 + i * 62
        d.rectangle([x, 240 - h, x + 44, 240], fill=(70, 120, 190))
    save(im, "chart.jpg")

    # 12. Night sky with stars and moon.
    im = Image.new("RGB", (448, 288), (18, 20, 46))
    d = ImageDraw.Draw(im)
    d.ellipse([330, 30, 400, 100], fill=(238, 238, 210))
    import random

    random.seed(7)
    for _ in range(120):
        x, y = random.randint(0, 447), random.randint(0, 200)
        d.point((x, y), fill=(255, 255, 240))
    d.polygon([(0, 240), (140, 205), (300, 250), (448, 215), (448, 288), (0, 288)], fill=(12, 14, 30))
    save(im, "night.jpg")

    # 6. Forest path.
    im = Image.new("RGB", (448, 288), (86, 112, 78))
    d = ImageDraw.Draw(im)
    d.polygon([(150, 288), (224, 150), (300, 288)], fill=(150, 128, 96))
    for x, top in ((40, 60), (110, 30), (200, 20), (300, 40), (390, 70)):
        d.polygon([(x - 45, 210), (x, top), (x + 45, 210)], fill=(42, 74, 48))
        d.polygon([(x - 38, 150), (x, top + 25), (x + 38, 150)], fill=(52, 88, 56))
    save(im, "forest.jpg")

    return paths


from typing import Any  # noqa: E402


class WarningCatcher(logging.Handler):
    def __init__(self) -> None:
        super().__init__(level=logging.WARNING)
        self.dumped = False

    def emit(self, record: logging.LogRecord) -> None:
        if "Could not extract JSON" in record.getMessage():
            self.dumped = True


def assistant_text(result: Any) -> str:
    """Pull the assistant reply out of the pipeline result.

    Mirrors ``tag_extractor.extract_tags_from_result``: this pipeline returns
    ``generated_text`` as a list of chat messages, not a string.
    """
    raw = (result[0] or {}).get("generated_text", "") if result else ""
    if isinstance(raw, str):
        return raw
    if isinstance(raw, list):
        text = ""
        for msg in raw:
            if isinstance(msg, dict) and msg.get("role") == "assistant":
                content = msg.get("content", "")
                if isinstance(content, list):
                    for item in content:
                        if isinstance(item, dict) and item.get("type") == "text":
                            text += item.get("text", "")
                elif isinstance(content, str):
                    text += content
        return text
    return str(raw)


def invalid_json_kind(raw: str) -> str:
    """Why a reply is not strictly valid JSON ('' when it is)."""
    try:
        json.loads(raw)
        return ""
    except Exception as exc:
        if "```" in raw:
            return "fence"
        if re.search(r"'[A-Za-z_]+'\s*:", raw):
            return "single_quotes"
        if not raw.lstrip().startswith(("{", "[")):
            return "prose"
        msg = str(exc)
        if "Unterminated string" in msg or "Expecting ',' delimiter" in msg:
            return "newline_or_truncation"
        if "Expecting value" in msg:
            return "truncation"
        return "other"


def main() -> int:
    import logging as _logging

    _logging.basicConfig(level=logging.INFO, format="%(levelname)s %(message)s")

    images = make_images(Path(__file__).resolve().parent / "_prompt-ab-images")
    images.append(REPO / "tests" / "Synapic.Avalonia.Tests" / "TestData" / "sample.jpg")
    if os.environ.get("AB_IMAGES"):
        wanted = os.environ["AB_IMAGES"].split(",")
        images = [p for p in images if p.stem in wanted]

    print(f"loading {config.DEFAULT_MODEL_ID} ...", flush=True)
    t0 = time.monotonic()
    pipe, task = model_loader.load_model(
        config.DEFAULT_MODEL_ID, config.MODEL_TASK_IMAGE_TEXT_TO_TEXT, "cpu"
    )
    print(f"loaded in {time.monotonic() - t0:.1f}s task={task}\n", flush=True)

    from PIL import Image as PILImage

    # NB: on the image-text-to-text pipeline ``pipe.tokenizer`` is a method,
    # not the tokenizer object - the real one lives on the processor.
    tokenizer = getattr(getattr(pipe, "processor", None), "tokenizer", None)
    assert tokenizer is not None and hasattr(tokenizer, "encode"), "no tokenizer"

    variants = VARIANTS
    if os.environ.get("AB_VARIANTS"):
        wanted_v = os.environ["AB_VARIANTS"].split(",")
        variants = {k: v for k, v in VARIANTS.items() if k in wanted_v}

    rows = []
    for name, (prompt, extra, system) in variants.items():
        for path in images:
            with PILImage.open(path) as img:
                if img.mode != "RGB":
                    img = img.convert("RGB")
                # Use the shipped builder, so the harness measures the exact
                # message shape (system turn included) the sidecar sends.
                messages = build_vlm_messages(system, img, prompt)
                kwargs = _generation_kwargs(pipe, MAX_NEW_TOKENS)
                gc = kwargs.get("generation_config")
                for key, value in extra.items():
                    if key == "tokenizer":
                        kwargs["tokenizer"] = tokenizer
                    elif gc is not None:
                        setattr(gc, key, value)
                    else:
                        kwargs[key] = value
                started = time.monotonic()
                try:
                    result = pipe(text=messages, generate_kwargs=kwargs)
                    error = ""
                except Exception as exc:  # noqa: BLE001
                    result = [{"generated_text": ""}]
                    error = f"{type(exc).__name__}: {exc}"
                elapsed = time.monotonic() - started

            raw = assistant_text(result)
            n_tok = len(tokenizer.encode(raw))

            catcher = WarningCatcher()
            logging.getLogger("tag_extractor").addHandler(catcher)
            try:
                category, keywords, description, _ = extract_tags_from_result(
                    result, task, threshold=0.3
                )
            finally:
                logging.getLogger("tag_extractor").removeHandler(catcher)

            rows.append(
                {
                    "variant": name,
                    "image": path.name,
                    "error": error,
                    "chars": len(raw),
                    "tokens": n_tok,
                    "hit_cap": n_tok >= MAX_NEW_TOKENS - 2,
                    "strict_json": not invalid_json_kind(raw),
                    "invalid": invalid_json_kind(raw),
                    "dumped": catcher.dumped,
                    # Usable record with no rescue at all: strict JSON that the
                    # extractor turned into a real description + keywords.
                    "clean": bool(
                        not invalid_json_kind(raw)
                        and keywords
                        and description
                        and not description.lstrip().startswith(("{", "["))
                    ),
                    "seconds": round(elapsed, 1),
                    "description_head": description[:70].replace("\n", "\\n"),
                    "keywords": len(keywords),
                    "raw_head": raw[:90].replace("\n", "\\n"),
                }
            )
            r = rows[-1]
            print(
                f"{name:16} {path.name:14} tok={r['tokens']:4} cap={int(r['hit_cap'])} "
                f"json={int(r['strict_json'])} invalid={r['invalid'] or '-':22} "
                f"dump={int(r['dumped'])} kw={r['keywords']:2} {r['seconds']:5.1f}s"
                + (f"  ERROR {error}" if error else ""),
                flush=True,
            )
            print(f"                 raw: {r['raw_head']}", flush=True)

    print("\n=== SUMMARY ===")
    for name in variants:
        sub = [r for r in rows if r["variant"] == name]
        if not sub:
            continue
        print(
            f"{name:16} strict_json={sum(r['strict_json'] for r in sub)}/{len(sub)}  "
            f"clean={sum(r['clean'] for r in sub)}/{len(sub)}  "
            f"dumped={sum(r['dumped'] for r in sub)}/{len(sub)}  "
            f"hit_cap={sum(r['hit_cap'] for r in sub)}/{len(sub)}  "
            f"mean_tokens={sum(r['tokens'] for r in sub) / len(sub):.0f}  "
            f"errors={sum(1 for r in sub if r['error'])}"
        )

    out = Path(__file__).resolve().parent / "_prompt-ab-results.json"
    out.write_text(json.dumps(rows, indent=2), encoding="utf-8")
    print(f"\nfull rows -> {out}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
