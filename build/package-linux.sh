#!/usr/bin/env bash
# Package the Linux AppImage via linuxdeploy (spec §7.2).
# Usage: package-linux.sh [rid] [artifacts-dir]
set -euo pipefail

RID="${1:-linux-x64}"
ARTIFACTS_DIR="${2:-artifacts/$RID}"
APP_DIR="${ARTIFACTS_DIR}/AppDir"

REPO_ROOT="$(cd "$(dirname "$0")/.." && pwd)"

mkdir -p "$APP_DIR/usr/bin"

# Stage the self-contained Avalonia publish + sidecar (already in ARTIFACTS_DIR).
cp -r "$ARTIFACTS_DIR/Synapic" "$APP_DIR/usr/bin/" 2>/dev/null || true
cp "$ARTIFACTS_DIR/synapic-inference" "$APP_DIR/usr/bin/" 2>/dev/null || \
  echo "WARN: sidecar not found in $ARTIFACTS_DIR"

cat > "$APP_DIR/AppRun" <<'EOF'
#!/bin/bash
HERE="$(cd "$(dirname "$0")" && pwd)"
exec "$HERE/usr/bin/Synapic" "$@"
EOF
chmod +x "$APP_DIR/AppRun"

# linuxdeploy derives the installed icon name from the desktop file's Icon=
# entry, so the PNG has to exist or the AppImage build refuses to run.
ICON_FILE="$REPO_ROOT/assets/icons/Icon.png"
if [[ ! -f "$ICON_FILE" ]]; then
  echo "ERROR: app icon not found at $ICON_FILE" >&2
  exit 1
fi

mkdir -p "$APP_DIR/usr/share/metainfo"
cat > "$APP_DIR/synapic.desktop" <<'EOF'
[Desktop Entry]
Type=Application
Name=Synapic
Comment=AI-powered image tagging for digital asset management
Exec=AppRun
Icon=synapic
Categories=Graphics;Development;
EOF

# Optional GPG signature of the final AppImage (spec §7.2).
GPG_SIGN=()
if [[ -n "${GPG_KEY_ID:-}" ]]; then
  GPG_SIGN=(--sign --gpg2-sign-key "$GPG_KEY_ID")
fi

echo "Downloading linuxdeploy"
LINUXDEPLOY="$REPO_ROOT/build/_linuxdeploy.AppImage"
if [[ ! -f "$LINUXDEPLOY" ]]; then
  curl -fsSL -o "$LINUXDEPLOY" "https://github.com/linuxdeploy/linuxdeploy/releases/download/continuous/linuxdeploy-x86_64.AppImage"
  chmod +x "$LINUXDEPLOY"
fi

echo "Building AppImage"
"$LINUXDEPLOY" --appdir "$APP_DIR" --output appimage "${GPG_SIGN[@]}" \
  --executable "$APP_DIR/usr/bin/Synapic" \
  --desktop-file "$APP_DIR/synapic.desktop" \
  --icon-file "$ICON_FILE"

mv "$REPO_ROOT"/Synapic*.AppImage "$ARTIFACTS_DIR/" 2>/dev/null || true
ls -lh "$ARTIFACTS_DIR"
