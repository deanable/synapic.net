"""Pytest bootstrap: put the sidecar source directory on sys.path.

The sidecar modules use flat absolute imports (``import config``,
``import model_loader``, ...) because ``service.py`` is the PyInstaller entry
script and cannot use relative imports. The tests therefore import the modules
as top-level modules too, keeping one import identity between app and tests.
"""

import sys
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parents[2]
SIDECAR_SRC = REPO_ROOT / "src" / "Synapic.Inference"

if SIDECAR_SRC.is_dir() and str(SIDECAR_SRC) not in sys.path:
    sys.path.insert(0, str(SIDECAR_SRC))
