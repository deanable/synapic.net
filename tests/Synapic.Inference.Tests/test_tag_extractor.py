"""Tests for tag_extractor + json_utils ports.

extract_tags_from_result and extract_dict_from_text mirror the original repo's
behaviour on well-formed input. The JSON hunt is deliberately more forgiving than
the original where a small VLM gets the payload shape wrong - envelopes,
capitalised or synonymous keys, literal newlines, truncation, and JSON delivered
inside a string. Each of those used to end in "Could not extract JSON from model
response" and write the raw payload into the Description field, so they are
pinned here, together with the payload that is still deliberately left alone.
"""

import json
import sys
from pathlib import Path

import pytest

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / "src" / "Synapic.Inference"))

from json_utils import (  # noqa: E402
    extract_dict_from_text,
    safe_parse_python_literal,
)
from tag_extractor import (  # noqa: E402
    extract_tags_from_result,
    to_title_case,
)

TASK_CLASSIFICATION = "image-classification"
TASK_ZERO_SHOT = "zero-shot-image-classification"
TASK_IMAGE_TO_TEXT = "image-to-text"
TASK_VLM = "image-text-to-text"


class TestJsonUtils:
    def test_plain_json(self):
        text = '{"description": "D", "category": "C", "keywords": ["A"]}'
        data = extract_dict_from_text(text, expected_keys={"description", "category", "keywords"})
        assert data == {"description": "D", "category": "C", "keywords": ["A"]}

    def test_fenced_code_block(self):
        text = 'Here you go:\n```json\n{"description": "D", "keywords": ["A"]}\n```\nDone.'
        data = extract_dict_from_text(text, expected_keys={"description"})
        assert data["description"] == "D"

    def test_embedded_in_prose(self):
        text = 'Answer: {"description": "D", "category": "C"} — thanks!'
        data = extract_dict_from_text(text, expected_keys={"description"})
        assert data["category"] == "C"

    def test_truncated_json_repaired(self):
        text = '{"description": "A dog", "keywords": ["dog"'
        data = extract_dict_from_text(text, expected_keys={"description"})
        assert data is not None
        assert data["description"] == "A dog"

    def test_rejects_dict_without_expected_keys(self):
        text = '{"unrelated": 1}'
        assert extract_dict_from_text(text, expected_keys={"description"}) is None

    def test_python_literal_fallback(self):
        text = "{'description': 'D', 'category': 'C'}"
        data = extract_dict_from_text(text, expected_keys={"description"})
        assert data["description"] == "D"

    def test_safe_parse_rejects_deep_nesting(self):
        text = "[" * 200 + "]" * 200
        with pytest.raises(ValueError):
            safe_parse_python_literal(text, max_depth=100)

    def test_empty_and_none_inputs(self):
        assert extract_dict_from_text("") is None
        assert extract_dict_from_text("no braces here") is None

    def test_payload_inside_an_envelope(self):
        # A wrapper object used to hide the payload: the brace scanner only ever
        # offered the outermost span, which carried none of the expected keys.
        text = '{"data": {"description": "D", "category": "C"}, "success": true}'
        data = extract_dict_from_text(text, expected_keys={"description"})
        assert data == {"description": "D", "category": "C"}

    def test_envelope_without_the_payload_is_still_rejected(self):
        text = '{"data": {"unrelated": 1}, "success": true}'
        assert extract_dict_from_text(text, expected_keys={"description"}) is None

    def test_capitalised_keys_are_accepted(self):
        text = '{"Description": "D", "Category": "C"}'
        data = extract_dict_from_text(text, expected_keys={"description"})
        assert data["Description"] == "D"

    def test_literal_newline_inside_a_string(self):
        # Multi-line captions arrive with real newlines, which strict JSON rejects.
        text = '{"description": "line one\nline two"}'
        data = extract_dict_from_text(text, expected_keys={"description"})
        assert data["description"] == "line one\nline two"

    def test_truncation_inside_a_string_is_repaired(self):
        # max_new_tokens cuts the caption mid-sentence: the repair has to close the
        # quote before the braces, or it closes the braces inside the literal.
        text = '{"description": "A cat sitting on a wooden mat in a sunlit'
        data = extract_dict_from_text(text, expected_keys={"description"})
        assert data is not None
        assert data["description"] == "A cat sitting on a wooden mat in a sunlit"

    def test_truncation_inside_an_array_value_is_repaired(self):
        text = '{"description": "D", "keywords": ["one", "two'
        data = extract_dict_from_text(text, expected_keys={"description"})
        assert data is not None
        assert data["keywords"] == ["one", "two"]

    def test_payload_delivered_as_an_escaped_string(self):
        text = json.dumps('{"description": "D", "keywords": ["K"]}')
        data = extract_dict_from_text(text, expected_keys={"description"})
        assert data["description"] == "D"


class TestTitleCase:
    def test_regular_words(self):
        assert to_title_case("hello world") == "Hello World"

    def test_hyphenated(self):
        assert to_title_case("blue-sky") == "Blue-Sky"

    def test_underscored(self):
        assert to_title_case("blue_sky") == "Blue_Sky"

    def test_slash_separated(self):
        assert to_title_case("art/design") == "Art/Design"

    def test_all_caps_preserved(self):
        # Matches the original to_title_case: all-caps words keep their case,
        # lowercase words are capitalized, "an" is not special-cased.
        assert to_title_case("JPEG image from an AI") == "JPEG Image From An AI"

    def test_mixed_case_preserved(self):
        assert to_title_case("iPhone photo") == "iPhone Photo"

    def test_empty(self):
        assert to_title_case("") == ""


class TestExtractClassification:
    def test_top5_keywords(self):
        result = [
            {"label": "golden retriever", "score": 0.9},
            {"label": "dog", "score": 0.8},
            {"label": "cat", "score": 0.7},
        ]
        category, keywords, description, _ = extract_tags_from_result(result, TASK_CLASSIFICATION, threshold=0.5)
        assert category == ""
        assert keywords == ["Golden Retriever", "Dog", "Cat"]
        assert description == ""

    def test_threshold_filters(self):
        result = [{"label": "dog", "score": 0.4}]
        _, keywords, _, _ = extract_tags_from_result(result, TASK_CLASSIFICATION, threshold=0.5)
        assert keywords == []

    def test_single_dict_result(self):
        result = {"label": "dog", "score": 0.9}
        _, keywords, _, _ = extract_tags_from_result(result, TASK_CLASSIFICATION, threshold=0.5)
        assert keywords == ["Dog"]


class TestExtractZeroShot:
    def test_best_category_selected(self):
        result = [
            {"label": "nature", "score": 0.95},
            {"label": "urban", "score": 0.05},
        ]
        category, keywords, _, _ = extract_tags_from_result(result, TASK_ZERO_SHOT, threshold=0.9)
        assert category == "Nature"
        assert keywords == []

    def test_labels_scores_dict_format(self):
        result = {"labels": ["A", "B"], "scores": [0.2, 0.95]}
        category, _, _, _ = extract_tags_from_result(result, TASK_ZERO_SHOT, threshold=0.9)
        assert category == "B"


class TestExtractImageToText:
    def test_structured_dict_payload(self):
        result = [{"generated_text": {"description": "D", "category": "C", "keywords": ["K1", "K2"]}}]
        category, keywords, description, _ = extract_tags_from_result(result, TASK_IMAGE_TO_TEXT)
        assert category == "C"
        assert keywords == ["K1", "K2"]
        assert description == "D"

    def test_json_in_text(self):
        result = [{"generated_text": '{"description": "D", "category": "C", "keywords": ["K"]}'}]
        category, keywords, description, _ = extract_tags_from_result(result, TASK_IMAGE_TO_TEXT)
        assert (category, keywords, description) == ("C", ["K"], "D")

    def test_plain_text_fallback(self):
        result = [{"generated_text": "A simple caption."}]
        category, keywords, description, _ = extract_tags_from_result(result, TASK_IMAGE_TO_TEXT)
        assert category == ""
        assert keywords == []
        assert description == "A simple caption."

    def test_prompt_prefixes_stripped(self):
        result = [{"generated_text": "Caption: The image shows a dog."}]
        _, _, description, _ = extract_tags_from_result(result, TASK_IMAGE_TO_TEXT)
        assert description == "A dog."

    def test_rogue_single_char_description_dropped(self):
        result = [{"generated_text": "S, something"}]
        _, _, description, _ = extract_tags_from_result(result, TASK_IMAGE_TO_TEXT)
        assert description == "Something" or description == ""

    def test_chat_message_format(self):
        result = [{"generated_text": [{"role": "assistant", "content": [{"type": "text", "text": "Hi"}]}]}]
        _, _, description, _ = extract_tags_from_result(result, TASK_IMAGE_TO_TEXT)
        assert description == "Hi"

    def test_vlm_task_alias_handled(self):
        result = [{"generated_text": '{"description": "D", "category": "C", "keywords": ["K"]}'}]
        category, _, description, _ = extract_tags_from_result(result, TASK_VLM)
        assert category == "C"
        assert description == "D"

    def test_category_list_sanitized_to_mode(self):
        result = [{"generated_text": {"category": ["travel", "travel", "nature"], "description": "D"}}]
        category, _, _, _ = extract_tags_from_result(result, TASK_IMAGE_TO_TEXT)
        assert category == "Travel"

    def test_no_result_placeholder_not_added_here(self):
        # The [AI: No Result] placeholder is added by the engine, not the extractor.
        result = [{"generated_text": ""}]
        category, keywords, description, _ = extract_tags_from_result(result, TASK_IMAGE_TO_TEXT)
        assert (category, keywords, description) == ("", [], "")

    def test_caption_and_tags_aliases_are_read(self):
        # The prompt asks for description/keywords, but a model answering with
        # {"caption": ..., "tags": [...]} is still giving a usable record.
        result = [{"generated_text": '{"caption": "A cat.", "tags": ["Cat", "Mat"]}'}]
        _, keywords, description, _ = extract_tags_from_result(result, TASK_IMAGE_TO_TEXT)
        assert description == "A cat."
        assert keywords == ["Cat", "Mat"]

    def test_capitalised_keys_are_read(self):
        result = [{
            "generated_text": (
                '{"Description": "A cat.", "Category": "Animals", "Keywords": ["Cat"]}'
            )
        }]
        category, keywords, description, _ = extract_tags_from_result(result, TASK_IMAGE_TO_TEXT)
        assert (category, keywords, description) == ("Animals", ["Cat"], "A cat.")

    def test_the_reply_shape_the_old_prompt_actually_produced(self):
        # Verbatim reply observed from LFM2.5-VL-450M before the prompt rewrite
        # (build/check-tag-prompt.py): a ```json fence, single-quoted keys, and the
        # object broken across lines. The prompt now asks for clean JSON, but a
        # model that ignores it must still be tagged rather than dumped.
        raw = (
            "```json\n{\n  'description': 'A simple illustration of two green "
            "mountains with a yellow sun.',\n  'category': 'Landscape',\n"
            "  'keywords': [\n    'mountains',\n    'sun'\n  ]\n}\n```"
        )
        result = [{"generated_text": [{"role": "assistant", "content": raw}]}]

        category, keywords, description, _ = extract_tags_from_result(result, TASK_VLM)

        assert category == "Landscape"
        assert keywords == ["Mountains", "Sun"]
        assert description == (
            "A simple illustration of two green mountains with a yellow sun."
        )

    def test_enveloped_payload_is_read(self):
        result = [{
            "generated_text": (
                '{"data": {"description": "A cat.", "keywords": ["Cat"]}, "success": true}'
            )
        }]
        _, keywords, description, _ = extract_tags_from_result(result, TASK_IMAGE_TO_TEXT)
        assert description == "A cat."
        assert keywords == ["Cat"]

    def test_truncated_structured_payload_is_still_tagged(self):
        raw = '{"description": "A cat sitting on a wooden mat in a sunlit'
        result = [{"generated_text": raw}]
        _, _, description, _ = extract_tags_from_result(result, TASK_IMAGE_TO_TEXT)
        assert description == "A cat sitting on a wooden mat in a sunlit"
        assert description != raw

    def test_an_unrecognised_payload_is_left_as_raw_text(self):
        # Deliberate: accepting an object with no recognised field would produce an
        # empty record and silently discard the text. Keeping it as the description
        # (with the warning) loses nothing.
        raw = '{"image_description": "A cat.", "confidence": 0.9}'
        result = [{"generated_text": raw}]
        _, _, description, _ = extract_tags_from_result(result, TASK_IMAGE_TO_TEXT)
        assert description == raw


class TestDeduplicationAndLimits:
    def test_keywords_deduplicated_after_title_case(self):
        # The original dedupes AFTER Title Casing, and Title Case preserves
        # all-caps words ("SKY"), so only exactly-equal forms collapse.
        result = [{"generated_text": '{"description": "D", "keywords": ["Sky", "sky", "SKY", "Cloud"]}'}]
        _, keywords, _, _ = extract_tags_from_result(result, TASK_IMAGE_TO_TEXT)
        assert keywords == ["Sky", "SKY", "Cloud"]

    def test_probabilities_passthrough(self):
        probs = {"A": 0.5}
        _, _, _, out = extract_tags_from_result([], TASK_CLASSIFICATION, probabilities=probs)
        assert out is probs
