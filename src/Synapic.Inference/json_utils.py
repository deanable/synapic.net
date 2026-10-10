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
* a value the model ended at the line break without its closing quote *and*
  without the comma that separates it from the next member, so nothing between
  the two members is well formed in any dialect;
* the whole payload delivered JSON-encoded as a string
  (``"{\\"description\\": ...}"``).
"""

from __future__ import annotations

import ast
import json
import logging
import re
from typing import Any, Iterable, List, Optional

logger = logging.getLogger(__name__)

# The start of a JSON member after its quote: ``key": ...``. Used to recognise
# the boundary where one member should have ended and the next one begins.
_MEMBER_KEY = re.compile(r'([A-Za-z_][A-Za-z0-9_.\- ]*)"\s*:')

# Why a reply had to be rewritten before it could be read. Reported to the host,
# which shows those items as repaired (see json_utils module docstring).
REASON_MISSING_MEMBER_SEPARATOR = "missing member separator"
REASON_TRUNCATED_PAYLOAD = "truncated payload"

# The characters a member boundary may legitimately follow: the object or array
# opener, the previous separator, or a key's colon.
_BOUNDARY_OK_BEFORE = (",", "{", "[", ":")


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
    repairs: Optional[List[str]] = None,
) -> Optional[dict]:
    """Extract the first useful dictionary payload embedded in free-form text.

    When ``repairs`` is given, one short reason is appended to it for every
    rewrite that was needed before the payload could be read (none for a reply
    that parsed as it arrived). The caller surfaces those items as repaired
    rather than clean, so a batch whose model was mangling its own JSON is
    visible instead of silently written as ordinary content.
    """
    if not text or not isinstance(text, str):
        return None

    key_set = {key for key in (expected_keys or []) if key}

    # A payload that arrives JSON-encoded inside a string literal is decoded and
    # searched again; two unwrap rounds cover the quoting a VLM actually emits.
    search_text = text
    for _ in range(3):
        for candidate_text, source_repair in _candidate_sources(search_text, key_set):
            # Reasons are collected per attempt and only handed to the caller when
            # the attempt really produced a payload, so a failed search cannot
            # leave a reason behind against a payload found later.
            attempt = [source_repair] if source_repair else []
            found = _search_dict_candidates(
                candidate_text, key_set, max_depth, max_length, repairs=attempt
            )
            if found is not None:
                if repairs is not None:
                    repairs.extend(attempt)
                return found

        unwrapped = _unwrap_string_payload(search_text)
        if unwrapped is None:
            return None
        search_text = unwrapped

    return None


def _candidate_sources(text: str, expected_keys: set):
    """``(text, repair reason)``: the text as it arrived, then its repaired form.

    The repair is only attempted when the text as it arrived yields nothing, so a
    payload that already parses is never rewritten.
    """
    yield text, None

    repaired = _repair_missing_member_separators(text, expected_keys)
    if repaired is not None:
        yield repaired, REASON_MISSING_MEMBER_SEPARATOR


def _search_dict_candidates(
    text: str,
    key_set: set,
    max_depth: int,
    max_length: int,
    repairs: Optional[List[str]] = None,
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
        parsed = _parse_candidate_dict(
            repaired_candidate,
            expected_keys=key_set,
            max_depth=max_depth,
            max_length=max_length,
        )
        if parsed is not None:
            if repairs is not None:
                repairs.append(REASON_TRUNCATED_PAYLOAD)
            return parsed

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


def _repair_missing_member_separators(text: str, expected_keys: set) -> Optional[str]:
    """Close the value and restore the separator where two members ran together.

    Observed reply (a Daminion item, 2026-10-10, LFM2.5-VL-450M): the description
    ran to the end of its line and the next line opened ``"category":`` straight
    away — the closing quote and the comma between the members were simply not
    written::

        {
          "description": "… a futuristic or otherworldly atmosphere. 
          "category": "visual_art", "keywords": ["staircase", …]
        }

    Nothing can read that: strict JSON rejects the bare words after the quote,
    ``strict=False`` cannot see a member boundary that is not there, and the
    balanced-brace hunt finds no span at all (the unclosed quote swallows the
    closing brace). The reply ended up in the Description field as 450 characters
    of raw text, with the category and the keywords lost.

    The repair adds at most the two characters of a member boundary (``",`` ), and
    only beside a quote that opens a key the caller is looking for — the tag
    extractor passes the payload's key vocabulary — so a quoted phrase inside a
    caption is left alone and valid JSON is returned untouched (its boundary is
    already there). A caller that passes no keys is the one case where any key
    counts; every caller in the app passes keys.
    """
    out: list[str] = []
    in_string = False
    escaped = False
    repaired = False

    for position, char in enumerate(text):
        if escaped:
            escaped = False
        elif char == "\\":
            escaped = True
        elif char == '"':
            key = _key_opened_at(text, position + 1)

            if key is not None and _key_is_expected(key, expected_keys):
                if in_string:
                    # The value ran on into the next key: it was never closed. What
                    # the model left at the end of that line is the line break and
                    # the next member's indentation, so the quote goes in front of it
                    # rather than after it ("… hue. \n  " is not a caption).
                    while out and out[-1].isspace():
                        out.pop()
                    out.append('"' + ",")
                    repaired = True
                elif (
                    before := _last_significant(out)
                ) is not None and before not in _BOUNDARY_OK_BEFORE:
                    # The value is closed but its separator is missing.
                    out.append(",")
                    repaired = True

                # The quote being copied opens that key, so the scan continues
                # inside the key's string either way.
                in_string = True
            else:
                in_string = not in_string

        out.append(char)

    return "".join(out) if repaired else None


def _key_opened_at(text: str, start: int) -> Optional[str]:
    """The key this quote would open, when ``key":`` follows it."""
    match = _MEMBER_KEY.match(text, start)
    return match.group(1).strip() if match else None


def _key_is_expected(key: str, expected_keys: set) -> bool:
    if not expected_keys:
        return True
    return key.lower() in {str(name).lower() for name in expected_keys}


def _last_significant(out: list) -> Optional[str]:
    """The last non-space character emitted, or None when there is none yet."""
    for char in reversed(out):
        if not char.isspace():
            return char
    return None


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
