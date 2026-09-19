# Packaging

Per-platform single-bundle distribution (spec §7): the .NET app plus the
PyInstaller sidecar, shipped together. macOS/Linux payloads are
self-contained; **Windows is framework-dependent** to keep the installer small
(see the prerequisite note below).

## Layout

```
artifacts/<rid>/
├── Synapic(.exe)            # self-contained Avalonia publish
├── synapic-inference(.exe)  # PyInstaller sidecar
├── *.dll                    # runtime deps
└── installer output         # .exe / .AppImage / .dmg
```

## Build steps (any RID)

```
build/fetch-python.ps1|.sh <rid>     # python-build-standalone 3.11.9 (pinned)
build/install-python-deps.ps1|.sh <rid>   # pip install -r requirements.txt (CPU torch)
build/build-sidecar.ps1|.sh <rid>    # PyInstaller → synapic-inference(.exe)
dotnet publish src/Synapic.Avalonia -c Release -r <rid> --self-contained
build/package-windows.ps1            # Inno Setup 6 → Synapic-Setup-x64.exe
build/package-linux.sh               # linuxdeploy → Synapic-x86_64.AppImage
build/package-macos.sh <rid>         # codesign + notarytool + create-dmg
```

## Notes & mitigations (spec §9)

- **Windows .NET prerequisite:** the Windows payload is published
  framework-dependent; `package-windows.ps1` stages the official .NET 10
  Desktop Runtime installer next to the payload and `installer-windows.iss`
  bundles it as an offline prerequisite. At install time a missing runtime is
  installed silently (`/install /quiet /norestart`); with no bundled copy the
  installer downloads it live. The app's own startup check
  (`DotNetRuntimeCheckService`) is the second line of defense.
- **Bundle size:** the sidecar excludes `cv2`, `imagehash`, `faiss`,
  `sentence-transformers`, `customtkinter` (dedup and metadata writing moved to
  C#). UPX is on; disable in `synapic-inference.spec` if AV heuristics flag it.
- **Lite vs full installers:** "full" bakes `LiquidAI/LFM2.5-VL-1.6B` weights
  into the sidecar's model cache at build time (offline-first). "lite" ships
  without weights; the app downloads on first run via `POST /models/download`.
- **Windows signing:** EV cert via `signtool` (set `SIGNING_CERT_THUMBPRINT`).
- **macOS:** sign every `.dylib`/`.so` in the bundle before the app bundle,
  hardened runtime (`--options runtime`), notarize with `notarytool --wait`,
  staple. Requires `MACOS_SIGNING_IDENTITY` (+ Apple ID secrets to notarize).
- **Linux:** optional GPG signature of the AppImage via `GPG_KEY_ID`.

## CI

- `.github/workflows/build.yml` — every push/PR: unit tests (pytest + xUnit),
  then a 4-RID matrix (win-x64, linux-x64, osx-x64, osx-arm64) building and
  uploading bundles.
- `.github/workflows/release.yml` — `v*` tags: same matrix, plus packaging,
  signing (when secrets are present), and a GitHub Release with all assets.
