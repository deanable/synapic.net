"""Tag extraction from raw AI model output.

Port of the Python Synapic app's ``src/core/image_processing.py`` extraction
section (``extract_tags_from_result`` and helpers). Handles classification,
zero-shot, image-to-text and vision-language model outputs, JSON payload
extraction, confidence filtering, and Title Case normalization.
"""

from __future__ import annotations

import logging
from collections import Counter
from typing import Any, Dict, List, Optional, Tuple

import config  # noqa: E402 (flat imports: PyInstaller entry compatibility)
from json_utils import extract_dict_from_text  # noqa: E402

logger = logging.getLogger(__name__)


def to_title_case(text: str) -> str:
    """Convert text to proper Title Case.

    Handles regular words, compound words ("blue-sky" -> "Blue-Sky"),
    underscores, forward slashes, all-caps ("3D", "AI", "JPEG") and mixed
    case ("iPhone") — identical semantics to the original implementation.
    """

    def capitalize_word(word: str) -> str:
        if not word:
            return word

        if word.isupper():
            return word

        if not word.islower() and not word.isupper():
            if word[0].isupper() and word[1:].islower():
                return word
            return word

        return word[0].upper() + word[1:].lower() if len(word) > 1 else word.upper()

    if not text or not isinstance(text, str):
        return text

    result = []
    for word in text.split():
        if "-" in word:
            parts = word.split("-")
            word = "-".join(capitalize_word(p) for p in parts)
        elif "_" in word:
            parts = word.split("_")
            word = "_".join(capitalize_word(p) for p in parts)
        elif "/" in word:
            parts = word.split("/")
            word = "/".join(capitalize_word(p) for p in parts)
        else:
            word = capitalize_word(word)
        result.append(word)

    return " ".join(result)


def _normalize_keywords(keywords_raw: Any) -> List[str]:
    """Normalize keyword payloads into a flat list of strings."""
    if isinstance(keywords_raw, str):
        return [k.strip() for k in keywords_raw.split(",") if k and k.strip()]

    if isinstance(keywords_raw, list):
        keywords = []
        for item in keywords_raw:
            if isinstance(item, str):
                keywords.extend(
                    [part.strip() for part in item.split(",") if part and part.strip()]
                )
            elif item is not None:
                keywords.append(str(item).strip())
        return [keyword for keyword in keywords if keyword]

    return []


def _sanitize_category(category_raw: Any) -> str:
    """Normalize a category field that may be a string, list, or malformed value."""
    if isinstance(category_raw, str):
        return category_raw.strip()

    if isinstance(category_raw, list):
        if not category_raw:
            return ""

        flat = []
        for item in category_raw:
            if isinstance(item, list):
                flat.extend(str(i).strip() for i in item if i)
            elif item is not None:
                flat.append(str(item).strip())

        flat = [f for f in flat if f]
        if not flat:
            return ""

        if len(set(flat)) == 1:
            return flat[0]

        most_common, count = Counter(flat).most_common(1)[0]
        if count > 1:
            return most_common

        return flat[0]

    if category_raw is not None:
        result = str(category_raw).strip()
        if result and len(result) < 200:
            return result

    return "[AI: No Result]"


# The prompt asks for description/category/keywords, but a model answering
# {"caption": ..., "tags": [...]} is still handing back a usable record. Reading
# only the exact names meant such a payload was either rejected outright (and the
# raw text written into Description) or accepted with its text silently dropped.
VLM_FIELD_ALIASES = {
    "description": ("description", "caption", "summary"),
    "category": ("category", "categories"),
    "keywords": ("keywords", "tags"),
}
# Every name a payload may use - also the vocabulary handed to the JSON hunt, so
# it does not throw a dict away for using a synonym.
VLM_PAYLOAD_KEYS = frozenset(
    name for names in VLM_FIELD_ALIASES.values() for name in names
)


def reply_excerpt(text: str, limit: int = 400) -> str:
    """One bounded log line showing what the model actually said.

    The warning used to name no reply at all, so a batch of failures could not be
    diagnosed from the log: the payload had already been written into the image's
    Description field, and finding it meant searching the catalog for the text and
    guessing which item it belonged to. Line breaks are escaped rather than
    collapsed, because where a model ends a line is exactly what a repair needs to
    know. Shared with ``inference_engine``, which logs and echoes the reply it is
    about to ask the model to fix.
    """
    flat = text.replace("\r", "").replace("\n", "\\n").replace("\t", "\\t").strip()
    return flat if len(flat) <= limit else flat[:limit] + "..."


def _payload_field(payload: dict, field: str, default: Any = None) -> Any:
    """Read ``field`` from a parsed payload, ignoring case and via known aliases."""
    if not isinstance(payload, dict):
        return default

    lowered = {
        key.lower(): value for key, value in payload.items() if isinstance(key, str)
    }
    for name in VLM_FIELD_ALIASES[field]:
        if name in lowered:
            return lowered[name]
    return default


# How well a model's reply reads as the payload the caller asked for. Two
# degrees of malformed matter to the caller: a reply that had to be rewritten
# before it could be read (the model dropped a quote, a comma, or the reply was
# cut off), and one nothing can be read from at all, which falls back to raw
# text. Both are worth one more ask (see inference_engine's retry).
REPLY_CLEAN = "clean"
REPLY_REPAIRED = "repaired"
REPLY_UNREADABLE = "unreadable"

# Higher reads better; the retry is only adopted when it improves on the reply
# already in hand.
_REPLY_RANK = {REPLY_UNREADABLE: 0, REPLY_REPAIRED: 1, REPLY_CLEAN: 2}


def _raw_generated_text(result: Any) -> Any:
    """The ``generated_text`` a pipeline result carries, or None when there is none.

    Covers the three shapes the pipelines produce: a one-element result list, a
    bare dict, and (defensively) anything else.
    """
    if isinstance(result, list) and len(result) > 0:
        first = result[0]
        return first.get("generated_text", "") if isinstance(first, dict) else None
    if isinstance(result, dict):
        return result.get("generated_text", "")
    return None


def vlm_reply_text(result: Any) -> str:
    """The model's reply as text: a chat turn, a plain string, or ``""``.

    Empty means either that there is no text at all or that the pipeline
    already handed the answer over as a parsed dict — the extractor reads that
    shape directly, so there is no text for a caller to re-ask about.
    """
    raw_gen = _raw_generated_text(result)
    if isinstance(raw_gen, str):
        return raw_gen
    if not isinstance(raw_gen, list):
        return ""

    text = ""
    for msg in raw_gen:
        if isinstance(msg, dict) and msg.get("role") == "assistant":
            content = msg.get("content", "")
            if isinstance(content, list):
                for item in content:
                    if isinstance(item, dict) and item.get("type") == "text":
                        text += item.get("text", "")
            elif isinstance(content, str):
                text += content
    return text


def classify_reply(result: Any, model_task: str) -> str:
    """How well ``result`` reads as a payload: clean, repaired or unreadable.

    Runs the same JSON hunt the extractor does, so the caller's decision to ask
    the model again is made on exactly the verdict the tags will be built from —
    and costs no model time (the text of a tag reply is a few hundred bytes).
    """
    if model_task not in (
        config.MODEL_TASK_IMAGE_TO_TEXT,
        config.MODEL_TASK_IMAGE_TEXT_TO_TEXT,
    ):
        return REPLY_CLEAN

    # A pipeline that already parsed the reply into a dict needs no reading at all.
    if isinstance(_raw_generated_text(result), dict):
        return REPLY_CLEAN

    text = vlm_reply_text(result)
    if not text.strip():
        return REPLY_UNREADABLE

    repairs: List[str] = []
    data = extract_dict_from_text(
        text, expected_keys=VLM_PAYLOAD_KEYS, repairs=repairs
    )
    if not isinstance(data, dict):
        return REPLY_UNREADABLE
    return REPLY_REPAIRED if repairs else REPLY_CLEAN


def reply_is_malformed(result: Any, model_task: str) -> bool:
    """True when the reply is not a payload that reads cleanly as it arrived."""
    return classify_reply(result, model_task) != REPLY_CLEAN


def reads_better_than(candidate: Any, current: Any, model_task: str) -> bool:
    """True when ``candidate`` reads strictly better than the reply in hand.

    A retry that repeated the model's mistake (or made it worse) is discarded,
    so the tags are never built from the worse of two replies.
    """
    return _REPLY_RANK[classify_reply(candidate, model_task)] > _REPLY_RANK[
        classify_reply(current, model_task)
    ]


def extract_tags_from_result(
    result: Any,
    model_task: str,
    threshold: float = 0.0,
    stop_words: Optional[List[str]] = None,
    probabilities: Optional[Dict[str, float]] = None,
    repairs: Optional[List[str]] = None,
) -> Tuple[str, List[str], str, Dict[str, float]]:
    """Extract category, keywords, and description from AI model output.

    Handles three main model types:
    1. Image Classification: top-N keyword labels with confidence scores
    2. Zero-Shot Classification: best matching category from user options
    3. Image-to-Text: parses generated captions / JSON-structured metadata

    Returns (category, keywords, description, probabilities), all keywords
    and the category Title Cased.

    When ``repairs`` is given, one short reason is appended per rewrite the JSON
    hunt needed (nothing at all for a reply that parsed as it arrived).
    ``inference_engine`` forwards them to the host, which reports those items as
    repaired rather than clean so a batch the model was mangling is visible.
    """
    category = ""
    keywords: List[str] = []
    description = ""

    if model_task in [
        config.MODEL_TASK_IMAGE_CLASSIFICATION,
        config.MODEL_TASK_ZERO_SHOT,
        config.MODEL_TASK_IMAGE_TO_TEXT,
        config.MODEL_TASK_IMAGE_TEXT_TO_TEXT,
    ]:
        logger.debug(f"Extracting tags - Task: {model_task}, Threshold: {threshold}")
        logger.debug(f"Raw Result: {str(result)[:500]}")

    try:
        if model_task == config.MODEL_TASK_IMAGE_CLASSIFICATION:
            if isinstance(result, list):
                sorted_res = sorted(result, key=lambda x: x["score"], reverse=True)
                for item in sorted_res[:5]:
                    if item["score"] >= threshold:
                        label = item["label"]
                        keywords.extend([k.strip() for k in label.split(",")])
            elif isinstance(result, dict):
                if result["score"] >= threshold:
                    label = result["label"]
                    keywords.extend([k.strip() for k in label.split(",")])

        elif model_task == config.MODEL_TASK_ZERO_SHOT:
            matched_categories: List[str] = []

            if isinstance(result, list):
                sorted_res = sorted(result, key=lambda x: x["score"], reverse=True)
                for item in sorted_res:
                    if isinstance(item, dict) and "label" in item and "score" in item:
                        if item["score"] >= threshold:
                            matched_categories.append(item["label"])
            elif isinstance(result, dict) and "labels" in result and "scores" in result:
                zipped = sorted(
                    zip(result["labels"], result["scores"]),
                    key=lambda x: x[1],
                    reverse=True,
                )
                for label, score in zipped:
                    if score >= threshold:
                        matched_categories.append(label)

            if matched_categories:
                category = matched_categories[0]
                logger.debug(
                    f"Zero-Shot Category: '{category}' (Score: >={threshold})"
                )

        elif model_task in [config.MODEL_TASK_IMAGE_TO_TEXT, config.MODEL_TASK_IMAGE_TEXT_TO_TEXT]:
            raw_gen = _raw_generated_text(result)

            # CASE 1: Structured Dictionary (from smart VLMs)
            if isinstance(raw_gen, dict):
                description = _payload_field(raw_gen, "description", "")
                category = _sanitize_category(_payload_field(raw_gen, "category", ""))
                keywords = _normalize_keywords(_payload_field(raw_gen, "keywords", []))

            # CASE 2: String (plain caption) or chat format
            else:
                text = vlm_reply_text(result)

                if text:
                    json_extracted = False
                    repairs_before = len(repairs) if repairs is not None else 0
                    data = extract_dict_from_text(
                        text,
                        expected_keys=VLM_PAYLOAD_KEYS,
                        repairs=repairs,
                    )
                    if isinstance(data, dict):
                        description = _payload_field(data, "description", "")
                        category = _sanitize_category(_payload_field(data, "category", ""))
                        keywords = _normalize_keywords(_payload_field(data, "keywords", []))
                        json_extracted = True
                        if repairs is not None and len(repairs) > repairs_before:
                            logger.info(
                                "Reply repaired before it could be read: %s",
                                "; ".join(repairs[repairs_before:]),
                            )
                        logger.info(
                            "Successfully extracted structured payload from generated text"
                        )

                    if not json_extracted:
                        logger.warning(
                            "Could not extract JSON from model response, using text as "
                            "plain description: %s",
                            reply_excerpt(text),
                        )
                        description = text.strip()

                        prefixes_to_strip = [
                            "Describe the image.",
                            "Describe this image.",
                            "Caption:",
                            "Description:",
                            "The image shows",
                            "This image shows",
                            "An image of",
                            "A picture of",
                            "generated_text:",
                            "Output:",
                            "Response:",
                            "Analysing the image:",
                            "Analysis:",
                            "Here is the JSON object:",
                            "```json",
                            "```",
                        ]

                        still_stripping = True
                        while still_stripping:
                            original = description
                            for prefix in prefixes_to_strip:
                                if description.lower().startswith(prefix.lower()):
                                    description = description[len(prefix):].strip()

                            if description.lower().startswith("s, "):
                                description = description[3:].strip()
                            elif description.lower().startswith("s "):
                                description = description[2:].strip()

                            description = description.lstrip(":.,- ")

                            if description == original:
                                still_stripping = False

                        description = description.strip()

                        if len(description) <= 1:
                            logger.warning(
                                f"Extracted description too short ('{description}'). Setting to empty."
                            )
                            description = ""

                        if description and not description[0].isupper():
                            description = description[0].upper() + description[1:]

                        if not keywords:
                            keywords = []

        if keywords:
            keywords = list(dict.fromkeys([k for k in keywords if k]))

    except Exception as e:
        logger.error(f"Error extracting tags from result: {e}")

    # Title Case normalization
    if category:
        category = to_title_case(category)

    if keywords:
        keywords = [to_title_case(k) for k in keywords if k]
        keywords = list(dict.fromkeys(keywords))

    logger.debug(
        f"Final tags - Category: '{category}', Keywords: {keywords[:5]}..., "
        f"Description: '{description[:50]}...'"
    )

    return category, keywords, description, probabilities or {}
