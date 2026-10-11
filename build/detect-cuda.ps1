# detect-cuda.ps1 - CUDA environment diagnostic for Synapic.
#
# Reports what the Synapic CUDA inference path needs to run on this machine:
# the NVIDIA driver (nvcuda.dll), the GPU(s) reported by nvidia-smi, and the
# driver/runtime versions. Read-only: it changes nothing on the system.
#
# Usage:  powershell -ExecutionPolicy Bypass -File build\detect-cuda.ps1
# Exit 0 when a usable CUDA setup is detected, 1 (with a human summary)
# when it is not - CI and scripts can branch on either.

$ErrorActionPreference = 'Continue'

Write-Host "Synapic CUDA detection"
Write-Host "======================" 

# 1. The CUDA driver library: what ComputeDeviceProbe.cs checks. It ships with
#    the NVIDIA display driver, so its presence is the cheapest honest signal
#    that a CUDA GPU is usable here.
$systemDir = [Environment]::SystemDirectory
$nvcuda = Join-Path $systemDir 'nvcuda.dll'
$hasNvcuda = Test-Path $nvcuda
if ($hasNvcuda) {
    $version = (Get-Item $nvcuda).VersionInfo.ProductVersion
    Write-Host "[OK]   CUDA driver library present: $nvcuda ($version)"
} else {
    Write-Host "[MISS] CUDA driver library not found at $nvcuda"
}

# 2. nvidia-smi: the GPU list and driver/CUDA runtime versions.
$smi = Get-Command nvidia-smi -ErrorAction SilentlyContinue
if (-not $smi) {
    $candidate = Join-Path (Join-Path $env:SystemRoot 'System32') 'nvidia-smi.exe'
    if (Test-Path $candidate) { $smi = Get-Item $candidate }
}
if ($smi) {
    Write-Host "[OK]   nvidia-smi found: $($smi.Source)"
    & $smi.Source
    if ($LASTEXITCODE -ne 0) {
        Write-Host "[WARN] nvidia-smi exited with error code $LASTEXITCODE"
    }
} else {
    Write-Host "[MISS] nvidia-smi not found (NVIDIA display driver likely not installed)"
}

# 3. Device enumeration through WMI, as a fallback signal.
try {
    $gpus = Get-CimInstance Win32_VideoController -ErrorAction Stop | Where-Object {
        $_.Name -match 'NVIDIA'
    }
    if ($gpus) {
        foreach ($gpu in $gpus) {
            Write-Host "[OK]   NVIDIA GPU: $($gpu.Name) (driver $($gpu.DriverVersion))"
        }
    } else {
        Write-Host "[MISS] No NVIDIA GPU among the video controllers"
    }
} catch {
    Write-Host "[WARN] GPU enumeration failed: $_"
}

# Bottom line, matching what the app itself concludes.
$usable = $hasNvcuda
if ($usable) {
    Write-Host ""
    Write-Host "Result: CUDA is available. Download the synapic-inference win-x64-cuda"
    Write-Host "sidecar in Settings > Inference; testing prefers CUDA and the server"
    Write-Host "will report 'CUDA' from /health once the model loads."
    exit 0
} else {
    Write-Host ""
    Write-Host "Result: CUDA is NOT available. Synapic will run inference on the CPU."
    Write-Host "Install the NVIDIA display driver for your GPU from nvidia.com - and"
    Write-Host "only after that download the CUDA sidecar. Synapic itself has nothing"
    Write-Host "to install: it never offers to install CUDA on your behalf."
    exit 1
}
