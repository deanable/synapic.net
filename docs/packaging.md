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

- `.github/workflows/build.yml` — pushes to `main` and manual dispatch run
  everything: unit tests (pytest + xUnit), a 3-RID matrix (win-x64,
  linux-x64, osx-arm64) building and uploading bundles, and the installer
  smoke test. osx-x64 (Intel Mac) is not built: torch dropped x86_64 macOS
  wheels after 2.2.2, so the sidecar cannot build there. PRs run the same but with a path filter (code/build
  changes only — doc-only PRs skip CI) and **without** the installer smoke
  job, which costs extra Windows runner minutes.
- `.github/workflows/release.yml` — `v*` tags: same matrix, plus packaging,
  signing (when secrets are present), and a GitHub Release with all assets.

## Manual test: upgrade path (same AppId)

The installer uses a fixed AppId (`build/installer-windows.iss`), so
installing a newer build over an older one is an **in-place upgrade**: Inno
reuses the previous `{app}` directory, uninstaller, and Add/Remove Programs
entry. Verify with two builds before every release:

1. Build and set aside the old installer:
   `./build/package-windows.ps1 -AppVersion 1.0.0`, then copy
   `artifacts/win-x64/Synapic-Setup-win-x64.exe` aside (e.g. to
   `Synapic-Setup-1.0.0.exe`) — the output filename does not include the
   version, so the second build would overwrite it.
2. Rebuild with the new version: `./build/package-windows.ps1 -AppVersion 1.0.1`.
3. Run the 1.0.0 setup, launch the app once (creates user data), quit it.
   Add/Remove Programs should show Synapic 1.0.0 (registry
   `HKLM\...\Uninstall\{8A7C2C31-...}_is1` → `DisplayVersion`).
4. Close the app, then run the 1.0.1 setup (GUI or
   `/VERYSILENT /NORESTART`) — setup prompts to close a running app
   otherwise (CloseApplications).

Checklist:

- [ ] No second Add/Remove Programs entry; `DisplayVersion` is now 1.0.1
- [ ] Install directory unchanged; app launches
- [ ] User data survived the upgrade: `%APPDATA%\Synapic\config.json`, logs
- [ ] Desktop/group shortcuts still point at the same `{app}`
- [ ] Files that 1.0.0 shipped but 1.0.1 does not remain until uninstall
      (Inno does not diff old installs) — add explicit cleanup only if one
      is ever harmful
- [ ] Uninstalling afterwards empties `{app}` (including the model cache via
      `[UninstallDelete]` `{localappdata}\Synapic\models`) but keeps
      `%APPDATA%\Synapic` user data

Caveat: the AppId string `{8A7C2C31-5E0D-4B21-9C4F-SYNAPICNET01}` is not a
hex-valid GUID. Inno treats AppId as an opaque identifier, so this works —
but it must **never change after the first public release**, or machines get
two parallel installs with separate uninstall entries. Normalizing it to a
real GUID is only safe before the first release.
