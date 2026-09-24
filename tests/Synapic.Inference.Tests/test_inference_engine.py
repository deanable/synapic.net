"""Tests for the VLM message shape and the generation-kwargs builder.

The message shape matters as much as the kwargs: transformers 5.1's
image-text-to-text pipeline reads ``["type"]`` off every content part of every
message, so a system turn whose content is a bare string raised ``TypeError:
string indices must be integers, not 'str'`` - failing every image of a run
whenever the user typed a custom system prompt in Step 2.

The sidecar used to pass ``max_new_tokens`` alongside the pipeline's own
``generation_config`` (which ships ``max_length=20`` for LFM2.5-VL), logging two
transformers deprecation warnings on every image. ``_generation_kwargs`` clones
the config, pins ``max_new_tokens`` and clears ``max_length`` so generation has
a single source of truth. It also pins ``do_sample`` off: tag output has to be
reproducible, and a model that turns sampling on would make JSON compliance a
lottery.
"""

import inference_engine


class _FakeGenerationConfig:
    def __init__(self, **kwargs):
        self.__dict__.update(kwargs)


class _FakePipeline:
    def __init__(self, generation_config=None):
        self.generation_config = generation_config


class _CapturingPipeline:
    """Stands in for a real image-text-to-text pipeline."""

    task = "image-text-to-text"
    model_id = "fake/model"

    def __init__(self, reply):
        self.generation_config = _FakeGenerationConfig(max_length=20, max_new_tokens=None)
        self.reply = reply
        self.messages = None

    def __call__(self, text=None, generate_kwargs=None):
        self.messages = text
        return [{"generated_text": [{"role": "assistant", "content": self.reply}]}]


REPLY = '{"description": "A cat.", "category": "Animals", "keywords": ["Cat"]}'


def _image(tmp_path):
    from PIL import Image

    path = tmp_path / "sample.jpg"
    Image.new("RGB", (8, 8), (120, 130, 140)).save(path, "JPEG")
    return str(path)


def test_system_turn_carries_content_parts_not_a_bare_string():
    """A string system content crashes transformers 5.1 before inference.

    Its image-text-to-text ``preprocess`` walks every message's content and
    reads ``["type"]`` off each item, so a bare string raises
    ``TypeError: string indices must be integers, not 'str'`` - which made a
    custom system prompt fail every image of a run, not just one.
    """
    messages = inference_engine.build_vlm_messages("Be brief.", object(), "Describe.")

    assert messages[0]["role"] == "system"
    assert messages[0]["content"] == [{"type": "text", "text": "Be brief."}]
    for message in messages:
        assert isinstance(message["content"], list)


def test_no_system_prompt_means_a_single_user_turn():
    messages = inference_engine.build_vlm_messages("", object(), "Describe.")

    assert len(messages) == 1
    assert messages[0]["role"] == "user"


def test_user_turn_carries_the_image_and_the_instruction():
    messages = inference_engine.build_vlm_messages("", object(), "Describe.")

    assert [part["type"] for part in messages[0]["content"]] == ["image", "text"]
    assert messages[0]["content"][1]["text"] == "Describe."


def test_run_inference_with_a_system_prompt_tags_the_image(tmp_path):
    # End to end through the real code path: a custom system prompt must reach
    # the model and the reply must come back as tags.
    pipe = _CapturingPipeline(REPLY)

    result = inference_engine.run_inference(
        pipe,
        _image(tmp_path),
        "image-text-to-text",
        system_prompt="Use British English.",
    )

    assert pipe.messages[0]["content"] == [{"type": "text", "text": "Use British English."}]
    assert result["category"] == "Animals"
    assert result["keywords"] == ["Cat"]
    assert result["description"] == "A cat."


def test_run_inference_without_a_system_prompt_tags_the_image(tmp_path):
    pipe = _CapturingPipeline(REPLY)

    result = inference_engine.run_inference(pipe, _image(tmp_path), "image-text-to-text")

    assert [message["role"] for message in pipe.messages] == ["user"]
    assert result["category"] == "Animals"


def test_run_inference_uses_a_custom_tag_instruction(tmp_path):
    pipe = _CapturingPipeline(REPLY)

    inference_engine.run_inference(
        pipe,
        _image(tmp_path),
        "image-text-to-text",
        user_prompt="Describe this image and reply with JSON.",
    )

    assert pipe.messages[-1]["content"][1]["text"] == (
        "Describe this image and reply with JSON."
    )


def test_run_inference_treats_a_blank_tag_instruction_as_the_built_in_one(tmp_path):
    # Blank is what the Step 2 box sends when the user has not overridden the
    # instruction, so it must not reach the model as an empty user turn.
    for blank in ("", "   ", "\n\t "):
        pipe = _CapturingPipeline(REPLY)
        inference_engine.run_inference(
            pipe, _image(tmp_path), "image-text-to-text", user_prompt=blank
        )
        assert pipe.messages[-1]["content"][1]["text"] == (
            inference_engine.DEFAULT_VLM_USER_PROMPT
        )


def test_run_inference_strips_a_custom_tag_instruction(tmp_path):
    pipe = _CapturingPipeline(REPLY)

    inference_engine.run_inference(
        pipe, _image(tmp_path), "image-text-to-text", user_prompt="  Be brief.  "
    )

    assert pipe.messages[-1]["content"][1]["text"] == "Be brief."


def test_run_inference_warns_when_a_custom_instruction_drops_a_field(tmp_path, caplog):
    # Measured on the real model: asking for the three keys without saying what
    # "keywords" should look like returns valid JSON with no keywords at all.
    pipe = _CapturingPipeline(
        '{"description": "A cat.", "category": "Animals", "keywords": []}'
    )

    with caplog.at_level("WARNING"):
        inference_engine.run_inference(
            pipe, _image(tmp_path), "image-text-to-text", user_prompt="Give me JSON."
        )

    assert any("no keywords" in record.getMessage() for record in caplog.records)


def test_run_inference_does_not_warn_for_the_built_in_instruction(tmp_path, caplog):
    # The built-in instruction is the known-good one; warning about it would be
    # noise on every image.
    pipe = _CapturingPipeline(
        '{"description": "A cat.", "category": "Animals", "keywords": []}'
    )

    with caplog.at_level("WARNING"):
        inference_engine.run_inference(pipe, _image(tmp_path), "image-text-to-text")

    assert not any("no keywords" in record.getMessage() for record in caplog.records)


def test_run_inference_sends_the_json_instruction(tmp_path):
    # The format instruction travels in the user turn, so it is present whether
    # or not the user configured a system prompt.
    for system in ("", "Be brief."):
        pipe = _CapturingPipeline(REPLY)
        inference_engine.run_inference(
            pipe, _image(tmp_path), "image-text-to-text", system_prompt=system
        )
        user_turn = pipe.messages[-1]
        text = user_turn["content"][1]["text"]
        assert text == inference_engine.DEFAULT_VLM_USER_PROMPT


def test_generation_kwargs_clears_conflicting_max_length():
    pipe = _FakePipeline(_FakeGenerationConfig(max_length=20, max_new_tokens=None))

    kwargs = inference_engine._generation_kwargs(pipe, 512)

    # No bare max_new_tokens: the pipeline would otherwise pass it together
    # with a generation_config and trigger the deprecation warning.
    assert "max_new_tokens" not in kwargs
    gc = kwargs["generation_config"]
    assert gc.max_new_tokens == 512
    assert gc.max_length is None


def test_generation_kwargs_does_not_mutate_the_pipeline_config():
    original = _FakeGenerationConfig(max_length=20, max_new_tokens=None)
    pipe = _FakePipeline(original)

    inference_engine._generation_kwargs(pipe, 256)

    assert original.max_length == 20
    assert original.max_new_tokens is None
    assert original.__dict__.get("do_sample") is None


def test_generation_kwargs_falls_back_when_pipeline_has_no_config():
    kwargs = inference_engine._generation_kwargs(object(), 128)
    assert kwargs == {"max_new_tokens": 128, "do_sample": False}


def test_generation_kwargs_overrides_sampling_from_the_pipeline_config():
    pipe = _FakePipeline(
        _FakeGenerationConfig(max_length=20, max_new_tokens=None, do_sample=True, temperature=0.7)
    )

    kwargs = inference_engine._generation_kwargs(pipe, 512)

    assert kwargs["generation_config"].do_sample is False
    # ...and it does not reach into the pipeline's own config to do it.
    assert pipe.generation_config.do_sample is True
