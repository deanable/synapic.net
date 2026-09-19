# Install the sidecar's pinned requirements into the fetched standalone Python.
# Usage: install-python-deps.ps1 <rid> [python-dir]
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Rid,
    [string]$PythonDir = "build/_python/$Rid/python"
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot

$pythonExe = Join-Path $PythonDir "python.exe"
if (-not (Test-Path $pythonExe)) { throw "Python not found at $pythonExe - run fetch-python.ps1 first" }

Write-Host "Upgrading pip tooling"
& $pythonExe -m pip install --upgrade pip setuptools wheel

Write-Host "Installing torch/torchvision (CPU wheels - CUDA variants substituted at packaging time)"
& $pythonExe -m pip install --no-cache-dir torch==2.9.1 torchvision==0.24.1 --index-url https://download.pytorch.org/whl/cpu
if ($LASTEXITCODE -ne 0) { throw "torch CPU install failed with exit code $LASTEXITCODE" }

Write-Host "Installing sidecar requirements (torch/torchvision already satisfied)"
& $pythonExe -m pip install --no-cache-dir -r (Join-Path $repoRoot "src/Synapic.Inference/requirements.txt")
if ($LASTEXITCODE -ne 0) { throw "requirements install failed with exit code $LASTEXITCODE" }

Write-Host "Python deps installed for $Rid"
