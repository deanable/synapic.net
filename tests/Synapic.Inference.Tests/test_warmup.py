"""Cold-start behaviour: boot warm-up and the in-flight load wait.

The first batch of a run used to fan four parallel /tag requests at a cold
sidecar. That raced transformers' lazy import inside the frozen bundle
(``ImportError: cannot import name 'pipeline'`` -> 503 for three of the four)
and, once that was fixed, still queued behind a ~15s load that the host's
single 3s-delayed retry could not outlast. Warm-up moves the import and the
load to boot; ``_wait_for_model_ready`` turns the remaining 503 into a wait.
"""

import os
import threading
import time

import pytest

import config
import model_loader
import service


def _ready():
    """Put the shared loader state back to its idle shape."""
    model_loader.set_status("ready", None)


@pytest.fixture(autouse=True)
def _no_grace(monkeypatch):
    """The readiness head start only exists for the real host; tests skip it."""
    monkeypatch.setattr(config, "WARMUP_GRACE_SECONDS", 0.0)


def test_wait_returns_immediately_when_nothing_is_loading():
    _ready()
    assert service._wait_for_model_ready(5) is True


def test_wait_blocks_until_an_in_flight_load_finishes():
    _ready()

    def finish_later():
        time.sleep(0.5)
        _ready()

    model_loader.set_status("loading")
    threading.Thread(target=finish_later, daemon=True).start()

    started = time.monotonic()
    assert service._wait_for_model_ready(10) is True
    assert time.monotonic() - started >= 0.4
    _ready()


def test_wait_gives_up_after_the_timeout_and_signals_503():
    model_loader.set_status("loading")
    try:
        started = time.monotonic()
        assert service._wait_for_model_ready(0.5) is False
        assert time.monotonic() - started >= 0.4
    finally:
        _ready()


def test_warmup_is_skipped_when_disabled(monkeypatch):
    monkeypatch.setenv(config.WARMUP_DISABLE_ENV, "1")
    monkeypatch.setattr(
        service.model_loader, "load_model",
        lambda *a, **k: (_ for _ in ()).throw(AssertionError("must not load")),
    )
    service._warm_up_model()


def test_warmup_is_skipped_when_the_model_is_not_cached(monkeypatch):
    monkeypatch.delenv(config.WARMUP_DISABLE_ENV, raising=False)
    monkeypatch.setattr(service.model_loader, "is_model_downloaded", lambda *a, **k: False)
    monkeypatch.setattr(
        service.model_loader, "load_model",
        lambda *a, **k: (_ for _ in ()).throw(AssertionError("must not load")),
    )
    _ready()
    before = model_loader.get_state_snapshot()

    service._warm_up_model()

    assert model_loader.get_state_snapshot() == before


def test_warmup_failure_never_reports_error_status(monkeypatch):
    """/health "error" aborts the host's startup, so warm-up must not leak it."""
    monkeypatch.delenv(config.WARMUP_DISABLE_ENV, raising=False)
    monkeypatch.setattr(service.model_loader, "is_model_downloaded", lambda *a, **k: True)

    def explode(*args, **kwargs):
        model_loader.set_status("loading")
        model_loader.set_status("error", "weights are corrupt")
        raise RuntimeError("boom")

    monkeypatch.setattr(service.model_loader, "load_model", explode)
    _ready()

    service._warm_up_model()

    snapshot = model_loader.get_state_snapshot()
    assert snapshot["status"] == "ready"
    assert snapshot["error"] is None


def test_warmup_success_leaves_the_model_serving(monkeypatch):
    monkeypatch.delenv(config.WARMUP_DISABLE_ENV, raising=False)
    monkeypatch.setattr(service.model_loader, "is_model_downloaded", lambda *a, **k: True)

    def fake_load(model_id, task, device="cpu", token=None):
        model_loader.set_loaded_model(model_id, device)
        return object(), task

    monkeypatch.setattr(service.model_loader, "load_model", fake_load)

    service._warm_up_model()

    snapshot = model_loader.get_state_snapshot()
    assert snapshot["status"] == "ready"
    assert snapshot["model"] == config.DEFAULT_MODEL_ID
    _ready()


def test_env_defaults_match_the_documented_contract():
    assert os.environ.get("SYNAPIC_DISABLE_AUTO_DOWNLOAD") == "1"  # conftest
    assert os.environ.get("SYNAPIC_DISABLE_WARMUP") == "1"  # conftest
    assert config.WARMUP_DISABLE_ENV == "SYNAPIC_DISABLE_WARMUP"
    # Warm-up must stay clear of the host's readiness poll, and the wait must
    # stay inside its 5-minute /tag timeout so the single retry still has room
    # if a load really does hang.
    assert config.WARMUP_GRACE_SECONDS >= 1.0
    assert config.MODEL_LOAD_WAIT_SECONDS < 300
    assert config.MODEL_LOAD_WAIT_SECONDS >= 60
