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

# ISCC anchors relative paths to the script's own directory (not the CWD),
# so hand it an absolute artifacts path; create the dir if missing.
$ArtifactsDir = (New-Item -ItemType Directory -Force $ArtifactsDir).FullName

$iscc = Get-Command ISCC.exe -ErrorAction SilentlyContinue
if (-not $iscc) {
    $candidates = @(
        "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
        "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
    )
    foreach ($c in $candidates) {
        if (Test-Path $c) { $iscc = $c; break }
    }
}
if (-not $iscc) { throw "Inno Setup 6 (ISCC.exe) not found - install from https://jrsoftware.org/isinfo.php" }

# Stage the .NET 10 Desktop Runtime installer next to the payload so the Inno
# setup bundles it as an offline prerequisite. The app is published
# framework-dependent on Windows to keep the bundle small; when this file is
# absent the installer falls back to a live download at install time.
$runtimeExe = "windowsdesktop-runtime-win-x64.exe"
$runtimePath = Join-Path $ArtifactsDir $runtimeExe
if (-not (Test-Path $runtimePath)) {
    Write-Host "Fetching .NET 10 Desktop Runtime installer (offline prerequisite)..."
    $meta = Invoke-RestMethod -Uri 'https://builds.dotnet.microsoft.com/dotnet/release-metadata/10.0/releases.json'
    $file = $meta.releases | ForEach-Object { $_.windowsdesktop } |
        Where-Object { $_ } | ForEach-Object { $_.files } |
        Where-Object { $_.rid -eq 'win-x64' -and $_.name -eq $runtimeExe } |
        Select-Object -First 1
    if (-not $file) { throw "Runtime installer not found in .NET release metadata" }
    Invoke-WebRequest -Uri $file.url -OutFile $runtimePath
    $size = (Get-Item $runtimePath).Length
    if ($size -lt 10MB) { throw "Staged runtime installer looks invalid ($size bytes) - expected the full ~60 MB setup" }
    Write-Host "Staged: $runtimePath ($([math]::Round($size / 1MB, 1)) MB)"
}

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
