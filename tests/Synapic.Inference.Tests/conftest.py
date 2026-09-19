"""Pytest bootstrap: put the sidecar source directory on sys.path.

The sidecar modules use flat absolute imports (``import config``,
``import model_loader``, ...) because ``service.py`` is the PyInstaller entry
script and cannot use relative imports. The tests therefore import the modules
as top-level modules too, keeping one import identity between app and tests.

Also disables the startup default-model auto-download so tests never touch
the network for LiquidAI/LFM2.5-VL-450M.
"""

import os
import sys
from pathlib import Path

import pytest

os.environ.setdefault("SYNAPIC_DISABLE_AUTO_DOWNLOAD", "1")

REPO_ROOT = Path(__file__).resolve().parents[2]
SIDECAR_SRC = REPO_ROOT / "src" / "Synapic.Inference"

if SIDECAR_SRC.is_dir() and str(SIDECAR_SRC) not in sys.path:
    sys.path.insert(0, str(SIDECAR_SRC))


@pytest.fixture(autouse=True)
def _reset_download_state():
    """Keep the module-level download state from leaking between tests."""
    import model_loader

    model_loader.reset_download_state()
    yield
    model_loader.reset_download_state()
