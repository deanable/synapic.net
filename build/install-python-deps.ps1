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

# The `-cuda` RID suffix selects the CUDA wheel index; everything else gets
# CPU wheels. requirements.txt pins torch==2.9.1, which PEP 440 treats as
# satisfied by 2.9.1+cu126, so the CUDA wheels survive the requirements pass.
if ($Rid -like "*-cuda") {
    Write-Host "Installing torch/torchvision (CUDA 12.6 wheels)"
    & $pythonExe -m pip install --no-cache-dir torch==2.9.1 torchvision==0.24.1 --index-url https://download.pytorch.org/whl/cu126
    if ($LASTEXITCODE -ne 0) { throw "torch CUDA install failed with exit code $LASTEXITCODE" }
} else {
    Write-Host "Installing torch/torchvision (CPU wheels)"
    & $pythonExe -m pip install --no-cache-dir torch==2.9.1 torchvision==0.24.1 --index-url https://download.pytorch.org/whl/cpu
    if ($LASTEXITCODE -ne 0) { throw "torch CPU install failed with exit code $LASTEXITCODE" }
}

Write-Host "Installing sidecar requirements (torch/torchvision already satisfied)"
& $pythonExe -m pip install --no-cache-dir -r (Join-Path $repoRoot "src/Synapic.Inference/requirements.txt")
if ($LASTEXITCODE -ne 0) { throw "requirements install failed with exit code $LASTEXITCODE" }

Write-Host "Python deps installed for $Rid"
