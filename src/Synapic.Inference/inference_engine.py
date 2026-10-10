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

import copy
import logging
import time
from typing import Any, Dict, List, Optional

import config  # noqa: E402 (flat imports: PyInstaller entry compatibility)
import model_loader  # noqa: E402
from tag_extractor import (  # noqa: E402
    extract_tags_from_result,
    reads_better_than,
    reply_excerpt,
    reply_is_malformed,
    vlm_reply_text,
)

logger = logging.getLogger(__name__)

# The user turn is the only place the reply format is specified, so it has to
# be explicit. Measured against LFM2.5-VL-450M (greedy, 512 max_new_tokens,
# 13 images - see build/check-tag-prompt.py): the previous one-sentence wording
# produced **zero** strictly valid JSON replies. All 13 arrived wrapped in a
# ```json fence and 3 of them used single-quoted keys, so ``tag_extractor`` had
# to rescue every reply and anything the rescue could not repair was written
# into Description as raw text. The wording below adds the three things that
# fixed it: an explicit ban on fences and on text outside the object, an
# explicit demand for double quotes, and a one-line example of the shape. Same
# model, same images: 13/13 strictly valid JSON, mean reply length down from 78
# to 52 tokens (more headroom under ``max_new_tokens``, so fewer truncated
# payloads to repair).
#
# Keep the key names in sync with ``tag_extractor.VLM_FIELD_ALIASES``, and keep
# the example's values generic: a plausible example gets copied verbatim.
DEFAULT_VLM_USER_PROMPT = (
    "Describe this image and reply with exactly one JSON object, on one "
    "single line - nothing else, no markdown, no code fences, no line breaks "
    "anywhere in the reply, no text before or after it.\n"
    "Use exactly these three keys, spelled exactly like this, and use double "
    "quotes for every key and string value:\n"
    '  "description": one or two sentences describing the image, written on a '
    "single line with no line breaks and no quotation marks inside it;\n"
    '  "category": one broad category as a single short string;\n'
    '  "keywords": an array of 5 to 10 short tags.\n'
    "Keep the whole reply under 120 words.\n"
    'Shape: {"description": "words here", "category": "word", '
    '"keywords": ["tag", "tag"]}'
)


# Sent as the user turn of the one retry a malformed reply gets. It repeats the
# format rules that matter and shows the model what it actually wrote, which is
# what makes the second attempt a rewrite of a known-bad reply rather than a
# fresh guess at the image (a fresh guess loses the first answer's content).
RETRY_INSTRUCTION = (
    "\n\nYour previous reply could not be read as JSON. Reply again with the same "
    "information, as exactly one line of strict JSON: one object, every key and "
    "string in double quotes, no line breaks anywhere, no code fences, no text "
    "before or after it. The reply to fix:\n"
)

# How much of the broken reply is echoed back to the model. It is a few hundred
# bytes in practice; the cap only protects the context from a runaway reply.
RETRY_ECHO_LIMIT = 1500


def _expects_a_json_payload(instruction: str) -> bool:
    """Whether this instruction is asking for a JSON object at all.

    The retry is only for a reply that was *meant* to be JSON. A user who
    replaced the built-in instruction with prose of their own gets a caption,
    which is not a failure to repair — retrying every image of such a run would
    double its cost to fix a problem it does not have.
    """
    if instruction == DEFAULT_VLM_USER_PROMPT:
        return True
    lowered = instruction.lower()
    return "json" in lowered or "{" in instruction


def _ask_vlm(
    model: Any, system_prompt: str, image: Any, instruction: str, max_new_tokens: int
) -> Any:
    """One chat-style VLM call with the given tag instruction."""
    return model(
        text=build_vlm_messages(system_prompt, image, instruction),
        generate_kwargs=_generation_kwargs(model, max_new_tokens),
    )


def build_vlm_messages(
    system_prompt: str, image: Any, user_text: str
) -> List[Dict[str, Any]]:
    """Build the chat messages for one VLM call.

    ``user_text`` is the tag instruction for this call - normally
    ``DEFAULT_VLM_USER_PROMPT``, or whatever the user replaced it with.

    Every turn carries *content parts*, never a bare string - including the
    system turn. transformers 5.1's image-text-to-text ``preprocess`` collects
    the visuals by walking every message's content and reading ``["type"]`` off
    each item, so a string content raises ``TypeError: string indices must be
    integers, not 'str'`` from ``apply_chat_template`` before the model is even
    called. Since a custom system prompt is user-editable in Step 2, that used to
    fail **every** image of the run with HTTP 500 (reproduced on the shipped
    default model; see ``build/check-tag-prompt.py`` variant ``S_with_system``).

    The model's own chat template wants either shape (its ``parse_content``
    helper handles a string or a list), so the list form costs nothing and is
    the only one this pipeline accepts.
    """
    messages: List[Dict[str, Any]] = []
    if system_prompt:
        messages.append(
            {
                "role": "system",
                "content": [{"type": "text", "text": system_prompt}],
            }
        )
    messages.append(
        {
            "role": "user",
            "content": [
                {"type": "image", "image": image},
                {"type": "text", "text": user_text},
            ],
        }
    )
    return messages


def _generation_kwargs(model: Any, max_new_tokens: int) -> Dict[str, Any]:
    """Build ``generate_kwargs`` without transformers' deprecation warnings.

    Passing ``max_new_tokens`` next to the pipeline's own ``generation_config``
    (which many VLM repos ship with a ``max_length`` set — LFM2.5-VL declares
    ``max_length=20``) makes every call log two warnings::

        Passing `generation_config` together with generation-related
        arguments=({'max_new_tokens'}) is deprecated ...
        Both `max_new_tokens` (=512) and `max_length`(=20) seem to have been
        set. `max_new_tokens` will take precedence.

    The pipeline forwards any user-supplied ``generation_config`` as-is, so we
    clone the pipeline's own config, pin ``max_new_tokens`` on it and clear
    ``max_length``. Generation then has a single source of truth and both
    warnings disappear (``max_new_tokens`` still wins, as before).

    ``do_sample`` is pinned off as well. The shipped default model ships no
    sampling flags, so this is already how it runs; pinning it means a future
    model whose ``generation_config`` turns sampling on cannot make tag output
    non-deterministic (and JSON compliance a lottery) without that being a
    deliberate edit here.
    """
    base = getattr(model, "generation_config", None)
    if base is None:
        base = getattr(getattr(model, "model", None), "generation_config", None)
    if base is None:
        # Unknown pipeline shape: keep the explicit kwarg (pre-fix behavior).
        return {"max_new_tokens": int(max_new_tokens), "do_sample": False}

    generation_config = copy.deepcopy(base)
    generation_config.max_new_tokens = int(max_new_tokens)
    generation_config.max_length = None
    generation_config.do_sample = False
    return {"generation_config": generation_config}


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
    user_prompt: str = "",
) -> Dict[str, Any]:
    """Run inference on one image and return a TagResponse-shaped dict.

    Mirrors the original pipeline: probability scoring (tier ladder) runs
    first when the mode requests it; probability-only tagging derives tags
    from the score map; otherwise the model generates a result that
    ``extract_tags_from_result`` parses into category/keywords/description.

    ``user_prompt`` replaces the built-in tag instruction for this call. It is
    user-editable in Step 2, so a blank or whitespace-only value means "use the
    built-in instruction" rather than "send no instruction at all".
    """
    started = time.monotonic()

    candidates = list(candidate_labels or [])
    provider = "local"

    # ------------------------------------------------------------------
    # Probability scoring pass (tier ladder 2 -> 2.5 -> 0)
    # ------------------------------------------------------------------
    score_result = None
    prob_dict: Dict[str, float] = {}
    if probability_mode != "llm" and candidates:
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
                    from keyword_scoring import build_thresholded_view

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
    probability_only = probability_mode == "probability"

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
    reply_retried = False

    if task in [config.MODEL_TASK_IMAGE_TO_TEXT, config.MODEL_TASK_IMAGE_TEXT_TO_TEXT]:
        from PIL import Image

        with Image.open(image_path) as img:
            if img.mode != "RGB":
                img = img.convert("RGB")

            if getattr(model, "task", "") == config.MODEL_TASK_IMAGE_TEXT_TO_TEXT:
                # Modern image-text-to-text (Qwen2-VL, LFM2.5-VL, ...): chat-style messages
                instruction = user_prompt.strip() or DEFAULT_VLM_USER_PROMPT
                if user_prompt.strip():
                    logger.info(
                        f"Using the custom tag instruction from settings ({len(instruction)} chars)"
                    )
                try:
                    result = _ask_vlm(model, system_prompt, img, instruction, max_new_tokens)
                except Exception as e:
                    logger.error(f"VLM inference failed: {e}")
                    raise

                # One more ask before giving up on the format. A small VLM
                # sometimes wrecks its own JSON (a missing quote, a break inside
                # the object, a reply cut off mid-string); asking again with the
                # broken reply in front of it recovers those items instead of
                # writing the raw reply into Description. Only when a payload
                # was asked for, only once, and only when the second reply
                # really reads better than the first - a retry that repeats the
                # same mistake leaves the tags exactly as they would have been.
                if _expects_a_json_payload(instruction) and reply_is_malformed(
                    result, task
                ):
                    broken = vlm_reply_text(result)
                    logger.warning(
                        "Model reply could not be read as JSON - asking once more: %s",
                        reply_excerpt(broken),
                    )
                    # Marked on the decision, not on the outcome: the item really
                    # did need a second ask, whether or not that ask produced
                    # anything.
                    reply_retried = True
                    try:
                        # The model is shown its own reply, verbatim and with its
                        # line breaks intact: it is being asked to re-format a
                        # known answer, not to look at the image again.
                        retry = _ask_vlm(
                            model,
                            system_prompt,
                            img,
                            RETRY_INSTRUCTION + broken[:RETRY_ECHO_LIMIT],
                            max_new_tokens,
                        )
                    except Exception as e:
                        # The retry is a second chance, not a requirement: the
                        # first reply is already in hand, so a retry that fails
                        # to generate must not fail the item.
                        logger.warning(f"The retry of the VLM call failed: {e}")
                    else:
                        if reads_better_than(retry, result, task):
                            logger.info("The second reply reads better - using it")
                            result = retry
                        else:
                            logger.warning(
                                "The second reply did not read any better - keeping the "
                                "first one"
                            )
            else:
                # Standard image-to-text (BLIP, GIT, ...)
                try:
                    result = model(
                        img,
                        prompt="Describe the image.",
                        generate_kwargs=_generation_kwargs(model, max_new_tokens),
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
    # Reasons the JSON hunt had to rewrite the model's reply before it could be
    # read; non-empty marks the item as repaired over the wire, so a batch whose
    # replies were being mangled does not look like a clean one.
    reply_repairs: List[str] = []
    category, keywords, description, probabilities = extract_tags_from_result(
        result, task, threshold=confidence_threshold, repairs=reply_repairs
    )

    # A custom instruction is the user's own wording, and a vague one makes the
    # model drop a field while still returning valid JSON - so the reply parses
    # and the field is silently empty (measured: asking for the three keys but
    # not for "keywords as an array of 5-10 tags" returned no keywords). Say so
    # in the log rather than leaving the user to wonder where their tags went.
    if task == config.MODEL_TASK_IMAGE_TEXT_TO_TEXT and user_prompt.strip():
        missing = [
            name
            for name, value in (
                ("category", category),
                ("keywords", keywords),
                ("description", description),
            )
            if not value
        ]
        if missing:
            logger.warning(
                "The custom tag instruction returned no "
                + "/".join(missing)
                + " - check that it asks for those keys, or clear it to use the "
                "built-in instruction"
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

    return _finalize(
        category,
        keywords,
        description,
        probabilities,
        score_result,
        model,
        started,
        reply_repairs=reply_repairs,
        reply_retried=reply_retried,
    )


def _finalize(
    category: str,
    keywords: List[str],
    description: str,
    probabilities: Dict[str, float],
    score_result: Any,
    model: Any,
    started: float,
    reply_repairs: Optional[List[str]] = None,
    reply_retried: bool = False,
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
        # Empty for a reply that parsed as it arrived; one short reason per
        # rewrite otherwise (the host shows those items as repaired).
        "reply_repairs": list(reply_repairs or []),
        # True when the first reply could not be read as JSON and the model was
        # asked once more (the tags come from whichever reply read better).
        "reply_retried": bool(reply_retried),
    }
    if score_result is not None:
        response["scoring"] = score_result.to_plain_dict()
    return response
