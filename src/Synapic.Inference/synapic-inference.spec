# -*- mode: python ; coding: utf-8 -*-
# PyInstaller spec for the Synapic inference sidecar (spec §4.1).
# Builds a single-file console executable: synapic-inference(.exe)
#
# Build:  pyinstaller src/Synapic.Inference/synapic-inference.spec
#         (run from the repo root with the sidecar venv active)

import os
from PyInstaller.utils.hooks import collect_data_files, collect_submodules

block_cipher = None

# ── Entry point ──────────────────────────────────────────────────────────────
# service.py is the packaged entry point. Path layout: the spec runs with the
# sidecar dir on pathex so `import service` resolves. All sidecar modules use
# FLAT ABSOLUTE imports (import config, import model_loader, ...) because the
# entry script has no parent package — relative imports crash at exe startup.
entry = os.path.join(SPECPATH, "service.py")

hiddenimports = [
    # Core ML
    "torch", "torchvision", "timm",
    "transformers", "accelerate", "safetensors",
    "huggingface_hub", "tokenizers", "sentencepiece",
    # Vision
    "PIL", "qwen_vl_utils",
    # FastAPI stack
    "fastapi", "uvicorn", "pydantic", "pydantic_core",
    "anyio", "sniffio", "starlette", "click", "h11",
    # Stdlib modules PyInstaller sometimes misses
    "json", "pathlib", "dataclasses", "typing",
]

datas = (
    collect_data_files("transformers")
    + collect_data_files("tokenizers")
)

a = Analysis(
    [entry],
    pathex=[SPECPATH],
    binaries=[],
    datas=datas,
    hiddenimports=hiddenimports,
    hookspath=[],
    hooksconfig={},
    runtime_hooks=[],
    excludes=[
        "tkinter", "matplotlib", "jupyter", "notebook",
        "pytest", "sphinx", "docutils",
        # NOTE: do NOT exclude torch.testing or torch.distributed — both are
        # imported at runtime by torch itself (torch/autograd/gradcheck.py and
        # torch/nn/parallel/distributed.py respectively, reached during
        # torch.nn init). Excluding them breaks every /tag in the packaged exe
        # (masked as "cannot import name 'nn' from partially initialized
        # module 'torch'").
        # Not used by the sidecar (dedup → C#, metadata writes → C#)
        "cv2", "imagehash", "piexif", "iptcinfo3",
        "faiss", "sentence_transformers", "customtkinter",
    ],
    cipher=block_cipher,
    noarchive=False,
)

pyz = PYZ(a.pure, a.zipped_data, cipher=block_cipher)

exe = EXE(
    pyz,
    a.scripts,
    a.binaries,
    a.zipfiles,
    a.datas,
    [],
    name="synapic-inference",
    debug=False,
    bootloader_ignore_signals=False,
    strip=True,
    upx=True,  # Requires UPX installed; drop if AV heuristics flag it (spec §9)
    upx_exclude=[],
    runtime_tmpdir=None,
    console=True,  # Keep console for logging; hidden via CREATE_NO_WINDOW by host
    disable_windowed_traceback=False,
    argv_emulation=False,
    target_arch=None,
    codesign_identity=None,
    entitlements_file=None,
)
