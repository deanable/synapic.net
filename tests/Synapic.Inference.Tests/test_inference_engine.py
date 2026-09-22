"""Tests for the generation-kwargs builder in ``inference_engine``.

The sidecar used to pass ``max_new_tokens`` alongside the pipeline's own
``generation_config`` (which ships ``max_length=20`` for LFM2.5-VL), logging two
transformers deprecation warnings on every image. ``_generation_kwargs`` clones
the config, pins ``max_new_tokens`` and clears ``max_length`` so generation has
a single source of truth.
"""

import inference_engine


class _FakeGenerationConfig:
    def __init__(self, **kwargs):
        self.__dict__.update(kwargs)


class _FakePipeline:
    def __init__(self, generation_config=None):
        self.generation_config = generation_config


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


def test_generation_kwargs_falls_back_when_pipeline_has_no_config():
    kwargs = inference_engine._generation_kwargs(object(), 128)
    assert kwargs == {"max_new_tokens": 128}
