#Requires -Version 5.1
<#
.SYNOPSIS
    Compiles the Synapic HTML Help sources into Synapic.chm.

.DESCRIPTION
    HTML Help Workshop's hhc.exe is quiet about failure - it can exit 0 having
    compiled nothing, and a stale .chm looks fine - so this script does four
    things the raw tool does not: it checks the sources first (check-help.py),
    greps hhc's own output for diagnostics, proves the .chm was actually
    rewritten by this run, and refuses an output too small to be help at all.
    It also leaves the caller a truthful exit code, because hhc's own one is not.

    On success it writes help-payload.json next to the .chm: the SHA-256 of the
    bytes just compiled. The app project embeds that manifest into Synapic.dll
    beside the compiled help, and HelpService checks the bytes against it before
    opening them, so a damaged install is reported instead of showing help that
    is not the help this build was made from. Every failure path here removes
    the manifest along with the .chm - a hash left describing a file that is not
    there is worse than no hash at all.

.PARAMETER HhcPath
    Full path to hhc.exe. Defaults to $env:HHC, then to the usual install
    locations for HTML Help Workshop and the Windows SDK.

.PARAMETER OutputDirectory
    Where to put the compiled Synapic.chm. Defaults to this folder.

.PARAMETER AllowMissingCompiler
    Warn and exit 0 instead of failing when this machine cannot produce a
    compiled .chm: hhc.exe is not installed, or it ran and wrote nothing usable.
    For a leg that needs the help sources checked but may not carry a working
    HTML Help Workshop: the app ships the HTML topics on every platform and opens
    those when there is no .chm, so losing the compiler costs the
    Contents/Index/search panes and nothing else.

.EXAMPLE
    ./docs/help/build-chm.ps1

.EXAMPLE
    ./docs/help/build-chm.ps1 -OutputDirectory ./artifacts/win-x64
#>
[CmdletBinding()]
param(
    [string] $HhcPath,
    [string] $OutputDirectory,
    [switch] $AllowMissingCompiler
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$projectDir = $PSScriptRoot
$projectFile = 'Synapic.hhp'
$projectPath = Join-Path $projectDir $projectFile
$compiledName = 'Synapic.chm'
$compiledPath = Join-Path $projectDir $compiledName

# Written only by a compile this script has proven, and removed by every one it
# has not. Anything else lets a hash outlive the file it describes.
$manifestName = 'help-payload.json'
$manifestPath = Join-Path $projectDir $manifestName

function Remove-StalePayload {
    if (Test-Path -LiteralPath $manifestPath) {
        Remove-Item -LiteralPath $manifestPath -Force
    }
}

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

    if ($AllowMissingCompiler) {
        Remove-StalePayload
        Write-Warning 'hhc.exe was not found - skipping the compiled .chm. The HTML topics still ship, and the app opens those.'
        return $null
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

# Sources first: dead links, topics missing from [FILES] and non-ASCII bytes are
# always fatal, because hhc.exe would compile the mistake happily. The missing
# compiler below is the one failure that can be tolerated, and only on request.
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

$hhc = Find-Hhc
if (-not $hhc) { exit 0 }

Remove-StalePayload
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

# hhc.exe can also exit having written a stub - 0 bytes, or a handful - and say
# nothing at all, which the checks above cannot tell from success. A .chm next
# to the app wins over the HTML topics on Windows, so a stub is worse than no
# .chm: Windows help would open nothing while the fallback that works sits in
# help/. Not one topic fits in this much, so treat it as the failed compile it
# is and take the file with it.
$minimumChmBytes = 4KB
if ($compiled.Length -lt $minimumChmBytes) {
    $stubBytes = $compiled.Length
    Remove-Item -LiteralPath $compiledPath -Force
    Remove-StalePayload
    $output | ForEach-Object { Write-Host $_ }
    if (-not $AllowMissingCompiler) {
        throw "hhc.exe wrote a $stubBytes-byte $compiledName - a failed compile, not help. Removed it; the HTML topics in help/ are what the app will open."
    }
    Write-Warning "hhc.exe wrote a $stubBytes-byte $compiledName (a failed compile) - removed it, so the app opens the HTML topics instead."
    exit 0
}

$target = $compiled.FullName
if ($OutputDirectory) {
    $destinationDir = (New-Item -ItemType Directory -Force -Path $OutputDirectory).FullName
    $target = Join-Path $destinationDir $compiledName
    Copy-Item -LiteralPath $compiledPath -Destination $target -Force
}# The file's own size, not the length of the path string it is named by - which
# reported a healthy 13 KB .chm as "0 KB" and made a good compile look empty.
$sizeKb = [math]::Round((Get-Item -LiteralPath $target).Length / 1KB)
Write-Host "Compiled $target ($sizeKb KB)" -ForegroundColor Green

# The SHA-256 of the bytes this run produced, written beside the .chm it
# describes. Synapic.Main.csproj embeds this manifest into Synapic.dll
# alongside the compiled help, and HelpService checks the bytes against it before
# opening them: this is what makes the help tamper-evident rather than merely
# compiled. No BOM - JsonDocument does not skip one, and Windows PowerShell's
# Set-Content -Encoding UTF8 writes one.
$hash = (Get-FileHash -LiteralPath $compiledPath -Algorithm SHA256).Hash.ToLowerInvariant()
$manifestJson = [ordered]@{
    chm    = $compiledName
    sha256 = $hash
    bytes  = $compiled.Length
} | ConvertTo-Json

[System.IO.File]::WriteAllText(
    $manifestPath,
    $manifestJson,
    (New-Object System.Text.UTF8Encoding($false)))
Write-Host "Recorded SHA-256 $hash in $manifestName"

# -OutputDirectory is a copy of the .chm for someone publishing straight from
# it, so the hash has to travel with it or the pair there is not usable.
if ($OutputDirectory) {
    Copy-Item -LiteralPath $manifestPath -Destination (Join-Path $destinationDir $manifestName) -Force
}

# hhc.exe's exit code is not trustworthy in either direction: it reports failure
# on compiles that worked, and a caller that trusts the leftover code - GitHub's
# pwsh wrapper ends every step with `exit $LASTEXITCODE` - turns a good .chm into
# a failed build. This run has proven its own output by now, so hand the caller a
# clean code. Scoped global because a called script's own $LASTEXITCODE is local
# to it, and the caller is the one that reads this.
$global:LASTEXITCODE = 0
Write-Host 'Open it to check the Contents/Index panes and a couple of topics before shipping it.'
