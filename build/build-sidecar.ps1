# Build the Python inference sidecar with PyInstaller.
# Usage: build-sidecar.ps1 <rid> [python-dir] [output-dir]
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Rid,
    [string]$PythonDir = "build/_python/$Rid/python",
    [string]$OutputDir = "artifacts/$Rid"
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot

$pythonExe = Join-Path $PythonDir "python.exe"
if (-not (Test-Path $pythonExe)) { throw "Python not found at $pythonExe - run fetch-python.ps1 first" }

New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null

Push-Location $repoRoot
try {
    Write-Host "Running PyInstaller for $Rid"
    & $pythonExe -m PyInstaller --noconfirm --clean `
        --distpath $OutputDir `
        --workpath "build/_work/$Rid" `
        "src/Synapic.Inference/synapic-inference.spec"
    if ($LASTEXITCODE -ne 0) { throw "PyInstaller failed with exit code $LASTEXITCODE" }
}
finally {
    Pop-Location
}

Get-ChildItem $OutputDir | Format-Table Name, Length
