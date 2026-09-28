# Stage the official .NET 10 Desktop Runtime installer next to a Windows payload.
#
# Both Windows installers bundle this one file: the Inno Setup script as an
# offline prerequisite, and the WiX bundle as a chained package (build/wix). One
# script decides which build of the runtime ships, so the two cannot drift into
# staging different runtimes while each stays perfectly happy on its own - the
# same reasoning as build/installer-appid.txt and build/installer-identities.txt.
#
# Staging is a no-op when the file is already there, which is the normal case:
# the Inno and WiX packaging steps share one payload directory.
#
# Usage: stage-dotnet-runtime.ps1 -ArtifactsDir artifacts/win-x64
# Returns: the path to the staged installer.
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$ArtifactsDir,
    [string]$RuntimeExe = "windowsdesktop-runtime-win-x64.exe"
)

$ErrorActionPreference = "Stop"

# Anchor to the repository so this behaves the same wherever it is called from.
# Callers pass either a repository-relative path or an absolute one, so a rooted
# path is used as it stands rather than appended to the repository root.
$repoRoot = Split-Path -Parent $PSScriptRoot
if (-not [System.IO.Path]::IsPathRooted($ArtifactsDir)) {
    $ArtifactsDir = Join-Path $repoRoot $ArtifactsDir
}
$ArtifactsDir = (New-Item -ItemType Directory -Force $ArtifactsDir).FullName
$runtimePath = Join-Path $ArtifactsDir $RuntimeExe

if (Test-Path $runtimePath) {
    Write-Host "Using staged .NET 10 Desktop Runtime installer: $runtimePath"
    return $runtimePath
}

Write-Host "Fetching .NET 10 Desktop Runtime installer (offline prerequisite)..."
$meta = Invoke-RestMethod -Uri 'https://builds.dotnet.microsoft.com/dotnet/release-metadata/10.0/releases.json'
$file = $meta.releases | ForEach-Object { $_.windowsdesktop } |
    Where-Object { $_ } | ForEach-Object { $_.files } |
    Where-Object { $_.rid -eq 'win-x64' -and $_.name -eq $RuntimeExe } |
    Select-Object -First 1
if (-not $file) { throw "Runtime installer not found in .NET release metadata" }
Invoke-WebRequest -Uri $file.url -OutFile $runtimePath

$size = (Get-Item $runtimePath).Length
if ($size -lt 10MB) { throw "Staged runtime installer looks invalid ($size bytes) - expected the full ~60 MB setup" }
Write-Host "Staged: $runtimePath ($([math]::Round($size / 1MB, 1)) MB)"

return $runtimePath
