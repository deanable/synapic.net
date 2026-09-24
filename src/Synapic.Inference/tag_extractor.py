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


def extract_tags_from_result(
    result: Any,
    model_task: str,
    threshold: float = 0.0,
    stop_words: Optional[List[str]] = None,
    probabilities: Optional[Dict[str, float]] = None,
) -> Tuple[str, List[str], str, Dict[str, float]]:
    """Extract category, keywords, and description from AI model output.

    Handles three main model types:
    1. Image Classification: top-N keyword labels with confidence scores
    2. Zero-Shot Classification: best matching category from user options
    3. Image-to-Text: parses generated captions / JSON-structured metadata

    Returns (category, keywords, description, probabilities), all keywords
    and the category Title Cased.
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
            raw_gen = None
            if isinstance(result, list) and len(result) > 0:
                raw_gen = result[0].get("generated_text", "")
            elif isinstance(result, dict):
                raw_gen = result.get("generated_text", "")

            # CASE 1: Structured Dictionary (from smart VLMs)
            if isinstance(raw_gen, dict):
                description = _payload_field(raw_gen, "description", "")
                category = _sanitize_category(_payload_field(raw_gen, "category", ""))
                keywords = _normalize_keywords(_payload_field(raw_gen, "keywords", []))

            # CASE 2: String (plain caption) or chat format
            else:
                text = ""
                if isinstance(raw_gen, list):
                    for msg in raw_gen:
                        if isinstance(msg, dict) and msg.get("role") == "assistant":
                            content = msg.get("content", "")
                            if isinstance(content, list):
                                for item in content:
                                    if (
                                        isinstance(item, dict)
                                        and item.get("type") == "text"
                                    ):
                                        text += item.get("text", "")
                            elif isinstance(content, str):
                                text += content
                elif isinstance(raw_gen, str):
                    text = raw_gen

                if text:
                    json_extracted = False
                    data = extract_dict_from_text(
                        text,
                        expected_keys=VLM_PAYLOAD_KEYS,
                    )
                    if isinstance(data, dict):
                        description = _payload_field(data, "description", "")
                        category = _sanitize_category(_payload_field(data, "category", ""))
                        keywords = _normalize_keywords(_payload_field(data, "keywords", []))
                        json_extracted = True
                        logger.info(
                            "Successfully extracted structured payload from generated text"
                        )

                    if not json_extracted:
                        logger.warning(
                            "Could not extract JSON from model response, using text as plain description"
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
