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
build/fetch-python.ps1|.sh <rid>     # python-build-standalone 3.11.16 (pinned)
build/install-python-deps.ps1|.sh <rid>   # pip install -r requirements.txt (CPU torch)
build/build-sidecar.ps1|.sh <rid>    # PyInstaller → synapic-inference(.exe) + variant guard
dotnet publish src/Synapic.Avalonia -c Release -r <rid> --self-contained
build/package-windows.ps1            # Inno Setup 6 → Synapic-Setup-x64.exe
build/package-linux.sh               # linuxdeploy → Synapic-x86_64.AppImage
build/package-macos.sh <rid>         # codesign + notarytool + create-dmg
```

`build-server.ps1|.sh` chains the first three steps; the app's **Build Server**
button and CI both call it.

## Sidecar variants: CPU and CUDA

CUDA is not a separate program. It is the same sidecar built against CUDA torch
wheels, and it exists only on Windows (`InferenceSidecarService.BuildableRids`
returns `win-x64` + `win-x64-cuda`, `PreferredRid()` otherwise). The RID selects
the wheel index and nothing else:

| RID | torch wheels | Reference bundle size |
|-----|--------------|----------------------|
| `win-x64` | `download.pytorch.org/whl/cpu` | ~225 MB |
| `linux-x64`, `osx-arm64` | `download.pytorch.org/whl/cpu` | ~225–270 MB |
| `win-x64-cuda` | `download.pytorch.org/whl/cu126` | ~2.7 GB |

`install-python-deps.ps1` picks the index from the `-cuda` suffix;
`install-python-deps.sh` **refuses** a `-cuda` RID (CUDA is Windows-only, and
quietly installing CPU wheels into a directory that later gets packaged as
"CUDA" is exactly the failure the guard below exists to prevent).

Because the RID is an input rather than proof, `build-sidecar.ps1|.sh` runs
`build/check-sidecar-variant.py` after PyInstaller. It opens the packaged
archive with PyInstaller's `CArchiveReader` and asserts the payload matches the
RID: `torch/lib/torch_cuda.dll` plus the CUDA runtime DLLs for `-cuda`, and no
CUDA runtime binaries at all inside a CPU bundle. The variants are otherwise
indistinguishable — a wrong-wheels build still produces a valid executable under
the expected name — and the mistake would only surface on a user's GPU machine
after a 2.7 GB download.

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
  C#). UPX is **off** in `synapic-inference.spec` — compressing a 2.7 GB CUDA
  bundle is slow, AV-triggering, and pointless for a sidecar that starts once —
  so a future size push has to come from excluding packages, not packing.
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
  linux-x64, osx-arm64) building and uploading bundles, the CUDA sidecar job,
  and the installer smoke test. osx-x64 (Intel Mac) is not built: torch dropped
  x86_64 macOS wheels after 2.2.2, so the sidecar cannot build there. PRs run the same but with a path filter (code/build
  changes only — doc-only PRs skip CI) and **without** the installer smoke
  job, which costs extra Windows runner minutes.
- **The CUDA job** (`sidecar-cuda`, windows-2022) builds `win-x64-cuda`: it
  asserts the *installed* torch is a CUDA 12.x build (a cheap 30-second check
  that runs before the ~20-minute PyInstaller pass), lets `build-sidecar.ps1`
  run the payload guard, boots the finished bundle through the real port-file
  handshake and polls `/health` with `SYNAPIC_DISABLE_AUTO_DOWNLOAD=1` (so CI
  never downloads the 860 MB default model), then uploads the executable with a
  SHA-256 for 7 days. GitHub-hosted runners have **no GPU**, so this proves the
  bundle boots with CUDA wheels installed — torch falls back to CPU — and can
  never prove GPU inference. It is skipped on PRs (add the `build-cuda` label to
  force it) because ~3 GB of wheels plus a ~2.7 GB artifact is real cost per run.
- **Size ceilings when publishing:** `actions/upload-artifact` accepts a 10 GB
  artifact, but a GitHub Release asset must be **under 2 GiB** (the *total* size
  of a release is not capped). The CPU sidecar (~216 MB) uploads as one file;
  the ~2.5 GB CUDA one cannot, so `build/split-release-asset.py` cuts it into
  sub-2 GiB byte ranges (`<name>.part1 …`) and drops a `reassemble-<name>.bat`
  next to them that rejoins the parts and prints the expected SHA-256. Parts are
  raw ranges rather than a multi-volume archive so reassembly needs nothing
  installed: `copy /b` on Windows, `cat` elsewhere. The script also deletes the
  oversized copy from the staging directory, because one invalid file there
  fails the entire release rather than just itself.
- `.github/workflows/release.yml` — triggered by a `v*` tag **or** manual
  dispatch. Both run the same 3-RID matrix (sidecar → app publish → installer)
  plus the CUDA sidecar job, then `github-release` merges the artifacts and
  publishes them. Every released sidecar is boot-smoke-tested first, so a bundle
  that builds but does not serve `/health` never reaches the releases page.

### What a release contains

| Asset | Notes |
|-------|-------|
| `Synapic-Setup-win-x64.exe` | Windows installer, app + CPU sidecar |
| `Synapic-osx-arm64.dmg` | macOS disk image, app + CPU sidecar |
| `Synapic-*.AppImage` | Linux AppImage, app + CPU sidecar |
| `synapic-inference-win-x64.exe` | standalone CPU server |
| `synapic-inference-linux-x64`, `synapic-inference-osx-arm64` | standalone CPU server |
| `synapic-inference-win-x64-cuda.exe.part1/.part2` | standalone CUDA server, split (2 GiB cap) |
| `reassemble-synapic-inference-win-x64-cuda.exe.bat` | rejoins the CUDA parts, verifies the hash |
| `SHA256SUMS.txt` | one manifest for every asset, generated in the publish job |

Standalone sidecars matter because they let someone swap the CPU server for the
CUDA one (or pick up a newer server) without reinstalling the app — and without
needing a source checkout to run **Build Server**. The app finds its server by
file name, so a downloaded executable has to be renamed to
`synapic-inference.exe` (`synapic-inference` on Linux/macOS) and placed next to
the app.

### Validating the release path without a tag

Tags cost a version number and cannot be re-run against a fixed commit easily,
so dispatch the workflow directly:

```bash
gh workflow run release.yml -f version=0.0.0-dryrun          # build only
gh workflow run release.yml -f version=1.2.3 -f publish=true  # cut the release
```

The dry run exercises every build, the smoke tests, the split and the checksum
step, and leaves the assets on the run page for inspection — it only skips the
`github-release` job.

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
