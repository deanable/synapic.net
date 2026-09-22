#!/usr/bin/env python3
"""Verify that a packaged sidecar really is the variant its RID claims.

Usage:
    check-sidecar-variant.py <exe> <rid>

Why this exists
---------------
The CUDA variant is not a different program: it is the same sidecar built
against CUDA torch wheels. If the wheels step silently installs CPU torch (a
typo in the RID, a warm ``build/_python/<rid>`` left over from a CPU build, a
changed RID spelling), PyInstaller still produces a perfectly valid
``synapic-inference.exe`` - just a ~225 MB CPU one - and it gets labelled and
shipped as the CUDA variant. The failure only shows up on a user's GPU box,
after a 2.7 GB download, as "CUDA is not available".

So: after PyInstaller, inspect the bundle's own table of contents and assert
which torch build is actually inside it. The wheels differ in ways that are
plainly visible there:

  CPU   torch-2.9.1+cpu.dist-info,   torch/lib/torch_cpu.dll
  CUDA  torch-2.9.1+cu126.dist-info, torch/lib/torch_cuda.dll,
        torch/lib/cudart64_*.dll, torch/lib/cublas64_*.dll, torch/lib/cudnn64_*.dll

Windows CUDA wheels carry the CUDA runtime DLLs inside ``torch/lib`` (the
``nvidia-*`` pip packages are a Linux-only layout), so the DLL names are the
reliable probe. This runs on a GPU-less machine by design: it checks payload,
not inference.

Exit codes: 0 = payload matches the RID, 1 = mismatch (build is wrong),
2 = could not inspect the archive.
"""

from __future__ import annotations

import re
import sys

# CUDA runtime pieces the Windows cu12x torch wheels bundle under torch/lib, as
# (label shown in errors, pattern, required). torch_cuda.dll links against all of
# them, but only these three are load-bearing for the sidecar's inference path
# (matmul/attention/conv); the rest are reported as optional so a torch build
# that drops one of them does not red-flag an otherwise good bundle.
CUDA_RUNTIME_DLLS = (
    ("cudart64_*.dll", re.compile(r"torch\\lib\\cudart64_\d+\.dll$", re.IGNORECASE), True),
    ("cublas64_*.dll", re.compile(r"torch\\lib\\cublas64_\d+\.dll$", re.IGNORECASE), True),
    ("cudnn64_*.dll", re.compile(r"torch\\lib\\cudnn64_\d+\.dll$", re.IGNORECASE), True),
    ("cublasLt64_*.dll", re.compile(r"torch\\lib\\cublasLt64_\d+\.dll$", re.IGNORECASE), False),
    ("cufft64_*.dll", re.compile(r"torch\\lib\\cufft64_\d+\.dll$", re.IGNORECASE), False),
    ("curand64_*.dll", re.compile(r"torch\\lib\\curand64_\d+\.dll$", re.IGNORECASE), False),
    ("cusolver64_*.dll", re.compile(r"torch\\lib\\cusolver64_\d+\.dll$", re.IGNORECASE), False),
    ("cusparse64_*.dll", re.compile(r"torch\\lib\\cusparse64_\d+\.dll$", re.IGNORECASE), False),
)
TORCH_CUDA_CORE = re.compile(r"torch\\lib\\torch_cuda\.dll$", re.IGNORECASE)
TORCH_CPU_CORE = re.compile(r"torch\\lib\\torch_cpu\.dll$", re.IGNORECASE)
TORCH_DIST_INFO = re.compile(r"torch-[\d.]+(?:\+(?P<local>[a-z0-9._]+))?\.dist-info\\", re.IGNORECASE)

# Any of these inside a CPU bundle means CUDA wheels leaked into it.
CUDA_MARKERS = (
    "torch_cuda.dll",
    "torch\\lib\\cudart64_",
    "torch\\lib\\cublas64_",
    "torch\\lib\\cublaslt64_",
    "torch\\lib\\cudnn64_",
    "torch\\lib\\cufft64_",
    "torch\\lib\\cusparse64_",
    "torch\\lib\\cusolver64_",
    "torch\\lib\\curand64_",
)


def read_archive_names(exe: str) -> list[str]:
    """Top-level CArchive entries of a PyInstaller onefile bundle."""
    try:
        from PyInstaller.archive.readers import CArchiveReader
    except ImportError as exc:  # pragma: no cover - environment problem, not a build problem
        print(f"error: PyInstaller is required to inspect the bundle ({exc})", file=sys.stderr)
        raise SystemExit(2)

    try:
        return list(CArchiveReader(exe).toc.keys())
    except Exception as exc:
        print(f"error: could not read the PyInstaller archive in {exe}: {exc}", file=sys.stderr)
        raise SystemExit(2)


def describe(names: list[str]) -> str:
    """The torch build tags actually present, for the error message."""
    tags = sorted({m.group("local") or "(no local tag)" for m in
                   (TORCH_DIST_INFO.search(n) for n in names) if m})
    return ", ".join(tags) if tags else "none found"


def check(names: list[str], want_cuda: bool) -> list[str]:
    """Returns a list of human-readable problems; empty means the payload is right."""
    lowered = [n.lower() for n in names]
    problems: list[str] = []

    has_torch_cuda = any(TORCH_CUDA_CORE.match(n) for n in names)
    has_torch_cpu = any(TORCH_CPU_CORE.match(n) for n in names)
    has_service = any(n == "service" for n in names)

    if not has_service:
        problems.append(
            "the bundle has no 'service' entry point - this does not look like the "
            "synapic-inference sidecar at all (wrong .exe, or a stale distpath)"
        )

    if want_cuda:
        if not has_torch_cuda:
            problems.append(
                "torch/lib/torch_cuda.dll is missing - this is a CPU torch build, "
                "not a CUDA one. The wheels step did not install from the cu12x index "
                "(check the -cuda RID reached install-python-deps.ps1, and that no "
                "build/_python/<rid> from a CPU build was reused)."
            )
        if not has_torch_cpu:
            problems.append("torch/lib/torch_cpu.dll is missing - the torch install looks broken")

        absent = [label for label, pattern, required in CUDA_RUNTIME_DLLS
                  if required and not any(pattern.match(n) for n in names)]
        if absent:
            problems.append(
                "the CUDA runtime DLLs the sidecar needs are not bundled: "
                + ", ".join(absent)
                + " (a partial torch/nvidia install, or PyInstaller dropped them)"
            )
    else:
        leaked = [marker for marker in CUDA_MARKERS
                  if any(marker in name for name in lowered)]
        if leaked:
            problems.append(
                "CUDA runtime files are inside a CPU bundle: " + ", ".join(sorted(leaked))
                + " - the CPU variant would be multi-GB for no benefit"
            )
        if has_torch_cuda:
            problems.append("torch/lib/torch_cuda.dll is present in a CPU bundle")

    return problems


def main(argv: list[str]) -> int:
    if len(argv) != 3:
        print(__doc__.strip().splitlines()[3].strip(), file=sys.stderr)
        print("usage: check-sidecar-variant.py <exe> <rid>", file=sys.stderr)
        return 2

    exe, rid = argv[1], argv[2]
    want_cuda = rid.lower().endswith("-cuda")

    names = read_archive_names(exe)
    problems = check(names, want_cuda)

    if problems:
        print(f"FAIL: {exe} does not match RID '{rid}'", file=sys.stderr)
        for problem in problems:
            print(f"  - {problem}", file=sys.stderr)
        print(f"  torch builds found in the bundle: {describe(names)}", file=sys.stderr)
        return 1

    kind = "CUDA" if want_cuda else "CPU"
    print(f"OK: {exe} is a {kind} sidecar (RID {rid}); "
          f"torch build: {describe(names)}; {len(names)} archive entries")
    return 0


if __name__ == "__main__":
    raise SystemExit(main(sys.argv))
