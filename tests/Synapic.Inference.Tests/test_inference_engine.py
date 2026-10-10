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
    """Stands in for a real image-text-to-text pipeline.

    Takes one reply, or several to answer consecutive calls with (the last one
    repeats). ``calls`` keeps every call's messages, so a test can see that the
    model was asked again and what the second ask said; ``messages`` stays the
    most recent call, which is what the single-call tests read.
    """

    task = "image-text-to-text"
    model_id = "fake/model"

    def __init__(self, *replies):
        self.generation_config = _FakeGenerationConfig(max_length=20, max_new_tokens=None)
        self.replies = list(replies) or [""]
        self.calls = []
        self.messages = None

    def __call__(self, text=None, generate_kwargs=None):
        self.calls.append(text)
        self.messages = text
        reply = self.replies[min(len(self.calls) - 1, len(self.replies) - 1)]
        return [{"generated_text": [{"role": "assistant", "content": reply}]}]

    def instruction_for(self, call_index):
        """The user turn's instruction text of one call."""
        content = self.calls[call_index][-1]["content"]
        return next(part["text"] for part in content if part["type"] == "text")


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


# The reply shape observed in a Daminion batch, 2026-10-10: the value ran to the
# end of its line without its closing quote and without the comma before the next
# member, so every parser gave up and the raw reply was written into Description.
BROKEN_REPLY = (
    '{\n  "description": "A cat. \n'
    '  "category": "Animals", "keywords": ["Cat"]\n}'
)


def test_run_inference_reports_no_repairs_for_a_clean_reply(tmp_path):
    # An empty list is what the host reads as "nothing to flag": a clean batch
    # must not be dressed up as a repaired one.
    result = inference_engine.run_inference(
        _CapturingPipeline(REPLY), _image(tmp_path), "image-text-to-text"
    )

    assert result["reply_repairs"] == []


def test_run_inference_reports_why_a_reply_was_rebuilt(tmp_path, caplog):
    # The tags still come out (that is the point of the repair), but the item is
    # marked so a batch the model was mangling is visible instead of silent.
    with caplog.at_level("INFO"):
        result = inference_engine.run_inference(
            _CapturingPipeline(BROKEN_REPLY), _image(tmp_path), "image-text-to-text"
        )

    assert result["reply_repairs"] == ["missing member separator"]
    assert result["category"] == "Animals"
    assert result["keywords"] == ["Cat"]
    assert result["description"] == "A cat."
    assert any("repaired" in record.getMessage() for record in caplog.records)


def test_run_inference_reports_a_reply_cut_off_by_max_new_tokens(tmp_path):
    result = inference_engine.run_inference(
        _CapturingPipeline('{"description": "A cat.", "keywords": ["Cat"'),
        _image(tmp_path),
        "image-text-to-text",
    )

    assert result["reply_repairs"] == ["truncated payload"]
    assert result["description"] == "A cat."


class _CaptioningPipeline:
    """Stands in for a plain image-to-text pipeline (BLIP, GIT, ...).

    Called positionally with an image and a fixed "Describe the image." prompt,
    and asked for a caption rather than a payload.
    """

    task = "image-to-text"
    model_id = "fake/blip"
    generation_config = None

    def __init__(self, caption):
        self.caption = caption
        self.calls = 0

    def __call__(self, image=None, prompt=None, generate_kwargs=None):
        self.calls += 1
        return [{"generated_text": self.caption}]


def test_a_captioner_is_never_asked_again(tmp_path):
    # A captioning run asks for a sentence, not a payload: there is no format
    # to get wrong, so the retry (which lives in the chat branch) never fires.
    pipe = _CaptioningPipeline("A cat sitting on a mat.")

    result = inference_engine.run_inference(pipe, _image(tmp_path), "image-to-text")

    assert pipe.calls == 1
    assert result["reply_retried"] is False
    assert result["description"] == "A cat sitting on a mat."


def test_a_malformed_reply_is_asked_again_and_the_second_one_is_used(tmp_path):
    # The first reply is the shape that used to end up as raw text in the
    # Description; the retry is a chance to get a reply that reads cleanly.
    pipe = _CapturingPipeline(BROKEN_REPLY, REPLY)

    result = inference_engine.run_inference(pipe, _image(tmp_path), "image-text-to-text")

    assert len(pipe.calls) == 2
    assert result["reply_retried"] is True
    assert result["reply_repairs"] == []  # the reply that was used needed no repair
    assert result["category"] == "Animals"
    assert result["keywords"] == ["Cat"]
    assert result["description"] == "A cat."


def test_the_retry_shows_the_model_its_own_broken_reply(tmp_path):
    # Re-formatting a known answer keeps what the model already said; a fresh
    # guess at the image would lose it.
    pipe = _CapturingPipeline(BROKEN_REPLY, REPLY)

    inference_engine.run_inference(pipe, _image(tmp_path), "image-text-to-text")

    retry_turn = pipe.instruction_for(1)
    assert "one line of strict JSON" in retry_turn
    assert "could not be read as JSON" in retry_turn
    assert BROKEN_REPLY in retry_turn  # the previous reply, verbatim
    assert pipe.instruction_for(0) == inference_engine.DEFAULT_VLM_USER_PROMPT


def test_a_truncated_reply_is_asked_again(tmp_path):
    pipe = _CapturingPipeline('{"description": "A cat.", "keywords": ["Cat"', REPLY)

    result = inference_engine.run_inference(pipe, _image(tmp_path), "image-text-to-text")

    assert len(pipe.calls) == 2
    assert result["reply_retried"] is True
    assert result["reply_repairs"] == []
    assert result["category"] == "Animals"


def test_a_reply_that_stays_broken_keeps_the_first_one(tmp_path):
    # A retry that repeats the mistake must not replace the reply in hand (nor
    # cost the run its tags: the repair still reads the first one).
    pipe = _CapturingPipeline(BROKEN_REPLY, BROKEN_REPLY)

    result = inference_engine.run_inference(pipe, _image(tmp_path), "image-text-to-text")

    assert len(pipe.calls) == 2
    assert result["reply_retried"] is True
    assert result["reply_repairs"] == ["missing member separator"]
    assert result["description"] == "A cat."


def test_a_clean_reply_is_never_asked_again(tmp_path):
    pipe = _CapturingPipeline(REPLY)

    result = inference_engine.run_inference(pipe, _image(tmp_path), "image-text-to-text")

    assert len(pipe.calls) == 1
    assert result["reply_retried"] is False
    assert result["reply_repairs"] == []


def test_a_caption_is_not_retried_when_the_instruction_asks_for_prose(tmp_path):
    # A user who replaced the built-in instruction with prose of their own gets a
    # caption, which is not a malformed reply - retrying every image would double
    # the cost of such a run to fix a problem it does not have.
    pipe = _CapturingPipeline("A cat sitting on a mat.")

    result = inference_engine.run_inference(
        pipe, _image(tmp_path), "image-text-to-text", user_prompt="Describe this image."
    )

    assert len(pipe.calls) == 1
    assert result["reply_retried"] is False
    assert "cat" in result["description"].lower()


def test_a_custom_instruction_that_asks_for_json_is_still_retried(tmp_path):
    # The wording is the user's, but the intent is a payload - so a reply that
    # cannot be read is still worth one more ask.
    pipe = _CapturingPipeline(BROKEN_REPLY, REPLY)

    result = inference_engine.run_inference(
        pipe,
        _image(tmp_path),
        "image-text-to-text",
        user_prompt="Reply with one JSON object holding description and keywords.",
    )

    assert len(pipe.calls) == 2
    assert result["reply_retried"] is True
    assert result["description"] == "A cat."


def test_a_retry_that_fails_to_generate_does_not_fail_the_item(tmp_path):
    class _FailsOnRetry(_CapturingPipeline):
        def __call__(self, text=None, generate_kwargs=None):
            if self.calls:  # the second call: recorded, then the pipeline dies
                self.calls.append(text)
                raise RuntimeError("out of memory")
            return super().__call__(text, generate_kwargs)

    pipe = _FailsOnRetry(BROKEN_REPLY)

    result = inference_engine.run_inference(pipe, _image(tmp_path), "image-text-to-text")

    assert len(pipe.calls) == 2  # asked, and the ask failed
    assert result["reply_retried"] is True
    assert result["reply_repairs"] == ["missing member separator"]
    assert result["description"] == "A cat."


def test_run_inference_reports_a_clean_reply_that_is_not_json(tmp_path):
    # A plain caption is not a repair: the extractor deliberately keeps it as the
    # description, so the item is not flagged as rebuilt.
    result = inference_engine.run_inference(
        _CapturingPipeline("A cat sitting on a mat."),
        _image(tmp_path),
        "image-text-to-text",
    )

    assert result["reply_repairs"] == []
    assert "cat" in result["description"].lower()


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
