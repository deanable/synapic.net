# Package the Windows MSI and its Burn bundle with the WiX Toolset.
#
# Usage: package-windows-msi.ps1 [-Rid win-x64] [-AppVersion 1.0.0] [-SkipPublish]
#                                [-SkipSidecarCheck] [-ArtifactsDir <dir>] [-OutputDir <dir>]
#
# The Inno Setup path (package-windows.ps1) is what ships; this is the MSI
# equivalent, for the environments that want an MSI and a bundle rather than a
# single setup exe. Both are fed by the same publish directory, so the two
# installers cannot disagree about what the application is.
#
# Output (artifacts/msi by default):
#   Synapic-win-x64.msi               the package, installable on its own
#   Synapic-Setup-win-x64-msi.exe     the bundle: installs .NET if needed, then the MSI
#
# The -msi suffix on the bundle is what keeps it distinct from the Inno setup exe
# (Synapic-Setup-win-x64.exe) it sits next to on a release page.
[CmdletBinding()]
param(
    [string]$Rid = "win-x64",
    [string]$AppVersion = "1.0.0",
    [string]$ArtifactsDir = "artifacts/$Rid",
    [string]$OutputDir = "artifacts/msi",
    [switch]$SkipPublish,
    [switch]$SkipSidecarCheck
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot

# --- WiX ---------------------------------------------------------------------
# WiX is a dotnet tool, not an installed application: it lands on PATH for most
# shells but not all of them, so fall back to the per-user tool directory before
# giving up.
$wix = Get-Command wix.exe -ErrorAction SilentlyContinue
if (-not $wix) {
    $candidate = Join-Path $env:USERPROFILE ".dotnet/tools/wix.exe"
    if (Test-Path $candidate) { $wix = $candidate }
}
if (-not $wix) {
    throw "WiX Toolset not found - install it with: dotnet tool install --global wix --version 7.*"
}

# WiX v7 will not run until its Open Source Maintenance Fee EULA is accepted, and
# accepting it is a licensing acknowledgement rather than a build flag: it asserts
# that whoever runs this has satisfied the fee. Passing it here keeps that
# acknowledgement in the repository, where it is visible in review, instead of in
# per-user state on whichever machine happened to run `wix eula accept` first.
$eula = @("-acceptEula", "wix7")

# The extensions the sources need, added to the global WiX cache if they are not
# there already. Idempotent: `extension add` on an installed extension does
# nothing, and the cache is shared between users, not part of the repository.
$extensions = @(
    "WixToolset.Netfx.wixext",                 # .NET runtime detection, package and bundle
    "WixToolset.BootstrapperApplications.wixext"  # the bundle's own UI (WixStdBA)
)
foreach ($extension in $extensions) {
    & $wix extension add -g $extension @eula
    if ($LASTEXITCODE -ne 0) { throw "Could not add WiX extension $extension" }
}

# --- the payload -------------------------------------------------------------


# Rooted paths are used as they stand; relative ones are anchored to the
# repository, so this behaves the same wherever it is called from.
foreach ($name in @("ArtifactsDir", "OutputDir")) {
    $value = Get-Variable $name -ValueOnly
    if (-not [System.IO.Path]::IsPathRooted($value)) {
        Set-Variable $name -Value (Join-Path $repoRoot $value)
    }
}

if (-not $SkipPublish) {
    Write-Host "Publishing $Rid payload into $ArtifactsDir..."
    & dotnet publish (Join-Path $repoRoot "src/Synapic.Main/Synapic.Main.csproj") `
        -c Release -r $Rid --self-contained false -p:PublishSingleFile=false -o $ArtifactsDir
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE" }
}

# A payload that is not a publish would produce an installer that installs
# nothing useful, and MSBuild is happy to leave an output directory untouched.
foreach ($required in @("Synapic.exe", "Synapic.runtimeconfig.json")) {
    if (-not (Test-Path (Join-Path $ArtifactsDir $required))) {
        throw "$ArtifactsDir\$required is missing - that directory is not a Synapic publish. Run build/build-server.ps1 first, or let this script publish for you."
    }
}

# The sidecar is built by build/build-sidecar.ps1, not by `dotnet publish`, so it
# can be missing while the app payload looks complete. The Visual Studio project
# this replaces had exactly that hole and would package a 226 MB application with
# no inference engine and no complaint, which is why this is an error rather than
# a warning. -SkipSidecarCheck is for building the installer itself when the
# sidecar is not what you are working on.
if (-not $SkipSidecarCheck -and -not (Test-Path (Join-Path $ArtifactsDir "synapic-inference.exe"))) {
    throw "$ArtifactsDir\synapic-inference.exe is missing - the installer would ship without the inference sidecar. Run build/build-sidecar.ps1 -Rid $Rid, or pass -SkipSidecarCheck if you are only testing the installer itself."
}

# --- what the app needs to run ----------------------------------------------
# Read from the payload rather than hard-coded, so that moving the app to a newer
# target framework moves the installer's requirement with it. This is the check
# the bundle chains a runtime for and the package refuses to install without.
$runtimeConfig = Get-Content (Join-Path $ArtifactsDir "Synapic.runtimeconfig.json") -Raw | ConvertFrom-Json
$framework = $runtimeConfig.runtimeOptions.framework
if (-not $framework) { throw "Synapic.runtimeconfig.json has no runtimeOptions.framework - nothing to require at install time" }

# The DotNetCompatibilityCheck / DotNetCoreSearch runtime names. An Avalonia app
# resolves to "core"; the others are here so a future framework reference does not
# fail as a mystery.
switch ($framework.name) {
    "Microsoft.NETCore.App" { $runtimeType = "core" }
    "Microsoft.WindowsDesktop.App" { $runtimeType = "desktop" }
    "Microsoft.AspNetCore.App" { $runtimeType = "aspnet" }
    default { throw "The payload asks for $($framework.name), which this script cannot express as a runtime check. Add it to the switch here and to the enums documented in Synapic.Msi.wxs." }
}
$runtimeVersion = $framework.version
$runtimeMajor = ($runtimeVersion -split '\.')[0]
Write-Host "Payload requires $($framework.name) $runtimeVersion ($Rid)."

# --- the identities ---------------------------------------------------------
# Same arrangement as the Inno AppId guard: these are identities, and a changed
# UpgradeCode strands every existing install while a pinned ProductCode turns
# upgrades into repairs. Both are checked before anything is compiled - see
# check-installer-identities.py.
$guardPython = Join-Path $repoRoot "build/_python/$Rid/python/python.exe"
if (-not (Test-Path $guardPython)) {
    $onPath = Get-Command python -ErrorAction SilentlyContinue
    if (-not $onPath) { $onPath = Get-Command py -ErrorAction SilentlyContinue }
    if (-not $onPath) { throw "Python is required to run the UpgradeCode guard - run build/fetch-python.ps1 -Rid $Rid first" }
    $guardPython = $onPath.Source
}
& $guardPython (Join-Path $repoRoot "build/check-installer-identities.py")
if ($LASTEXITCODE -ne 0) {
    throw "Refusing to build: an installer identity no longer matches the shipped UpgradeCodes, or a ProductCode has been pinned."
}

# --- the runtime the bundle chains ------------------------------------------
# The same staged file the Inno installer bundles, staged by the same script.
$runtimePath = & (Join-Path $repoRoot "build/stage-dotnet-runtime.ps1") -ArtifactsDir $ArtifactsDir
if (-not (Test-Path $runtimePath)) { throw "The .NET runtime installer was not staged" }

# --- the package -------------------------------------------------------------
$ArtifactsDir = (New-Item -ItemType Directory -Force $ArtifactsDir).FullName
$OutputDir = (New-Item -ItemType Directory -Force $OutputDir).FullName

# RID-qualified like every other released asset, so a future arm64 MSI cannot
# silently overwrite this one on a release page.
$msi = Join-Path $OutputDir "Synapic-$Rid.msi"
Write-Host "Compiling $msi (version $AppVersion, x64)..."
& $wix build @eula -arch x64 `
    -ext WixToolset.Netfx.wixext `
    -d "PayloadDir=$ArtifactsDir" `
    -d "AppVersion=$AppVersion" `
    -d "RuntimeType=$runtimeType" `
    -d "RuntimeVersion=$runtimeVersion" `
    -o $msi `
    (Join-Path $repoRoot "build/wix/Synapic.Msi.wxs")
if ($LASTEXITCODE -ne 0) { throw "wix build failed for the MSI with exit code $LASTEXITCODE" }

# Sign the package before it is embedded in the bundle, not after: the bundle
# carries the MSI as a payload, so a signature added afterwards would only be on
# the copy on disk, and the one that actually gets installed would be unsigned.
$signtool = Get-Command signtool.exe -ErrorAction SilentlyContinue
if ($signtool -and $env:SIGNING_CERT_THUMBPRINT) {
    Write-Host "Signing $msi with certificate $env:SIGNING_CERT_THUMBPRINT"
    & signtool sign /sha1 $env:SIGNING_CERT_THUMBPRINT /tr http://timestamp.digicert.com /td sha256 /fd sha256 $msi
    if ($LASTEXITCODE -ne 0) { throw "signtool failed for the MSI with exit code $LASTEXITCODE" }
}

# --- the bundle --------------------------------------------------------------
$bundle = Join-Path $OutputDir "Synapic-Setup-$Rid-msi.exe"
Write-Host "Compiling $bundle..."
& $wix build @eula -arch x64 `
    -ext WixToolset.Netfx.wixext `
    -ext WixToolset.BootstrapperApplications.wixext `
    -d "MsiPath=$msi" `
    -d "RuntimePath=$runtimePath" `
    -d "AppVersion=$AppVersion" `
    -d "RuntimeType=$runtimeType" `
    -d "RuntimeVersion=$runtimeVersion" `
    -d "RuntimeMajor=$runtimeMajor" `
    -o $bundle `
    (Join-Path $repoRoot "build/wix/Synapic.Bundle.wxs")
if ($LASTEXITCODE -ne 0) { throw "wix build failed for the bundle with exit code $LASTEXITCODE" }

if ($signtool -and $env:SIGNING_CERT_THUMBPRINT) {
    Write-Host "Signing $bundle with certificate $env:SIGNING_CERT_THUMBPRINT"
    & signtool sign /sha1 $env:SIGNING_CERT_THUMBPRINT /tr http://timestamp.digicert.com /td sha256 /fd sha256 $bundle
    if ($LASTEXITCODE -ne 0) { throw "signtool failed for the bundle with exit code $LASTEXITCODE" }
}

# --- report ------------------------------------------------------------------
# `wix msi validate` runs the standard ICE checks, which catch authoring mistakes
# that still produce a working-looking package - a 32-bit component in a 64-bit
# directory, for instance, which is how the -arch x64 above was found. It is
# advisory: it warns about the same-version upgrade below and that is expected.
Write-Host "`nArtifacts:"
foreach ($file in @($msi, $bundle)) {
    Write-Host "  $file ($([math]::Round((Get-Item $file).Length / 1MB, 1)) MB)"
}
Write-Host "`nThe MSI is installable on its own and refuses to install without .NET $runtimeMajor. The bundle installs the runtime first and then the MSI."
# Advisory, not part of the build: it always reports ICE61 for the same-version
# upgrade above, which is expected, and a warning on every build trains people to
# ignore the checks that matter.
Write-Host "Check the package with: `"$wix`" msi validate `"$msi`" -acceptEula wix7"
