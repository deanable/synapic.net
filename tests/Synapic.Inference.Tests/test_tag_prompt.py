"""Guards on the VLM tag prompt.

The prompt is the only thing that tells the model what reply format to use, so
it is load-bearing rather than documentation. Measured against the shipped
default model (LFM2.5-VL-450M, greedy, 512 max_new_tokens, 13 images - harness
in ``build/check-tag-prompt.py``): the one-sentence wording this replaced produced
**zero** strictly valid JSON replies. All thirteen arrived wrapped in a
```` ```json ```` fence and three used single-quoted keys, so ``tag_extractor``
had to rescue every single reply and anything it could not repair was written
into the Daminion Description field as raw text. The wording now shipped
produced 13/13 strictly valid JSON.

These tests pin the three instructions that did the work, so a future rewrite
cannot quietly drop one and send the failure rate back to 100%.
"""

import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / "src" / "Synapic.Inference"))

from inference_engine import DEFAULT_VLM_USER_PROMPT  # noqa: E402
from tag_extractor import VLM_FIELD_ALIASES  # noqa: E402

PROMPT = DEFAULT_VLM_USER_PROMPT
LOWER = PROMPT.lower()


def test_prompt_names_every_key_the_extractor_reads():
    # The primary name for each field has to appear as a quoted key, or the
    # parser is hunting for a name the model was never asked to produce.
    for field, aliases in VLM_FIELD_ALIASES.items():
        assert f'"{aliases[0]}"' in PROMPT, f"{field} never named in the prompt"


def test_prompt_bans_code_fences():
    assert "no code fences" in LOWER


def test_prompt_bans_anything_outside_the_object():
    assert "nothing else" in LOWER
    assert "no text before or after" in LOWER


def test_prompt_demands_double_quotes():
    # The model's default alternative is Python-style single quotes, which is
    # what sent 3 of 13 replies down the python-literal rescue path.
    assert "double quotes" in LOWER


def test_prompt_shows_the_object_shape():
    # A worked example was worth roughly one image of compliance: the
    # fence-free wording scored 6/7, adding the example line took it to 7/7.
    assert 'Shape: {"description"' in PROMPT


def test_prompt_example_uses_placeholder_values():
    # A plausible example gets copied verbatim into the description.
    assert "words here" in LOWER
    assert "example" not in LOWER or "shape" in LOWER


def test_prompt_asks_for_a_single_line_description():
    # Literal newlines inside the string are what strict JSON rejects.
    assert "single line" in LOWER


def test_prompt_stays_inside_a_small_token_budget():
    # It is prepended to every request and the model's context is finite.
    assert len(PROMPT) < 900, f"prompt grew to {len(PROMPT)} chars"
