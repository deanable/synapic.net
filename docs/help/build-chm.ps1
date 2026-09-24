#Requires -Version 5.1
<#
.SYNOPSIS
    Compiles the Synapic HTML Help sources into Synapic.chm.

.DESCRIPTION
    HTML Help Workshop's hhc.exe is quiet about failure - it can exit 0 having
    compiled nothing, and a stale .chm looks fine - so this script does three
    things the raw tool does not: it checks the sources first (check-help.py),
    greps hhc's own output for diagnostics, and then proves the .chm was
    actually rewritten by this run.

.PARAMETER HhcPath
    Full path to hhc.exe. Defaults to $env:HHC, then to the usual install
    locations for HTML Help Workshop and the Windows SDK.

.PARAMETER OutputDirectory
    Where to put the compiled Synapic.chm. Defaults to this folder.

.EXAMPLE
    ./docs/help/build-chm.ps1

.EXAMPLE
    ./docs/help/build-chm.ps1 -OutputDirectory ./artifacts/win-x64
#>
[CmdletBinding()]
param(
    [string] $HhcPath,
    [string] $OutputDirectory
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$projectDir = $PSScriptRoot
$projectFile = 'Synapic.hhp'
$projectPath = Join-Path $projectDir $projectFile
$compiledName = 'Synapic.chm'
$compiledPath = Join-Path $projectDir $compiledName

if (-not (Test-Path -LiteralPath $projectPath)) {
    throw "Help project not found: $projectPath"
}

function Find-Hhc {
    if ($HhcPath) {
        if (-not (Test-Path -LiteralPath $HhcPath)) {
            throw "hhc.exe not found at the -HhcPath you gave: $HhcPath"
        }
        return (Get-Item -LiteralPath $HhcPath).FullName
    }

    if ($env:HHC -and (Test-Path -LiteralPath $env:HHC)) {
        return (Get-Item -LiteralPath $env:HHC).FullName
    }

    $roots = @(
        ${env:ProgramFiles(x86)},
        $env:ProgramFiles
    ) | Where-Object { $_ }

    $candidates = @()
    foreach ($root in $roots) {
        $candidates += (Join-Path $root 'HTML Help Workshop\hhc.exe')
        # The Windows SDK ships hhc.exe too, under a version-stamped bin folder.
        $candidates += Get-ChildItem -Path (Join-Path $root 'Windows Kits\10\bin') -Filter 'hhc.exe' -Recurse -ErrorAction SilentlyContinue |
            Sort-Object FullName -Descending |
            Select-Object -ExpandProperty FullName
    }

    foreach ($candidate in $candidates) {
        if ($candidate -and (Test-Path -LiteralPath $candidate)) {
            return (Get-Item -LiteralPath $candidate).FullName
        }
    }

    throw @"
hhc.exe was not found.

It ships with Microsoft HTML Help Workshop (the HTML Help 1.4 SDK) and with the
Windows SDK:
  https://learn.microsoft.com/previous-versions/windows/desktop/htmlhelp/microsoft-html-help-1-4-sdk

Install one of those, or point this script at an existing copy:
  ./build-chm.ps1 -HhcPath 'C:\path\to\hhc.exe'
  `$env:HHC = 'C:\path\to\hhc.exe'
"@
}

$hhc = Find-Hhc

# Cheap first: catch dead links, topics missing from [FILES] and non-ASCII bytes
# before handing anything to a compiler that would compile the mistake happily.
$checker = Join-Path $projectDir 'check-help.py'
if (Test-Path -LiteralPath $checker) {
    $python = Get-Command python -ErrorAction SilentlyContinue
    if ($python) {
        & $python.Source $checker
        if ($LASTEXITCODE -ne 0) {
            throw 'The help sources have problems (listed above). Fix them, or run check-help.py to see them all, then compile again.'
        }
    }
    else {
        Write-Warning 'python was not found - skipping check-help.py. The .chm may contain dead links.'
    }
}

Write-Host "Compiling $projectFile with $hhc"
$started = Get-Date

# hhc.exe resolves [FILES] and the output path relative to the project file, so
# run it from the project folder rather than passing a path with slashes in it
# (that difference has bitten this folder before).
Push-Location $projectDir
try {
    $output = & $hhc $projectFile 2>&1 | ForEach-Object { $_.ToString() }
}
finally {
    Pop-Location
}

$diagnostics = @($output | Where-Object { $_ -match 'HHC\d{4}|error|warning' })
if ($diagnostics.Count -gt 0) {
    Write-Warning "hhc.exe reported $($diagnostics.Count) diagnostic line(s):"
    $diagnostics | ForEach-Object { Write-Warning "  $_" }
}

if (-not (Test-Path -LiteralPath $compiledPath)) {
    $output | ForEach-Object { Write-Host $_ }
    throw "hhc.exe did not produce $compiledPath."
}

$compiled = Get-Item -LiteralPath $compiledPath
if ($compiled.LastWriteTime -lt $started) {
    throw "$compiledName was not rewritten by this run (it predates the compile). hhc.exe failed without reporting it; the output above is the only clue."
}

$target = $compiled.FullName
if ($OutputDirectory) {
    $destinationDir = (New-Item -ItemType Directory -Force -Path $OutputDirectory).FullName
    $target = Join-Path $destinationDir $compiledName
    Copy-Item -LiteralPath $compiledPath -Destination $target -Force
}

$sizeKb = [math]::Round($target.Length / 1KB)
Write-Host "Compiled $target ($sizeKb KB)" -ForegroundColor Green
Write-Host 'Open it to check the Contents/Index panes and a couple of topics before shipping it.'
