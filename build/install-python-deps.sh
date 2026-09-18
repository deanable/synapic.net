#!/usr/bin/env bash
# Install the sidecar's pinned requirements into the fetched standalone Python.
# Usage: install-python-deps.sh <rid> [python-dir]
set -euo pipefail

RID="${1:?usage: install-python-deps.sh <rid> [python-dir]}"
PYTHON_DIR="${2:-build/_python/$RID/python}"

REPO_ROOT="$(cd "$(dirname "$0")/.." && pwd)"

if [[ "$RID" == win-x64 ]]; then
  PY="$PYTHON_DIR/python.exe"
else
  PY="$PYTHON_DIR/bin/python3"
fi

echo "Upgrading pip tooling"
"$PY" -m pip install --upgrade pip setuptools wheel

echo "Installing sidecar requirements (CPU torch)"
"$PY" -m pip install --no-cache-dir -r "$REPO_ROOT/src/Synapic.Inference/requirements.txt"

echo "Python deps installed for $RID"
