# Install the sidecar's pinned requirements into the fetched standalone Python.
# Usage: install-python-deps.ps1 <rid> [python-dir]
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Rid,
    [string]$PythonDir = "build/_python/$Rid/python"
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)

$pythonExe = Join-Path $PythonDir "python.exe"
if (-not (Test-Path $pythonExe)) { throw "Python not found at $pythonExe — run fetch-python.ps1 first" }

Write-Host "Upgrading pip tooling"
& $pythonExe -m pip install --upgrade pip setuptools wheel

Write-Host "Installing sidecar requirements (CPU torch)"
& $pythonExe -m pip install --no-cache-dir -r (Join-Path $repoRoot "src/Synapic.Inference/requirements.txt")

Write-Host "Python deps installed for $Rid"
