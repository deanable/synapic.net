"""Pytest bootstrap: expose the sidecar source as an importable package.

The source directory is named ``Synapic.Inference`` (dot not importable), so
we register a synthetic package ``synapic_inference`` pointing at it. The
modules' own relative imports then resolve normally.
"""

import sys
import types
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parents[2]
SIDECAR_SRC = REPO_ROOT / "src" / "Synapic.Inference"

if SIDECAR_SRC.is_dir() and "synapic_inference" not in sys.modules:
    package = types.ModuleType("synapic_inference")
    package.__path__ = [str(SIDECAR_SRC)]
    sys.modules["synapic_inference"] = package
