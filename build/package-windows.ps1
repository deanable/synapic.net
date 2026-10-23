# Package the Windows installer with Inno Setup 6.
# Usage: package-windows.ps1 [-Rid win-x64] [-AppVersion 1.0.0]
[CmdletBinding()]
param(
    [string]$Rid = "win-x64",
    [string]$AppVersion = "1.0.0",
    [string]$ArtifactsDir = "artifacts/$Rid",
    [string]$IssScript = "build/installer-windows.iss"
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot

# ISCC anchors relative paths to the script's own directory (not the CWD),
# so hand it an absolute artifacts path; create the dir if missing.
$ArtifactsDir = (New-Item -ItemType Directory -Force $ArtifactsDir).FullName

$iscc = Get-Command ISCC.exe -ErrorAction SilentlyContinue
if (-not $iscc) {
    $candidates = @(
        "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
        "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
        # Per-user install (/CURRENTUSER), which needs no elevation.
        (Join-Path $env:LOCALAPPDATA "Programs\Inno Setup 6\ISCC.exe")
    )
    foreach ($c in $candidates) {
        if (Test-Path $c) { $iscc = $c; break }
    }
}
if (-not $iscc) { throw "Inno Setup 6 (ISCC.exe) not found - install from https://jrsoftware.org/isinfo.php" }

# AppId is the uninstall identity, so changing it makes Windows treat this build
# as a different application: a second Add/Remove Programs entry, the previous
# version's files left on disk, and no upgrade path. Refuse to compile unless the
# script agrees with the AppId that has actually shipped - see
# build/check-installer-appid.py and build/installer-appid.txt.
$guardPython = Join-Path $PSScriptRoot "_python/$Rid/python/python.exe"
if (-not (Test-Path $guardPython)) {
    $onPath = Get-Command python -ErrorAction SilentlyContinue
    if (-not $onPath) { $onPath = Get-Command py -ErrorAction SilentlyContinue }
    if (-not $onPath) {
        throw "Python is required to run the AppId guard - run build/fetch-python.ps1 -Rid $Rid first"
    }
    $guardPython = $onPath.Source
}
& $guardPython (Join-Path $PSScriptRoot "check-installer-appid.py")
if ($LASTEXITCODE -ne 0) {
    throw "Refusing to build: build/installer-windows.iss no longer matches the shipped AppId."
}

# Both installers glob the same payload folder, so their exclusion lists have to
# agree - see check-installer-payloads.py. The MSI path runs the same guard, so
# whichever script is used, the two manifests are compared before compiling.
& $guardPython (Join-Path $PSScriptRoot "check-installer-payloads.py")
if ($LASTEXITCODE -ne 0) {
    throw "Refusing to build: the Inno and MSI payload definitions have drifted apart."
}

# D-11-28: the background indexer publishes into the SAME payload folder as the
# app, not a subfolder. It finds synapic-inference.exe beside itself, and the
# installer already harvests the whole directory, so one copy of the 226 MB
# sidecar serves both. Publishing it here (after the app publish CI performs)
# keeps the app's shared Microsoft.Extensions/Serilog assemblies authoritative
# and puts the service in every installer built from this script.
Write-Host "Publishing the background indexer into $ArtifactsDir..."
& dotnet publish (Join-Path $repoRoot "src/Synapic.Indexer/Synapic.Indexer.csproj") `
    -c Release -r $Rid --self-contained false -p:PublishSingleFile=false -o $ArtifactsDir
if ($LASTEXITCODE -ne 0) { throw "dotnet publish of the indexer failed with exit code $LASTEXITCODE" }

# A payload missing the service would produce an installer that silently omits
# the feature this release is about, so every required file is checked by name.
foreach ($required in @("Synapic.Indexer.exe", "Synapic.Core.dll", "synapic-inference.exe")) {
    if (-not (Test-Path (Join-Path $ArtifactsDir $required))) {
        throw "$ArtifactsDir\$required is missing - the installer would ship without the background indexer. Check the indexer publish above and build/build-sidecar.ps1 for the sidecar."
    }
}

# Stage the .NET 10 Desktop Runtime installer next to the payload so the Inno
# setup bundles it as an offline prerequisite. The app is published
# framework-dependent on Windows to keep the bundle small; the .iss falls back to
# a live download at install time if the file turns out to be missing.
#
# Shared with the WiX path (package-windows-msi.ps1), which chains the same file
# into its bundle: one script decides which build of the runtime ships, so the two
# installers cannot end up bundling different ones.
& (Join-Path $PSScriptRoot "stage-dotnet-runtime.ps1") -ArtifactsDir $ArtifactsDir | Out-Null

Write-Host "Compiling installer: $IssScript (version $AppVersion, RID $Rid)"
& $iscc "/DAppVersion=$AppVersion" "/DRid=$Rid" "/DArtifactsDir=$ArtifactsDir" $IssScript
if ($LASTEXITCODE -ne 0) { throw "ISCC failed with exit code $LASTEXITCODE" }

$installer = Join-Path $ArtifactsDir "Synapic-Setup-$Rid.exe"
if (Test-Path $installer) {
    Write-Host "Installer created: $installer"
    # Optional EV code signing (signtool from Windows SDK).
    $signtool = Get-Command signtool.exe -ErrorAction SilentlyContinue
    if ($signtool -and $env:SIGNING_CERT_THUMBPRINT) {
        Write-Host "Signing installer with certificate $env:SIGNING_CERT_THUMBPRINT"
        & signtool sign /sha1 $env:SIGNING_CERT_THUMBPRINT /tr http://timestamp.digicert.com /td sha256 /fd sha256 $installer
    }
}
