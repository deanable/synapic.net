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
    # Guard: refuse to build an exe whose transformers leaves the LFM2.5-VL
    # output head untied (see build/check-lfm2vl-tie.py for the full story).
    Write-Host "Checking that transformers ties LFM2.5-VL word embeddings..."
    & $pythonExe "build/check-lfm2vl-tie.py"
    if ($LASTEXITCODE -ne 0) {
        throw "transformers cannot tie LFM2.5-VL weights - pin transformers>=5.1.0 in src/Synapic.Inference/requirements.txt"
    }

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
