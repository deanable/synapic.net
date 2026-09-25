"""Tests for build/check-load-dtype.py.

The guard decides whether a *packed* sidecar will load the model in float32
on CPU - the difference between 2.0 s and 4.7 s per tagged image on AVX2-only
x86. It reads ``model_loader`` back out of the bundle's PYZ, so the part that
can be exercised without a PyInstaller build is the policy check itself.

Only ``check()`` (pure) is exercised here: the CI test job installs no torch
and no PyInstaller, and ``read_module_code`` imports PyInstaller lazily.
"""

from __future__ import annotations

import importlib.util
from pathlib import Path

import pytest

REPO_ROOT = Path(__file__).resolve().parents[2]
SCRIPT = REPO_ROOT / "build" / "check-load-dtype.py"


def _load_module():
    spec = importlib.util.spec_from_file_location("check_load_dtype", SCRIPT)
    module = importlib.util.module_from_spec(spec)
    assert spec.loader is not None
    spec.loader.exec_module(module)
    return module


@pytest.fixture(scope="module")
def guard():
    return _load_module()


# ── Fixtures ─────────────────────────────────────────────────────────────
# Source compiled into the module code object PyInstaller would pack, so the
# cases stay readable instead of being hand-built code objects.


def _module(source: str):
    return compile(source, "<fake model_loader>", "exec")


POLICY = """
def _load_dtype(device_str):
    if device_str == "cpu":
        return "float32"
    return "auto"


def _construct_model(device_str):
    dtype = _load_dtype(device_str)
    return dtype
"""


AUTO_EVERYWHERE = """
def _load_dtype(device_str):
    return "auto"


def _construct_model(device_str):
    dtype = _load_dtype(device_str)
    return dtype
"""


NO_POLICY = """
def _construct_model(device_str):
    dtype = "auto"
    return dtype
"""


POLICY_UNUSED = """
def _load_dtype(device_str):
    if device_str == "cpu":
        return "float32"
    return "auto"


def _construct_model(device_str):
    dtype = "auto"
    return dtype
"""


RAISING_POLICY = """
def _load_dtype(device_str):
    raise RuntimeError("no torch here")


def _construct_model(device_str):
    return _load_dtype(device_str)
"""


NO_CONSTRUCTOR = """
def _load_dtype(device_str):
    if device_str == "cpu":
        return "float32"
    return "auto"
"""


# ── Cases ────────────────────────────────────────────────────────────────


def test_packed_policy_passes(guard):
    assert guard.check(_module(POLICY)) == []


def test_auto_on_cpu_is_reported(guard):
    problems = guard.check(_module(AUTO_EVERYWHERE))

    assert len(problems) == 1
    assert "_load_dtype('cpu')" in problems[0]
    assert "'auto'" in problems[0] and "'float32'" in problems[0]


def test_bundle_without_the_policy_is_reported(guard):
    # The real regression this guards against: a bundle packed before
    # _load_dtype existed, which silently loads bf16 on CPU.
    problems = guard.check(_module(NO_POLICY))

    assert len(problems) == 1
    assert "no _load_dtype()" in problems[0]
    assert "bfloat16" in problems[0]


def test_policy_that_is_never_used_is_reported(guard):
    # _load_dtype could be present while _construct_model hard-codes dtype="auto".
    problems = guard.check(_module(POLICY_UNUSED))

    assert any("_construct_model() never calls _load_dtype()" in p for p in problems)


def test_policy_that_raises_is_reported(guard):
    problems = guard.check(_module(RAISING_POLICY))

    assert problems
    assert any("raised" in p for p in problems)


def test_missing_construct_model_is_reported(guard):
    problems = guard.check(_module(NO_CONSTRUCTOR))

    assert len(problems) == 1
    assert "_construct_model()" in problems[0]


def test_every_device_is_pinned(guard):
    # cuda/mps stay on "auto": bf16 is native there and halves memory traffic.
    assert guard.EXPECTED == {"cpu": "float32", "cuda": "auto", "mps": "auto"}
