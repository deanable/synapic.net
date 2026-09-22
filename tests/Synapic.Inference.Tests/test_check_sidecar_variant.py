"""Tests for build/check-sidecar-variant.py.

The guard decides whether a packaged sidecar is the CPU or the CUDA variant by
looking at the PyInstaller archive's table of contents. It is the only thing
standing between a wrong-wheels build and a 2.7 GB bundle that is shipped as
"CUDA", so its rules get tested directly rather than only through a real build.

Only ``check()`` (pure) is exercised here: the CI test job installs no torch and
no PyInstaller, and ``read_archive_names`` imports PyInstaller lazily.
"""

from __future__ import annotations

import importlib.util
from pathlib import Path

import pytest

REPO_ROOT = Path(__file__).resolve().parents[2]
SCRIPT = REPO_ROOT / "build" / "check-sidecar-variant.py"


def _load_module():
    spec = importlib.util.spec_from_file_location("check_sidecar_variant", SCRIPT)
    module = importlib.util.module_from_spec(spec)
    assert spec.loader is not None
    spec.loader.exec_module(module)
    return module


@pytest.fixture(scope="module")
def guard():
    return _load_module()


# ── Payload fixtures ─────────────────────────────────────────────────────────
# Entry names as PyInstaller stores them in the CArchive TOC (single backslashes).

CPU_PAYLOAD = [
    "service",
    "torch-2.9.1+cpu.dist-info\\METADATA",
    "torch\\lib\\torch_cpu.dll",
    "torch\\backends\\cudnn\\__init__.py",
    "torch\\backends\\cudnn\\rnn.py",
    "PIL\\_imaging.cp311-win_amd64.pyd",
]

CUDA_PAYLOAD = [
    "service",
    "torch-2.9.1+cu126.dist-info\\METADATA",
    "torch\\lib\\torch_cpu.dll",
    "torch\\lib\\torch_cuda.dll",
    "torch\\lib\\cudart64_12.dll",
    "torch\\lib\\cublas64_12.dll",
    "torch\\lib\\cublasLt64_12.dll",
    "torch\\lib\\cudnn64_9.dll",
    "torch\\lib\\cufft64_11.dll",
]


def test_cpu_payload_satisfies_cpu_rid(guard):
    assert guard.check(CPU_PAYLOAD, want_cuda=False) == []


def test_cuda_payload_satisfies_cuda_rid(guard):
    assert guard.check(CUDA_PAYLOAD, want_cuda=True) == []


def test_cpu_payload_rejected_for_cuda_rid(guard):
    problems = guard.check(CPU_PAYLOAD, want_cuda=True)
    assert any("torch_cuda.dll is missing" in p for p in problems)
    # The message must point at the wheels step, which is the actual cause.
    assert any("cu12x index" in p for p in problems)


def test_cuda_payload_rejected_for_cpu_rid(guard):
    problems = guard.check(CUDA_PAYLOAD, want_cuda=False)
    assert any("torch_cuda.dll is present" in p for p in problems)
    assert any("CUDA runtime files are inside a CPU bundle" in p for p in problems)


@pytest.mark.parametrize(
    "entry",
    [
        "torch\\backends\\cudnn\\__init__.py",
        "torch\\backends\\cudnn\\rnn.py",
        "torch\\utils\\_cuda_is_available.py",
    ],
)
def test_cuda_python_sources_are_not_treated_as_leaks(guard, entry):
    """torch ships CUDA *modules* in every build - only runtime binaries count."""
    payload = [*CPU_PAYLOAD, entry]
    assert guard.check(payload, want_cuda=False) == []


def test_wrong_exe_is_reported(guard):
    problems = guard.check(["something-else", "torch\\lib\\torch_cpu.dll"], want_cuda=False)
    assert any("no 'service' entry point" in p for p in problems)


@pytest.mark.parametrize(
    ("missing", "expected"),
    [
        ("cudart64_12.dll", "cudart64_"),
        ("cublas64_12.dll", "cublas64_"),
        ("cudnn64_9.dll", "cudnn64_"),
    ],
)
def test_partial_cuda_runtime_is_reported(guard, missing, expected):
    payload = [p for p in CUDA_PAYLOAD if not p.endswith(missing)]
    problems = guard.check(payload, want_cuda=True)
    assert any("CUDA runtime DLLs the sidecar needs are not bundled" in p for p in problems)
    assert any(expected in p for p in problems)


def test_optional_cuda_extras_may_be_absent(guard):
    """cufft/cusparse are not required by the sidecar's inference path."""
    payload = [p for p in CUDA_PAYLOAD if not p.endswith("cufft64_11.dll")]
    assert guard.check(payload, want_cuda=True) == []


def test_broken_cuda_build_without_torch_cpu_is_reported(guard):
    payload = [p for p in CUDA_PAYLOAD if not p.endswith("torch_cpu.dll")]
    problems = guard.check(payload, want_cuda=True)
    assert any("torch_cpu.dll is missing" in p for p in problems)


def test_describe_reports_the_torch_build_tag(guard):
    assert guard.describe(CPU_PAYLOAD) == "cpu"
    assert guard.describe(CUDA_PAYLOAD) == "cu126"
    assert guard.describe(["service"]) == "none found"


def test_usage_error_exits_2_without_touching_the_archive(guard):
    # No archive is read, so this works without PyInstaller installed.
    assert guard.main(["check-sidecar-variant.py"]) == 2
    assert guard.main(["check-sidecar-variant.py", "only-one-arg"]) == 2
