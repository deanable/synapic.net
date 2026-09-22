# Fetch a standalone Python 3.11 build (python-build-standalone) for a RID.
# Usage: fetch-python.ps1 <rid> [output-dir] [-Force]
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Rid,
    [string]$OutputDir = "build/_python",
    [switch]$Force
)

$ErrorActionPreference = "Stop"

$PbsTag = "20260901"
$PythonVersion = "3.11.16"

# CUDA is a packaging variant of the same Windows x64 interpreter: it shares
# the base Python and differs only in which torch wheels get installed
# (see install-python-deps.ps1), so it maps to the same triple.
switch ($Rid) {
    "win-x64"      { $triple = "x86_64-pc-windows-msvc"; $flavor = "install_only" }
    "win-x64-cuda" { $triple = "x86_64-pc-windows-msvc"; $flavor = "install_only" }
    default        { throw "Use build/fetch-python.sh for non-Windows RIDs (got: $Rid)" }
}

$archive = "cpython-$PythonVersion+$PbsTag-$triple-$flavor.tar.gz"
$url = "https://github.com/astral-sh/python-build-standalone/releases/download/$PbsTag/$archive"

$dest = Join-Path $OutputDir "$Rid"

# Re-extracting the tarball over an existing install removes the packages that
# were pip-installed into it (verified the hard way: a rerun wiped a cu126 torch
# install), so treat an existing interpreter as done. CI always starts clean.
$installedPython = Join-Path $dest "python\python.exe"
if (-not $Force -and (Test-Path $installedPython)) {
    Write-Host "Standalone Python already present at $installedPython - skipping fetch (use -Force to refresh)."
    return
}

New-Item -ItemType Directory -Force -Path $dest | Out-Null
$tarball = Join-Path $dest $archive

Write-Host "Fetching $url"
Invoke-WebRequest -Uri $url -OutFile $tarball

Write-Host "Extracting to $dest"
tar -xzf $tarball -C $dest
Remove-Item $tarball

$pythonExe = Join-Path $dest "python\python.exe"
& $pythonExe --version
Write-Host "Python installed at $pythonExe"
