#!/usr/bin/env bash
# Build the Python inference sidecar with PyInstaller.
# Usage: build-sidecar.sh <rid> [python-dir] [output-dir]
set -euo pipefail

RID="${1:?usage: build-sidecar.sh <rid> [python-dir] [output-dir]}"
PYTHON_DIR="${2:-build/_python/$RID/python}"
OUT_DIR="${3:-artifacts/$RID}"

REPO_ROOT="$(cd "$(dirname "$0")/.." && pwd)"

if [[ "$RID" == win-x64 ]]; then
  PY="$PYTHON_DIR/python.exe"
else
  PY="$PYTHON_DIR/bin/python3"
fi

mkdir -p "$OUT_DIR"

cd "$REPO_ROOT"
echo "Running PyInstaller for $RID"
"$PY" -m PyInstaller --noconfirm --clean \
  --distpath "$OUT_DIR" \
  --workpath "build/_work/$RID" \
  "src/Synapic.Inference/synapic-inference.spec"

# Convenience copy without RID suffix (CI stages it next to the dotnet app).
cp -v "$OUT_DIR/synapic-inference" "$OUT_DIR/synapic-inference" 2>/dev/null || true
ls -lh "$OUT_DIR"
