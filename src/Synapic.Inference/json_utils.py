"""Safe literal/JSON parsing helpers for model responses.

Port of the Python Synapic app's ``src/utils/json_utils.py``: extracts the
first useful dict payload embedded in free-form LLM/VLM text. Handles fenced
code blocks, balanced-brace scanning, and truncated-JSON repair.

Beyond the verbatim port, the search tolerates the ways a small VLM actually
gets a JSON answer wrong. Every one of these used to end in "Could not extract
JSON from model response", which writes the raw payload into the Description
field instead of tagging the image:

* a payload wrapped in an envelope (``{"result": {...}}``, ``{"data": {...}}``)
  - nested dicts are searched, outermost first;
* keys spelled with different capitalisation (``{"Description": ...}``);
* a literal newline inside a string value, which strict JSON rejects;
* the answer cut off mid-string by ``max_new_tokens``;
* the whole payload delivered JSON-encoded as a string
  (``"{\\"description\\": ...}"``).
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
        # strict=False permits raw control characters inside strings: a model
        # writing a multi-line caption emits literal newlines there, and strict
        # JSON rejects the entire payload over it.
        return json.loads(text, strict=False)
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

    # A payload that arrives JSON-encoded inside a string literal is decoded and
    # searched again; two unwrap rounds cover the quoting a VLM actually emits.
    search_text = text
    for _ in range(3):
        found = _search_dict_candidates(search_text, key_set, max_depth, max_length)
        if found is not None:
            return found

        unwrapped = _unwrap_string_payload(search_text)
        if unwrapped is None:
            return None
        search_text = unwrapped

    return None


def _search_dict_candidates(
    text: str, key_set: set, max_depth: int, max_length: int
) -> Optional[dict]:
    """First candidate payload in ``text`` carrying an expected key."""
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


def _unwrap_string_payload(text: str) -> Optional[str]:
    """Decode text that is itself a quoted string holding the payload.

    Some VLMs JSON-encode the answer, so the response arrives as
    ``"{\\"a\\": 1}"`` rather than ``{"a": 1}``. Returns the inner text, or
    None when this is not a string literal carrying braces.
    """
    stripped = text.strip()
    if stripped[:1] not in {'"', "'"}:
        return None

    try:
        inner = safe_parse_python_literal(stripped)
    except ValueError:
        return None

    return inner if isinstance(inner, str) and "{" in inner else None


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

    if expected_keys and not _has_expected_key(parsed, expected_keys):
        logger.debug("Rejected parsed dict because it did not contain any expected keys")
        return None

    return parsed


def _has_expected_key(parsed: dict, expected_keys: set) -> bool:
    """Whether the payload carries one of the expected keys, ignoring case.

    Models capitalise freely (``{"Description": ...}``) and the strict match
    this replaces threw the whole payload away over a capital letter.
    """
    present = {key.lower() for key in parsed if isinstance(key, str)}
    return any(str(key).lower() in present for key in expected_keys)


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
    """Every balanced ``{...}`` span, outermost first.

    Nested spans are included because a VLM frequently wraps the payload
    (``{"result": {...}}``, ``{"data": {...}, "success": true}``). They are
    offered after their parent so an unrelated envelope cannot shadow a payload
    that does carry the expected keys.
    """
    spans: list[tuple[int, int]] = []
    stack: list[tuple[str, int]] = []
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
            stack.append((char, idx))
            continue

        if char in "}]":
            if not stack:
                continue

            opener, start = stack[-1]
            if (opener, char) not in {("{", "}"), ("[", "]")}:
                stack.clear()
                continue

            stack.pop()
            if opener == "{":
                spans.append((start, idx + 1))

    for start, end in sorted(spans):
        yield text[start:end]


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
    # A cut-off inside a string value needs its quote closed before the brackets,
    # otherwise the appended closers land inside the unterminated literal.
    return candidate + (quote_char if in_string else "") + "".join(reversed(closers))


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
