#!/usr/bin/env python3
"""Verify that a packed sidecar will load the model in float32 on CPU.

Usage:
    check-load-dtype.py <exe>

Why this exists
---------------
LFM2.5-VL ships bfloat16 weights and ``dtype="auto"`` keeps them, so a
sidecar that resolves its load dtype with ``"auto"`` runs the whole model in
bfloat16. On x86 without AVX512-BF16/AMX - every mainstream 12th-14th gen
Core part - oneDNN has no native bf16 GEMM and emulates it instead: on an
i7-14700K a matmul that does 1077 GFLOPS in fp32 does 0.8 in bf16, and a
tagged image went from 2.0 s to 4.7 s.

Nothing on the surface shows it: the bundle is valid, ``/health`` says
"ready", and inference works. The difference lives in one function,
``model_loader._load_dtype`` - exactly the sort of thing a PyInstaller run
can silently make stale (a bundle packed before the policy existed, a reused
``build/_work`` cache, a release cut from the wrong commit).

So read that function back out of the *packed* bundle and assert what it
will answer. This needs no torch and never loads a model: it checks the
policy, not inference. ``build/build-sidecar.ps1|.sh`` calls it right after
``check-sidecar-variant.py``, so CI, a release, and the app's own Build
Server all refuse a bundle that would load bf16 on CPU.

Exit codes: 0 = the packed policy is right, 1 = it is not (do not ship it),
2 = could not inspect the archive.
"""

from __future__ import annotations

import os
import sys
import tempfile
import types

# device -> dtype the packed _load_dtype() has to resolve to.
EXPECTED = {
    "cpu": "float32",  # bf16 has no hardware GEMM on mainstream x86
    "cuda": "auto",    # native bf16 there; it halves the memory traffic
    "mps": "auto",
}


def read_module_code(exe: str):
    """The packed ``model_loader`` code object, pulled out of the bundle's PYZ."""
    try:
        from PyInstaller.archive.readers import CArchiveReader, ZlibArchiveReader
    except ImportError as exc:  # pragma: no cover - environment problem, not a build problem
        print(f"error: PyInstaller is required to inspect the bundle ({exc})", file=sys.stderr)
        raise SystemExit(2)

    try:
        archive = CArchiveReader(exe)
        pyz_name = next(
            (name for name in archive.toc if name.lower().endswith(".pyz")), None
        )
        if pyz_name is None:
            raise KeyError("no .pyz entry in the archive - not a PyInstaller bundle")
        payload = archive.extract(pyz_name)
    except Exception as exc:
        print(f"error: could not read the PyInstaller archive in {exe}: {exc}", file=sys.stderr)
        raise SystemExit(2)

    handle, path = tempfile.mkstemp(suffix=".pyz")
    os.close(handle)
    try:
        with open(path, "wb") as stream:
            stream.write(payload)
        pyz = ZlibArchiveReader(path)
        if "model_loader" not in list(pyz.toc):
            raise KeyError("'model_loader' is missing from the bundle's PYZ")
        code = pyz.extract("model_loader")
    except Exception as exc:
        print(f"error: could not read 'model_loader' out of {exe}: {exc}", file=sys.stderr)
        raise SystemExit(2)
    finally:
        try:
            os.unlink(path)
        except OSError:  # pragma: no cover - the reader may still hold it on some platforms
            pass

    return code[1] if isinstance(code, tuple) else code


def check(code) -> list[str]:
    """Human-readable problems; an empty list means the packed policy is right."""
    functions = {
        const.co_name: const for const in code.co_consts if hasattr(const, "co_name")
    }
    problems: list[str] = []

    policy = functions.get("_load_dtype")
    if policy is None:
        return [
            "the packed model_loader has no _load_dtype() - this bundle predates the "
            "CPU float32 policy (rebuild it from the current commit; it would load "
            "bfloat16 on CPU)"
        ]

    # Running just that one function's bytecode: it takes the device string and
    # returns one, so there is no module import, no torch and no model to load.
    load_dtype = types.FunctionType(policy, {"__builtins__": __builtins__})
    for device, expected in EXPECTED.items():
        try:
            resolved = load_dtype(device)
        except Exception as exc:
            problems.append(f"_load_dtype({device!r}) raised {exc!r}")
            continue
        if resolved != expected:
            problems.append(
                f"_load_dtype({device!r}) returns {resolved!r}, expected {expected!r} "
                f"- on {device} this bundle would load the model with the wrong dtype"
            )

    constructor = functions.get("_construct_model")
    if constructor is None:
        problems.append(
            "the packed model_loader has no _construct_model() - this does not look "
            "like the sidecar's model loader"
        )
    elif "_load_dtype" not in constructor.co_names:
        problems.append(
            "_construct_model() never calls _load_dtype() - the policy is packed but "
            "unused, so the bundle hard-codes its own dtype"
        )

    return problems


def main(argv: list[str]) -> int:
    if len(argv) != 2:
        print("usage: check-load-dtype.py <exe>", file=sys.stderr)
        return 2

    exe = argv[1]
    problems = check(read_module_code(exe))

    if problems:
        print(f"FAIL: {exe} would not load the model as expected", file=sys.stderr)
        for problem in problems:
            print(f"  - {problem}", file=sys.stderr)
        return 1

    wants = ", ".join(f"{device}={dtype}" for device, dtype in EXPECTED.items())
    print(f"OK: {exe} resolves the load dtype ({wants})")
    return 0


if __name__ == "__main__":
    raise SystemExit(main(sys.argv))
