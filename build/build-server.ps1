# One-shot build of the Python inference sidecar (synapic-inference.exe).
# Idempotent: fetches standalone Python if missing, installs pinned deps,
# then runs PyInstaller. Used by the app's "Build Server" button and CI.
# Usage: build-server.ps1 [rid]
param(
    [string]$Rid = "win-x64"
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$pythonExe = Join-Path $repoRoot "build/_python/$Rid/python/python.exe"

if (-not (Test-Path $pythonExe)) {
    Write-Host "[1/3] Fetching standalone Python for $Rid (one-time, ~30 MB)..."
    & (Join-Path $PSScriptRoot "fetch-python.ps1") -Rid $Rid
} else {
    Write-Host "[1/3] Standalone Python already present - skipping fetch."
}

Write-Host "[2/3] Installing sidecar Python dependencies (fast when already installed)..."
& (Join-Path $PSScriptRoot "install-python-deps.ps1") -Rid $Rid

Write-Host "[3/3] Running PyInstaller (this takes a few minutes)..."
& (Join-Path $PSScriptRoot "build-sidecar.ps1") -Rid $Rid

Write-Host "Sidecar build complete: artifacts/$Rid/synapic-inference.exe"
