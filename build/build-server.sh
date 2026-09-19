#!/usr/bin/env bash
# One-shot build of the Python inference sidecar (synapic-inference).
# Idempotent: fetches standalone Python if missing, installs pinned deps,
# then runs PyInstaller. Used by the app's "Build Server" button and CI.
set -euo pipefail

RID="${1:-}"
if [[ -z "$RID" ]]; then
  case "$(uname -s) $(uname -m)" in
    "Darwin arm64")  RID="osx-arm64" ;;
    Darwin*)         RID="osx-x64" ;;
    *aarch64)        RID="linux-arm64" ;;
    *)               RID="linux-x64" ;;
  esac
fi

REPO_ROOT="$(cd "$(dirname "$0")/.." && pwd)"

if [ ! -x "$REPO_ROOT/build/_python/$RID/python/bin/python3" ]; then
  echo "[1/3] Fetching standalone Python for $RID (one-time, ~30 MB)..."
  "$REPO_ROOT/build/fetch-python.sh" "$RID"
else
  echo "[1/3] Standalone Python already present - skipping fetch."
fi

echo "[2/3] Installing sidecar Python dependencies (fast when already installed)..."
"$REPO_ROOT/build/install-python-deps.sh" "$RID"

echo "[3/3] Running PyInstaller (this takes a few minutes)..."
"$REPO_ROOT/build/build-sidecar.sh" "$RID"

echo "Sidecar build complete: artifacts/$RID/synapic-inference"
