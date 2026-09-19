"""Inference execution for all model types.

Port of the per-item inference section of the Python Synapic app's
``src/core/processing.py`` (``_process_single_item`` STAGE 2). Builds the
correct pipeline call for image-text-to-text (VLM chat), image-to-text
(captioning), zero-shot, and plain classification models, then delegates tag
parsing to ``tag_extractor`` and probability scoring to ``model_loader``.

Only the I/O boundary changed: the caller passes an image path (never an
already-opened PIL image, since inference now happens in a separate process).
"""

from __future__ import annotations

import logging
import time
from typing import Any, Dict, List, Optional

import config  # noqa: E402 (flat imports: PyInstaller entry compatibility)
import model_loader  # noqa: E402
from tag_extractor import extract_tags_from_result  # noqa: E402

logger = logging.getLogger(__name__)

DEFAULT_VLM_USER_PROMPT = (
    "Analyze the image and return a JSON object with keys: "
    "'description' (detailed caption), 'category' (single broad category), "
    "and 'keywords' (list of 5-10 tags). Return ONLY the raw JSON string."
)


def run_inference(
    model: Any,
    image_path: str,
    task: str,
    confidence_threshold: float = 0.3,
    probability_mode: str = "llm",
    probability_threshold: float = 0.0,
    candidate_labels: Optional[List[str]] = None,
    system_prompt: str = "",
    max_new_tokens: int = 512,
    embedding_rescue_enabled: bool = False,
) -> Dict[str, Any]:
    """Run inference on one image and return a TagResponse-shaped dict.

    Mirrors the original pipeline: probability scoring (tier ladder) runs
    first when the mode requests it; probability-only tagging derives tags
    from the score map; otherwise the model generates a result that
    ``extract_tags_from_result`` parses into category/keywords/description.
    """
    started = time.monotonic()

    candidates = list(candidate_labels or [])
    provider = "local"

    # ------------------------------------------------------------------
    # Probability scoring pass (tier ladder 2 -> 2.5 -> 0)
    # ------------------------------------------------------------------
    score_result = None
    prob_dict: Dict[str, float] = {}
    if mode != "llm" and candidates:
        try:
            score_result = model_loader.score_keywords(
                model,
                image_path,
                candidates,
                provider=provider,
                task=task,
                mode=probability_mode,
                embedding_rescue_enabled=embedding_rescue_enabled,
            )
            prob_dict = score_result.score_map
            if (
                score_result.tier.value == "unavailable"
                and all(score == 0.0 for score in prob_dict.values())
            ):
                for note in score_result.notes:
                    logger.warning(f"Probability scoring unavailable: {note}")
                score_result = None
                prob_dict = {}
            else:
                for candidate, score in prob_dict.items():
                    passed = probability_threshold <= 0.0 or score >= probability_threshold
                    logger.info(
                        f"  {candidate}: {score:.3f} {'PASS' if passed else 'FAIL'}"
                    )
                if probability_threshold > 0.0:
                    prob_dict = {
                        k: v for k, v in prob_dict.items() if v >= probability_threshold
                    }
                    from .keyword_scoring import build_thresholded_view

                    score_result = build_thresholded_view(score_result, probability_threshold)
        except Exception as exc:
            logger.warning(
                f"Local probability inference failed ({type(exc).__name__}): {exc}"
            )
            score_result = None
            prob_dict = {}

    # ------------------------------------------------------------------
    # Probability-only tagging (no LLM inference)
    # ------------------------------------------------------------------
    probability_only = mode == "probability"

    if probability_only and prob_dict:
        category = max(prob_dict, key=prob_dict.get)
        keywords = list(prob_dict.keys())
        description = ""
        logger.info(f"Probability tagging: category={category}, keywords={keywords}")
        return _finalize(
            category, keywords, description, prob_dict, score_result, model, started
        )

    if probability_only and not prob_dict:
        logger.info(
            "Probability scoring returned no results — falling back to LLM tagging"
        )
        probability_only = False

    # ------------------------------------------------------------------
    # Model inference (mirrors processing.py STAGE 2)
    # ------------------------------------------------------------------
    result: Any = None

    if task in [config.MODEL_TASK_IMAGE_TO_TEXT, config.MODEL_TASK_IMAGE_TEXT_TO_TEXT]:
        from PIL import Image

        with Image.open(image_path) as img:
            if img.mode != "RGB":
                img = img.convert("RGB")

            if getattr(model, "task", "") == config.MODEL_TASK_IMAGE_TEXT_TO_TEXT:
                # Modern image-text-to-text (Qwen2-VL, LFM2.5-VL, ...): chat-style messages
                user_text = DEFAULT_VLM_USER_PROMPT
                if system_prompt:
                    messages = [
                        {"role": "system", "content": system_prompt},
                        {
                            "role": "user",
                            "content": [
                                {"type": "image", "image": img},
                                {"type": "text", "text": user_text},
                            ],
                        },
                    ]
                else:
                    messages = [
                        {
                            "role": "user",
                            "content": [
                                {"type": "image", "image": img},
                                {"type": "text", "text": user_text},
                            ],
                        }
                    ]
                try:
                    result = model(
                        text=messages,
                        generate_kwargs={"max_new_tokens": max_new_tokens},
                    )
                except Exception as e:
                    logger.error(f"VLM inference failed: {e}")
                    raise
            else:
                # Standard image-to-text (BLIP, GIT, ...)
                try:
                    result = model(
                        img,
                        prompt="Describe the image.",
                        generate_kwargs={"max_new_tokens": max_new_tokens},
                    )
                except Exception as e:
                    logger.debug(
                        f"Prompted inference failed ({e}), falling back to simple call."
                    )
                    result = model(img)

    elif task == config.MODEL_TASK_ZERO_SHOT:
        from PIL import Image

        with Image.open(image_path) as img:
            if img.mode != "RGB":
                img = img.convert("RGB")
            labels = candidates or config.DEFAULT_CANDIDATE_LABELS
            result = model(img, candidate_labels=labels)

    else:
        # Standard image classification
        from PIL import Image

        with Image.open(image_path) as img:
            if img.mode != "RGB":
                img = img.convert("RGB")
            result = model(img)

    # ------------------------------------------------------------------
    # Tag extraction (threshold on 0.0-1.0 scale)
    # ------------------------------------------------------------------
    category, keywords, description, probabilities = extract_tags_from_result(
        result, task, threshold=confidence_threshold
    )

    # Merge scored probabilities into the extraction output (scored map wins).
    if prob_dict:
        merged = dict(probabilities or {})
        merged.update(prob_dict)
        probabilities = merged

    # If extraction returned no useful data, write a placeholder so the item
    # is marked as processed (mirrors the original behavior).
    if not category and not keywords and not description:
        description = "[AI: No Result]"
        logger.info(f"No tags extracted for item, using placeholder: {description}")

    return _finalize(category, keywords, description, probabilities, score_result, model, started)


def _finalize(
    category: str,
    keywords: List[str],
    description: str,
    probabilities: Dict[str, float],
    score_result: Any,
    model: Any,
    started: float,
) -> Dict[str, Any]:
    # Cap keywords (tag-spam guard from the original config).
    keywords = keywords[: config.MAX_KEYWORDS_PER_IMAGE]

    response: Dict[str, Any] = {
        "category": category or "",
        "keywords": keywords,
        "description": description or "",
        "probabilities": probabilities or {},
        "inference_ms": int((time.monotonic() - started) * 1000),
        "model_used": getattr(model, "model_id", None) or model_loader.get_state_snapshot().get("model"),
    }
    if score_result is not None:
        response["scoring"] = score_result.to_plain_dict()
    return response
