#!/usr/bin/env bash
# Fetch a standalone Python 3.11 build (python-build-standalone) for a RID.
# Usage: fetch-python.sh <rid> [output-dir]
set -euo pipefail

FORCE=0
if [[ "${1:-}" == "--force" ]]; then
  FORCE=1
  shift
fi

RID="${1:?usage: fetch-python.sh [--force] <rid> [output-dir]}"
OUT_DIR="${2:-build/_python}"

# python-build-standalone release tag (pin for reproducibility)
PBS_TAG="20260901"
PYTHON_VERSION="3.11.16"

case "$RID" in
  linux-x64)  TRIPLE="x86_64-unknown-linux-gnu";  FLAVOR="install_only" ;;
  osx-x64)    TRIPLE="x86_64-apple-darwin";       FLAVOR="install_only" ;;
  osx-arm64)  TRIPLE="aarch64-apple-darwin";      FLAVOR="install_only" ;;
  win-x64)    TRIPLE="x86_64-pc-windows-msvc";    FLAVOR="install_only" ;;
  *) echo "Unsupported RID: $RID" >&2; exit 1 ;;
esac

ARCHIVE="cpython-${PYTHON_VERSION}+${PBS_TAG}-${TRIPLE}-${FLAVOR}.tar.gz"
URL="https://github.com/astral-sh/python-build-standalone/releases/download/${PBS_TAG}/${ARCHIVE}"

# Re-extracting the tarball over an existing install removes the packages that
# were pip-installed into it (verified the hard way: a rerun wiped a cu126 torch
# install), so treat an existing interpreter as done. CI always starts clean.
INSTALLED_PY="$OUT_DIR/$RID/python/bin/python3"
[[ -f "$INSTALLED_PY" ]] || INSTALLED_PY="$OUT_DIR/$RID/python/python.exe"
if [[ "$FORCE" != "1" && -f "$INSTALLED_PY" ]]; then
  echo "Standalone Python already present at $OUT_DIR/$RID/python - skipping fetch (use --force to refresh)."
  exit 0
fi

mkdir -p "$OUT_DIR/$RID"
echo "Fetching $URL"
curl -fsSL "$URL" | tar -xzf - -C "$OUT_DIR/$RID"

echo "Python installed at $OUT_DIR/$RID/python"
"$OUT_DIR/$RID/python/bin/python3" --version 2>/dev/null || "$OUT_DIR/$RID/python/python.exe" --version
