# Packaging

Per-platform single-bundle distribution (spec §7): the .NET app plus the
PyInstaller sidecar, shipped together. macOS/Linux payloads are
self-contained; **Windows is framework-dependent** to keep the installer small
(see the prerequisite note below).

## Layout

```
artifacts/<rid>/
├── Synapic(.exe)            # Avalonia publish (framework-dependent on Windows)
├── synapic-inference(.exe)  # PyInstaller sidecar
├── help/*.html              # user help topics, non-Windows RIDs (from docs/help)
│                           # Windows: Synapic.chm is inside Synapic.dll
├── *.dll                    # runtime deps
└── installer output         # .exe / .AppImage / .dmg
```

## Build steps (any RID)

```
build/fetch-python.ps1|.sh <rid>     # python-build-standalone 3.11.16 (pinned)
build/install-python-deps.ps1|.sh <rid>   # pip install -r requirements.txt (CPU torch)
build/build-sidecar.ps1|.sh <rid>    # PyInstaller → synapic-inference(.exe) + variant guard
build/install-html-help-workshop.ps1 # Windows only: hhc.exe, from build/vendor (hash-checked)
docs/help/build-chm.ps1              # Windows only: docs/help → Synapic.chm + SHA-256 manifest
dotnet publish src/Synapic.Avalonia -c Release -r <rid> --self-contained
build/package-windows.ps1            # Inno Setup 6 → Synapic-Setup-x64.exe
build/package-windows-msi.ps1        # WiX → Synapic-win-x64.msi + Synapic-Setup-win-x64-msi.exe
build/package-linux.sh               # linuxdeploy → Synapic-x86_64.AppImage
build/package-macos.sh <rid>         # codesign + notarytool + create-dmg
```

`build-server.ps1|.sh` chains the first three steps; the app's **Build Server**
button and CI both call it.

### Building an installer

Nothing in the solution packages by itself, and none of it is built by MSBuild:
the installers are separate command-line steps. On Windows there are two
installer definitions - three artefacts, because the MSI ships with a bundle
that installs the .NET runtime for it - all fed by the same framework-dependent
win-x64 publish:

| Installer | Definition | Produced by | Output |
|-----------|-----------|-------------|--------|
| Inno Setup (shipped) | `build/installer-windows.iss` | `build/package-windows.ps1` | `artifacts/win-x64/Synapic-Setup-win-x64.exe` |
| MSI | `build/wix/Synapic.Msi.wxs` | `build/package-windows-msi.ps1` | `artifacts/msi/Synapic-win-x64.msi` |
| Bundle | `build/wix/Synapic.Bundle.wxs` | `build/package-windows-msi.ps1` | `artifacts/msi/Synapic-Setup-win-x64-msi.exe` |

`package-windows.ps1` is the path CI runs: it takes the win-x64 publish in
`artifacts/<rid>`, runs the AppId guard, stages the .NET 10 desktop runtime next
to the payload and compiles the `.iss`. It deliberately does not publish - the
publish is a separate step in CI - so `artifacts/<rid>` has to be a real publish
before either packaging script runs.

### The MSI and the bundle

`build/wix` holds two WiX Toolset sources and the identity file they share:

| Source | What it is |
|--------|-----------|
| `Synapic.Msi.wxs` | the Windows Installer package |
| `Synapic.Bundle.wxs` | a Burn bundle: the .NET runtime first, then the MSI |
| `Synapic.Identities.wxi` | the two `UpgradeCode`s both sources include |

One command builds both artefacts, from the repository root:

```powershell
build/package-windows-msi.ps1                  # publish, then MSI, then bundle
build/package-windows-msi.ps1 -SkipPublish     # reuse the publish already in artifacts/win-x64
```

It publishes `artifacts/win-x64` with `-r win-x64 --self-contained false
-p:PublishSingleFile=false` - the flags the release uses - then reads the runtime
the payload asks for out of `Synapic.runtimeconfig.json`, stages the .NET runtime
installer with the same `build/stage-dotnet-runtime.ps1` the Inno path uses, runs
the installer identity guard, and compiles the MSI and then the bundle.

**The payload is globbed, not listed.** `<Files Include="…\**">` replaces what
used to be a hand-maintained static file list, so a dependency added to the app
appears in the installer with no regeneration step. It also closed a hole: the
Visual Studio project never listed the 226 MB inference sidecar, so the MSI it
produced shipped an application with no inference engine and said nothing. The
two exclusions are the Inno installer's, word for word - `*.pdb` and the staged
runtime installer - because the two installers should not disagree about what the
payload is. The packaging script also refuses to build if
`synapic-inference.exe` is not in the payload, for the same reason; the Visual
Studio path never had that.

Requires the WiX Toolset as a dotnet tool:
`dotnet tool install --global wix --version 7.0.0` - the same version the release
workflow installs, so a dev box and CI produce the same package. WiX v7 will not run until its
Open Source Maintenance Fee EULA is accepted, so the script passes
`-acceptEula wix7` on every invocation: the acknowledgement lives in the
repository, where it shows up in review, rather than in per-user state on
whichever machine happened to run `wix eula accept` first. The two extensions the
sources need are added to the global WiX cache by the script if they are missing.

#### The two identities, and the guard that keeps them straight

Windows Installer decides what a package *is* from two GUIDs with opposite
rules, and this is the pair worth understanding before editing either:

- `UpgradeCode` (in `Synapic.Identities.wxi`, recorded in
  `build/installer-identities.txt`) is the product **family** and must be the same
  in every version. It is what Windows matches a package against to decide
  whether it is upgrading something or installing a second copy. Change it and
  existing installs stop being recognised: a second entry in Add/Remove Programs,
  the previous version's files left on disk, no upgrade path.
- `ProductCode` must **not** be set at all. It names one particular build, so it
  has to be new in every version, and WiX generates a fresh GUID per build while
  the attribute is left alone. Pin one and Windows treats the new package as the
  same product as the old, which turns the upgrade into a repair of what is
  already there: the old files stay and the version never moves.

Neither mistake fails a build, installs anything wrong, or shows up at all until
someone who has the previous version tries to move to this one - which is why
`build/check-installer-identities.py` refuses to compile anything unless
`Synapic.Identities.wxi` still matches the recorded values **and**
`Synapic.Msi.wxs` has not pinned a ProductCode. It also checks that each source
uses the define the guard reads (`UpgradeCode="$(var.MsiUpgradeCode)"`), because
a literal there would leave the guard validating a file the compiler ignores.

Rebuilding twice from unchanged sources is the property each rule rests on, and
it holds: the ProductCode changes (`{4A2A6366-…}` then `{D50BA417-…}`) and the
UpgradeCode does not (`{20752828-…}`).

#### x64

`-arch x64` on both `wix build` commands is what makes the package 64-bit: it
sets the template and marks the components so Windows Installer does not redirect
them into `Program Files (x86)` and the 32-bit registry view. Leaving it off
still produces a package that installs and looks right, which is why it is worth
checking rather than trusting - without it `wix msi validate` reports
`ICE80: This 32BitComponent … uses 64BitDirectory INSTALLFOLDER` for every one of
the seventy-odd components.

The application installs to `[ProgramFiles64Folder]\Synapic` - the same
directory the Inno installer uses, so a machine that has had either ends up in
the same place.

#### The help subfolder

Help topics have to land in `<app>\help` (`HelpService.TopicDirectories` checks
there first). Subdirectories in the `Files` include pattern become subdirectories
of the install folder, so nothing declares it and nothing can forget to: the MSI
ends up with a `help` directory under `Synapic` holding the 27 topics. The old
project file needed a hand-built child node with a particular type GUID and its
own `Property` to get the same result.

#### The .NET 10 runtime requirement

The Windows payload is framework-dependent, so an installer that lands it on a
machine with no .NET runtime leaves behind an application that cannot start. The
two artefacts split that responsibility:

- **The bundle installs it.** `Synapic.Bundle.wxs` chains the staged
  `windowsdesktop-runtime-win-x64.exe` ahead of the MSI with `/install /quiet
  /norestart`, the same arguments the Inno script runs, and maps exit codes 3010
  and 1641 to a scheduled reboot and 1638 to success - all three mean the runtime
  installed and only the exit code is not zero, so the default would report a
  failure on a machine that is fine. `netfx:DotNetCoreSearch` sets the detection
  variable, so a machine that already has the framework skips the package
  entirely instead of reinstalling 60 MB of runtime. The package is
  `Permanent="yes"`: .NET is shared with other applications, so uninstalling
  Synapic leaves it alone, exactly as the Inno installer does.
- **The MSI refuses without it.** Administrators deploy MSIs directly - group
  policy, Intune, SCCM - and those deployments never see the bundle, so
  `Synapic.Msi.wxs` carries a check of its own. `netfx:DotNetCompatibilityCheck`
  runs a custom action before `LaunchConditions` in both the UI and execute
  sequences, and a machine without the runtime stops with *"Synapic needs the
  .NET 10.0.0 runtime, which is not installed. Download it from
  https://dotnet.microsoft.com/download/dotnet/10.0 …"*. That message only
  exists while the package can still run its own custom action, so the app
  carries the same link for the cases the installer never sees - see *Notes &
  mitigations* below.

Neither artefact writes the requirement down: the packaging script reads the
framework and version out of the payload's `Synapic.runtimeconfig.json` and
passes them in as `-d RuntimeType=core -d RuntimeVersion=10.0.0`, so moving the
app to a newer target framework moves the installer's requirement with it.

`core` is `Microsoft.NETCore.App`, which is what an Avalonia app asks for - it
references no desktop framework - and the Windows Desktop Runtime redistributable
installs the core framework too. Requiring core therefore never refuses a machine
where the app would actually have run, and it is what the bundle's detection
variable tests as well. The Inno script's own test is looser still: any host
reporting major version 10 or later, whatever runtime put it there.

The app's own check asks a narrower question - is the **desktop** framework
installed - and answers it from the runtime's layout on disk
(`%ProgramFiles%\dotnet\shared\Microsoft.WindowsDesktop.App\<version>`, plus
`DOTNET_ROOT`) with `dotnet --list-runtimes` as the fallback. It deliberately
does **not** use the documented registry key
`HKLM\SOFTWARE\dotnet\Setup\InstalledVersions\x64\sharedhost`: that value is the
**host** version, and the two drift. A machine with the 10.0.12 desktop runtime
and no 10 host reports `9.0.20` there, and the reverse - a 10.x host with the
desktop framework removed - reports 10.x. The second case is the one that
matters: trusting the key made the check announce *"no install needed"* on a
machine that was missing exactly what the installers require, and the download
link was never offered.

#### Checking a package without installing it

`wix msi validate` runs the standard ICE checks against the built package and is
the quickest way to catch authoring mistakes - the missing `-arch x64` was found
with it:

```powershell
wix msi validate artifacts/msi/Synapic-win-x64.msi
```

It reports one warning by design: `ICE61: This product should remove only older
versions of itself`. That comes from `AllowSameVersionUpgrades` on
`MajorUpgrade`, which is there because the product version stays at `1.0.0`
between releases - without it a rebuild at the same version installs *beside*
the existing copy rather than over it.

For the contents, the Windows Installer COM API reads the MSI directly
(PowerShell; keep the table and column names in backticks):

```powershell
$i = New-Object -ComObject WindowsInstaller.Installer
$db = $i.OpenDatabase((Resolve-Path 'artifacts\msi\Synapic-win-x64.msi'), 0)
$v = $db.OpenView('SELECT `Directory`,`Directory_Parent`,`DefaultDir` FROM `Directory`'); $v.Execute()
while ($r = $v.Fetch()) { "$($r.StringData(1)) <- $($r.StringData(2)) [$($r.StringData(3))]" }
```

A healthy package has 75 `File` rows - 46 application files, 27 help topics,
`synapic-inference.exe` and one stray `smoke.log` (see the limitations) - a
`Synapic | help` directory under the `ProgramFiles64Folder` row, and two
`LaunchCondition` rows: the runtime check above, plus WiX's own
`NOT WIX_DOWNGRADE_DETECTED` from `MajorUpgrade`. Long file names appear in the
`File` table in the `shortname|longname` form Windows Installer uses, so search
for the long half.

`wix burn extract` lists what a bundle actually carries:

```powershell
wix burn extract -o artifacts/msi/bundle artifacts/msi/Synapic-Setup-win-x64-msi.exe
```

Two payloads of about 60 MB and 232 MB: the runtime installer and the MSI, in
that order.

#### Limitations of the MSI path

- **The release build of it has never succeeded.** The release workflow does
  produce the MSI and the bundle - it installs the WiX tool and runs
  `package-windows-msi.ps1` for the Windows leg - but it is wrapped in
  `continue-on-error`, so a failure leaves the release without its MSI rather
  than failing outright. Nothing has built them on a runner yet; the first
  successful run is the point at which that guard can be dropped.
- **`smoke.log` ships.** It is a leftover from a manual sidecar smoke run sitting
  in `artifacts/win-x64` (uvicorn's startup output, not anything a script writes
  there), and both installers glob that directory, so both carry it into the
  install folder. Excluding it in one installer only would be worse - the two
  would disagree about what the payload is - so it wants fixing where it lives.
- **Nothing is signed.** The MSI and the bundle are produced unsigned unless
  `SIGNING_CERT_THUMBPRINT` is set, so SmartScreen warns on first run. When it is
  set, the script signs the MSI *before* the bundle is built, so the copy inside
  the bundle is signed too.

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
  (`DotNetRuntimeCheckService`) is the second line of defense. The MSI path
  covers the same ground from the other direction: the bundle installs the
  runtime before the package runs, and the package itself refuses to install
  without one - see *The MSI and the bundle* above.
- **The app hands over the download link.** Neither installer can finish this
  job from inside the app: the Inno prerequisite is handled before the app ever
  starts, the MSI refuses with a message and stops, and once the app is running
  on a machine whose runtime has since gone missing, neither installer is there
  any more. So the startup check does the last step itself - when the silent
  install cannot be completed (no network or a blocked proxy, a declined
  elevation prompt, or a desktop framework that is absent or older than 10), it
  writes
  `https://dotnet.microsoft.com/download/dotnet/10.0` into the log and raises
  `RuntimeUnavailable`, which shows a dialog offering that page. The URL and the
  minimum version the installers enforce are derived from the same
  `MinimumVersion`, so a target-framework bump moves all of them together.
- **Bundle size:** the sidecar excludes `cv2`, `imagehash`, `faiss`,
  `sentence-transformers`, `customtkinter` (dedup and metadata writing moved to
  C#). UPX is **off** in `synapic-inference.spec` — compressing a 2.7 GB CUDA
  bundle is slow, AV-triggering, and pointless for a sidecar that starts once —
  so a future size push has to come from excluding packages, not packing.
- **Lite vs full installers:** the shipped installer is lite - app plus CPU
  server, no weights - and should stay that way. "Bundling the model" below has
  the sizes, the 2 GiB release-asset ceiling, and the shape that serves offline
  installs without growing everyone's download.
- **User help payload:** the app opens the help itself (toolbar **Help** and
  <kbd>F1</kbd> — see `docs/help/README.md`), so the payload ships with every
  bundle, and it is different per platform.
  - **Windows:** the compiled `Synapic.chm` and its `help-payload.json` SHA-256
    manifest are **embedded resources** in `Synapic.dll` (`LogicalName`
    `Synapic.Help.*`), not files beside the binary. Nothing in the install
    directory can be deleted or half-copied to break the help, and
    `HelpService` refuses to open the bytes if they do not match the recorded
    hash — which is a damaged install to be reported, not substituted.
  - **Linux and macOS:** `docs/help/*.html` and `help.css` copied into `help/`
    next to the binary, opened in the default browser. There is no `.chm`
    viewer on either platform, so the compiled file would be dead weight.
  The Windows CI legs run `build/install-html-help-workshop.ps1` and then
  `build-chm.ps1` **without** `-AllowMissingCompiler`, because the `.chm` is the
  whole of the Windows help now: a Windows publish without one ships no help at
  all, so it fails the build. The compiler has to be installed explicitly
  because the `hhc.exe` in GitHub's windows-2022 Windows SDK exits 0 having
  written nothing. It is installed from the installer **vendored in the
  repository** (`build/vendor/html-help-workshop/htmlhelp.exe`, hash-checked
  before it is run) rather than through chocolatey or a download, so the
  release does not depend on a third-party package manager, or on the Internet
  Archive still serving the only surviving copy of a tool Microsoft stopped
  linking to in 2009. `build-chm.ps1` then proves its own output (written this
  run, at least 4 KB, no `HHC****` diagnostics) and writes the manifest the app
  checks against.
  `docs/help/check-help.py` runs
  in the Linux test job, so a dead link fails a PR rather than shipping.
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
- **The installer smoke test** (`installer-smoke`, windows-2022, main pushes
  only) proves more than "the installer produces a working install". It strips
  the machine's runtimes with `dotnet-core-uninstall`, runs
  `Synapic-Setup-win-x64.exe` silently - which has to put the desktop runtime
  back, because the setup bundles it - launches the installed app and greps its
  log for `[runtime]`.

  Then it exercises the app's own recovery path, which needs a machine that is
  missing the runtime while the app can still start. The payload is
  framework-dependent, so **only** the desktop framework goes: the step moves
  `dotnet\shared\Microsoft.WindowsDesktop.App` aside - the folder the check
  reads, and what a real removal leaves behind - and asserts both halves of that
  state (no desktop 10.x, `Microsoft.NETCore.App 10.x` still there) before
  launching the app. It blocks the app's own repair (a hosts entry for
  `builds.dotnet.microsoft.com` plus a dead `HTTPS_PROXY`) and reads the URL out
  of the dialog's own text box with UI Automation, comparing it against
  `https://dotnet.microsoft.com/download/dotnet/10.0`. Reading the dialog rather
  than the log is the point: a log line would also be there if the dialog were
  mis-bound or never shown. Folder and hosts file are restored in a `finally`, so
  the step leaves the runner as it found it.

  Two things it does not prove, both reported in the run itself. The strip does
  not actually remove the desktop runtime on this image - the copies it sees
  belong to SDKs, so the next step prints *"Desktop runtime still present —
  prerequisite path will NOT be exercised"* and the Inno prerequisite half stays
  unproven (the `.msi` this step downloads is cli-lab's current asset; it used to
  ask for a `.zip` that no longer exists, which is why the strip did nothing at
  all). And a runner's session is assumed to let UI Automation see the app's
  windows.
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
  The Windows leg also builds the MSI and the bundle, by installing the WiX
  tool and running `package-windows-msi.ps1` with `-SkipPublish` - the step runs
  *after* the Inno installer has been uploaded, and is wrapped in
  `continue-on-error` so a failure in this newer path cannot cost a release the
  installer that ships. It adds roughly 520 MB to a release: a 232 MB package
  plus a 290 MB bundle that contains it, because the inference server is inside
  both. Only the release workflow builds them; `build.yml` does not.

### What a release contains

| Asset | Notes |
|-------|-------|
| `Synapic-Setup-win-x64.exe` | Windows installer, app + CPU sidecar |
| `Synapic-Setup-win-x64-msi.exe` | Windows installer, MSI path: installs the .NET 10 runtime, then the package |
| `Synapic-win-x64.msi` | that same package on its own, for GPO/Intune/SCCM |
| `Synapic-osx-arm64.dmg` | macOS disk image, app + CPU sidecar |
| ~~`Synapic-*.AppImage`~~ | **not built** — see the known gap below |
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

**Linux installer unverified.** The AppImage has never been produced: the first
run died at the last step with `ERROR: Could not find icon executable for Icon
entry: synapic`, because `linuxdeploy` requires an app icon and the repository
shipped no image assets. `assets/icons/Icon.png` — a 256x256 PNG taken from the
app icon — is now handed to `linuxdeploy` with `--icon-file`, and
`package-linux.sh` fails fast when it is missing. No Linux dry run has exercised
that yet, so the step stays **guarded rather than fatal**: a failure emits a
`::warning::` annotation and the release continues, publishing
`synapic-inference-linux-x64` without an installer. Drop the guard once a dry run
reports `AppImage built` — and note that a dispatch builds the workflow as it
exists on `origin/main`, so the icon fix has to be pushed before that run can
prove anything.

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

## Bundling the model: keep the installer lite

The installer ships the app and the CPU server. It does not ship weights, and
the alternatives are worse, because the pieces have very different sizes and
only two of them are obligations:

| Piece | Size | In the installer? |
|-------|------|-------------------|
| App + .NET Desktop Runtime prerequisite | bundled; Windows is framework-dependent | **yes** - nothing runs without them |
| CPU sidecar | 216 MB standalone (226,782,812 bytes) | **yes** - building it is a developer-only path |
| `LiquidAI/LFM2.5-VL-450M` weights (default) | ~860 MB download, incompressible | no - first run, in the background |
| `LiquidAI/LFM2.5-VL-1.6B` weights (optional) | ~2 GB | no |

`Synapic-Setup-win-x64.exe` as released in v0.1.0 is **286 MB**
(299,709,667 bytes), and `Synapic-osx-arm64.dmg` is 171 MB. That is the
payoff of publishing Windows framework-dependent and letting the sidecar fetch
its own model.

**"Barebones that builds the sidecar" is not a shipping option.** Building the
sidecar needs a source checkout, `build/fetch-python.ps1`, ~2.5 GB of pinned
torch wheels, PyInstaller and about 20 minutes of CPU. That is the developer
path - `build-server.ps1`, the app's **Build Server** button, or **Update** on
a built row - and it only works for someone who already has the repository.
Someone who is handed an installer, or the standalone CPU server, never
compiles anything.

**Baking the default weights works, but should not be the default.** What it
buys: a first run that needs no network, which is real value for an on-prem
Daminion install that cannot reach huggingface.co. What it costs:

- ~860 MB of weights in an installer everyone downloads, and safetensors do
  not compress - LZMA2/max moves the setup exe from 286 MB to roughly 1.15 GB;
- every release job re-downloads and re-freezes them, and each installer pins
  one model revision, so a newer model means a new 1.15 GB installer instead of
  a download inside the app;
- a hard ceiling: a GitHub Release asset must be **under 2 GiB**. The 450M
  variant still fits (~1.15 GB), but adding `LFM2.5-VL-1.6B` (~2 GB) does not -
  that asset would need the split-and-reassemble treatment the CUDA server uses
  (`<name>.part1/.part2` plus `reassemble-*.bat`), and Inno Setup cannot install
  from split parts: the user has to run the `.bat` first, so it is no longer a
  one-file installer.

If offline installs become a requirement, the cheap shape is a second release
asset rather than a second installer: publish the Hugging Face cache for the
default model as `Synapic-models-lfm2.5-vl-450m.zip` and document unpacking it
into `%LOCALAPPDATA%\Synapic\models` - the sidecar's `HF_HOME`, fixed by
`InferenceSidecarService.ModelsRoot()` (`~/.cache/synapic/models` elsewhere) -
on a machine with network access, then copying it to the offline one. One
installer, a swappable model revision, no new packaging job. The next step up,
if admins object to a second download, is an optional
`Synapic-Setup-win-x64-full.exe` that drops those same files into
`{localappdata}\Synapic\models` as `[Files]` - the directory the uninstaller
already removes (`[UninstallDelete]`) - while the normal installer stays 286 MB.

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
hex-valid GUID. Inno treats AppId as an opaque identifier, so this works — but it
must **never change again**: v0.1.0 shipped with it, and a different value makes
Windows install the two side by side with separate uninstall entries and no
upgrade path. Normalizing it to a real GUID was only safe before that release,
so it is no longer an option.

`build/check-installer-appid.py` enforces that. `package-windows.ps1` runs it
before ISCC and refuses to compile when `installer-windows.iss` disagrees with
`build/installer-appid.txt` — the record of the AppId that has actually shipped.
Changing the AppId deliberately therefore means editing that record in the same
commit, which is what puts the decision in front of a reviewer, and it means
telling users they have to uninstall the previous version first.
