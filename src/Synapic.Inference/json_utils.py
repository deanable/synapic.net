"""Safe literal/JSON parsing helpers for model responses.

Port of the Python Synapic app's ``src/utils/json_utils.py``: extracts the
first useful dict payload embedded in free-form LLM/VLM text. Handles fenced
code blocks, balanced-brace scanning, and truncated-JSON repair.
"""

from __future__ import annotations

import ast
import json
import logging
from typing import Any, Iterable, Optional

logger = logging.getLogger(__name__)


def safe_parse_python_literal(
    text: str, max_depth: int = 100, max_length: int = 100000
) -> Any:
    """Safely parse a string that might be JSON or a Python literal.

    Layered strategy: reject oversized input, reject excessive nesting depth,
    try JSON first, fall back to ast.literal_eval.
    """
    if not text:
        return None

    if not isinstance(text, str):
        return text

    if len(text) > max_length:
        logger.warning(
            f"Rejected parsing of string: length {len(text)} exceeds limit of {max_length}"
        )
        raise ValueError(f"Input length exceeds limit of {max_length}")

    if not _check_nesting_depth(text, max_depth):
        logger.warning(
            f"Rejected parsing of string: nesting depth exceeds limit of {max_depth}"
        )
        raise ValueError(f"Nesting depth exceeds limit of {max_depth}")

    try:
        return json.loads(text)
    except json.JSONDecodeError:
        pass

    try:
        return ast.literal_eval(text)
    except (SyntaxError, ValueError, MemoryError) as e:
        logger.debug(f"Failed to parse literal: {e}")
        raise ValueError(f"Failed to parse literal: {e}")


def extract_dict_from_text(
    text: str,
    *,
    expected_keys: Optional[Iterable[str]] = None,
    max_depth: int = 100,
    max_length: int = 100000,
) -> Optional[dict]:
    """Extract the first useful dictionary payload embedded in free-form text."""
    if not text or not isinstance(text, str):
        return None

    key_set = {key for key in (expected_keys or []) if key}

    for candidate in _iter_candidate_dict_strings(text):
        parsed = _parse_candidate_dict(
            candidate, expected_keys=key_set, max_depth=max_depth, max_length=max_length
        )
        if parsed is not None:
            return parsed

    repaired_candidate = _repair_truncated_dict_candidate(text)
    if repaired_candidate:
        return _parse_candidate_dict(
            repaired_candidate,
            expected_keys=key_set,
            max_depth=max_depth,
            max_length=max_length,
        )

    return None


def _parse_candidate_dict(
    candidate: str,
    *,
    expected_keys: set,
    max_depth: int,
    max_length: int,
) -> Optional[dict]:
    try:
        parsed = safe_parse_python_literal(
            candidate, max_depth=max_depth, max_length=max_length
        )
    except ValueError as e:
        logger.debug(f"Failed to parse candidate payload: {e}")
        return None

    if not isinstance(parsed, dict):
        return None

    if expected_keys and not any(key in parsed for key in expected_keys):
        logger.debug("Rejected parsed dict because it did not contain any expected keys")
        return None

    return parsed


def _iter_candidate_dict_strings(text: str):
    for block in _iter_fenced_code_blocks(text):
        stripped = block.strip()
        if stripped:
            yield stripped

    yield from _iter_balanced_dict_strings(text)


def _iter_fenced_code_blocks(text: str):
    fence = "```"
    start = 0

    while True:
        block_start = text.find(fence, start)
        if block_start == -1:
            return

        content_start = block_start + len(fence)
        newline_index = text.find("\n", content_start)
        if newline_index == -1:
            return

        block_end = text.find(fence, newline_index + 1)
        if block_end == -1:
            return

        yield text[newline_index + 1 : block_end]
        start = block_end + len(fence)


def _iter_balanced_dict_strings(text: str):
    start_idx = None
    stack = []
    in_string = False
    quote_char = None
    escaped = False

    for idx, char in enumerate(text):
        if escaped:
            escaped = False
            continue

        if char == "\\":
            escaped = True
            continue

        if char in "\"'":
            if not in_string:
                in_string = True
                quote_char = char
            elif char == quote_char:
                in_string = False
                quote_char = None
            continue

        if in_string:
            continue

        if char in "{[":
            if start_idx is None and char == "{":
                start_idx = idx
            if start_idx is not None:
                stack.append(char)
            continue

        if char in "}]":
            if start_idx is None or not stack:
                continue

            opener = stack[-1]
            if (opener, char) not in {("{", "}"), ("[", "]")}:
                start_idx = None
                stack.clear()
                continue

            stack.pop()
            if start_idx is not None and not stack:
                yield text[start_idx : idx + 1]
                start_idx = None


def _repair_truncated_dict_candidate(text: str) -> Optional[str]:
    start_idx = text.find("{")
    if start_idx == -1:
        return None

    candidate = text[start_idx:].strip()
    if not candidate:
        return None

    closers = []
    in_string = False
    quote_char = None
    escaped = False

    for char in candidate:
        if escaped:
            escaped = False
            continue

        if char == "\\":
            escaped = True
            continue

        if char in "\"'":
            if not in_string:
                in_string = True
                quote_char = char
            elif char == quote_char:
                in_string = False
                quote_char = None
            continue

        if in_string:
            continue

        if char == "{":
            closers.append("}")
        elif char == "[":
            closers.append("]")
        elif char in "}]":
            if not closers or closers[-1] != char:
                return None
            closers.pop()

    if not closers:
        return None

    logger.debug("Attempting to repair truncated dictionary candidate")
    return candidate + "".join(reversed(closers))


def _check_nesting_depth(text: str, max_depth: int) -> bool:
    """Estimate bracket nesting depth without fully parsing the payload."""
    depth = 0
    in_string = False
    quote_char = None
    escaped = False

    for char in text:
        if escaped:
            escaped = False
            continue
        if char == "\\":
            escaped = True
            continue
        if char in "\"'":
            if not in_string:
                in_string = True
                quote_char = char
            elif char == quote_char:
                in_string = False
                quote_char = None
            continue

        if not in_string:
            if char in "[{(":
                depth += 1
                if depth > max_depth:
                    return False
            elif char in "]})":
                depth -= 1

    return True
