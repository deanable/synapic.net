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

# CUDA is a Windows-only packaging variant (see install-python-deps.ps1) and this
# script has no cu12x wheel path. Refuse loudly rather than quietly installing CPU
# wheels into a directory that build-sidecar.sh would then package as "CUDA".
if [[ "$RID" == *-cuda ]]; then
  echo "Unsupported RID: $RID - CUDA variants are Windows-only; use build/install-python-deps.ps1" >&2
  exit 1
fi

echo "Upgrading pip tooling"
"$PY" -m pip install --upgrade pip setuptools wheel

echo "Installing torch/torchvision (CPU wheels — CUDA variants substituted at packaging time)"
"$PY" -m pip install --no-cache-dir torch==2.9.1 torchvision==0.24.1 --index-url https://download.pytorch.org/whl/cpu

echo "Installing sidecar requirements (torch/torchvision already satisfied)"
"$PY" -m pip install --no-cache-dir -r "$REPO_ROOT/src/Synapic.Inference/requirements.txt"

echo "Python deps installed for $RID"
