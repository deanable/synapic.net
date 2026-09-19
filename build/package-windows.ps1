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
