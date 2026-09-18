#!/usr/bin/env bash
# Package a macOS DMG with code signing + notarization (spec §7.2).
# Usage: package-macos.sh <rid> [artifacts-dir]
# Env: MACOS_SIGNING_IDENTITY, APPLE_ID, APPLE_PASSWORD, APPLE_TEAM_ID (notarization)
set -euo pipefail

RID="${1:?usage: package-macos.sh <rid> [artifacts-dir]}"
ARTIFACTS_DIR="${2:-artifacts/$RID}"
APP_NAME="Synapic.app"
APP_DIR="$ARTIFACTS_DIR/$APP_NAME"

REPO_ROOT="$(cd "$(dirname "$0")/.." && pwd)"

mkdir -p "$APP_DIR/Contents/MacOS" "$APP_DIR/Contents/Resources"

# Stage the self-contained Avalonia publish + sidecar.
cp -r "$ARTIFACTS_DIR/Synapic" "$APP_DIR/Contents/MacOS/" 2>/dev/null || true
cp "$ARTIFACTS_DIR/synapic-inference" "$APP_DIR/Contents/MacOS/" 2>/dev/null || \
  echo "WARN: sidecar not found in $ARTIFACTS_DIR"

cat > "$APP_DIR/Contents/Info.plist" <<EOF
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>CFBundleName</key><string>Synapic</string>
    <key>CFBundleIdentifier</key><string>net.synapic.app</string>
    <key>CFBundleVersion</key><string>1.0.0</string>
    <key>CFBundleShortVersionString</key><string>1.0.0</string>
    <key>CFBundleExecutable</key><string>Synapic</string>
    <key>CFBundlePackageType</key><string>APPL</string>
    <key>LSMinimumSystemVersion</key><string>10.15</string>
    <key>NSHighResolutionCapable</key><true/>
</dict>
</plist>
EOF

if [[ -n "${MACOS_SIGNING_IDENTITY:-}" ]]; then
  echo "Codesigning with identity: $MACOS_SIGNING_IDENTITY"
  # Sign the sidecar and all bundled .dylib/.so first (spec §9 notarization risk).
  find "$APP_DIR" -name "*.dylib" -o -name "*.so" | while read -r lib; do
    codesign --force --timestamp --options runtime \
      --sign "$MACOS_SIGNING_IDENTITY" "$lib"
  done
  codesign --force --timestamp --options runtime \
    --sign "$MACOS_SIGNING_IDENTITY" "$APP_DIR/Contents/MacOS/synapic-inference"
  codesign --force --timestamp --options runtime \
    --entitlements "$REPO_ROOT/build/macos-entitlements.plist" \
    --sign "$MACOS_SIGNING_IDENTITY" "$APP_DIR"
else
  echo "MACOS_SIGNING_IDENTITY not set — skipping codesign (ad-hoc)"
  codesign --force --deep -s - "$APP_DIR"
fi

# Notarization + stapling.
if [[ -n "${APPLE_ID:-}" && -n "${APPLE_PASSWORD:-}" && -n "${APPLE_TEAM_ID:-}" ]]; then
  echo "Notarizing $APP_NAME"
  ditto -c -k --keepParent "$APP_DIR" "$ARTIFACTS_DIR/Synapic.zip"
  xcrun notarytool submit "$ARTIFACTS_DIR/Synapic.zip" \
    --apple-id "$APPLE_ID" --password "$APPLE_PASSWORD" --team-id "$APPLE_TEAM_ID" --wait
  xcrun stapler staple "$APP_DIR"
  rm -f "$ARTIFACTS_DIR/Synapic.zip"
fi

DMG="$ARTIFACTS_DIR/Synapic-${RID}.dmg"
rm -f "$DMG"
create-dmg --volname "Synapic" --overwrite "$DMG" "$APP_DIR" 2>/dev/null \
  || hdiutil create -volname Synapic -srcfolder "$APP_DIR" -ov -format UDZO "$DMG"

echo "DMG created: $DMG"
ls -lh "$ARTIFACTS_DIR"
