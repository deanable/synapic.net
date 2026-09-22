"""Keeps ``docs/sidecar-protocol.md`` honest.

The document is generated from the live FastAPI OpenAPI schema and the C#
contract DTOs. Running the generator in check mode here means the normal test
suite (not just CI's dedicated step) fails when a request DTO changes on one
side only, when a response DTO disappears, or when the committed document is
stale.
"""

import subprocess
import sys
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parents[2]


def test_protocol_doc_matches_code():
    result = subprocess.run(
        [sys.executable, "build/generate-protocol-doc.py", "--check"],
        cwd=REPO_ROOT,
        capture_output=True,
        text=True,
    )
    assert result.returncode == 0, (
        "docs/sidecar-protocol.md is out of date or the contract drifted.\n"
        f"{result.stdout}{result.stderr}"
    )
