#Requires -Version 5.1
<#
.SYNOPSIS
    Installs Microsoft HTML Help Workshop (hhc.exe) from the installer this
    repository vendors, on a machine that does not already have it.

.DESCRIPTION
    The compiled help is the whole of the Windows payload now: without hhc.exe
    there is no Synapic.chm, and a Windows build without one ships no help at
    all (docs/help/README.md). The compiler is a 1997 tool that Microsoft still
    redistributes freely but no longer links to, so CI installs it explicitly.

    It is vendored rather than fetched for two reasons. A build should not
    depend on a third-party package manager to produce its own payload, and it
    should not depend on web.archive.org staying up - the only surviving copy
    of this installer lives there, behind a redirect that serves HTML to
    anything that does not ask for the raw bytes. build/vendor/ is the pinned,
    hash-checked copy; this script installs it.

    The install is the same one the chocolatey package performs, and the same
    steps have to happen in this order:
      1. verify the vendored installer's SHA-256,
      2. self-extract it (it is an EXE wrapping an INF, not a plain archive),
      3. neutralise hhupd.exe in the INF - the automatic updater has no
         network to talk to and pops a modal dialog that would hang CI,
      4. run the INF through advpack's LaunchINFSection, which is how an INF
         section installs files,
      5. check hhc.exe is actually there, because none of the above fails
         loudly when it goes wrong.

.PARAMETER HhcPath
    Where to expect hhc.exe after installing, when it is not in the default
    location. Defaults to the usual Program Files path.

.PARAMETER Force
    Install even when a working hhc.exe is already present.

.EXAMPLE
    ./build/install-html-help-workshop.ps1

.EXAMPLE
    ./build/install-html-help-workshop.ps1 -Force
#>
[CmdletBinding()]
param(
    [string] $HhcPath,
    [switch] $Force
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# The pinned copy, and the hash it must always have. Changing the hash without
# re-reading the file is the only way to get an installer you did not intend
# to ship, so this is deliberately the SHA-256 and not the MD5 that the
# chocolatey package records.
$installer = Join-Path $PSScriptRoot 'vendor\html-help-workshop\htmlhelp.exe'
$expectedSha256 = 'b2b3140d42a818870c1ab13c1c7b8d4536f22bd994fa90aade89729a6009a3ae'

if (-not (Test-Path -LiteralPath $installer)) {
    throw "The vendored HTML Help Workshop installer is missing: $installer"
}

# The INF's default section is the one that installs files, so the section name
# is empty. LIS_QUIET | LIS_NOGRPCONV - quiet, and no Group Policy conversion.
# 'N' means do not reboot; nothing here is in use.
$section = ''
$launchFlags = 3
$reboot = 'N'

function Find-Hhc {
    if ($HhcPath) { return $HhcPath }

    $candidates = @()
    foreach ($root in @(${env:ProgramFiles(x86)}, $env:ProgramFiles)) {
        if (-not $root) { continue }
        $candidates += (Join-Path $root 'HTML Help Workshop\hhc.exe')
        $candidates += (Get-ChildItem -Path (Join-Path $root 'Windows Kits\10\bin') -Filter 'hhc.exe' -Recurse -ErrorAction SilentlyContinue |
            Sort-Object FullName -Descending |
            Select-Object -ExpandProperty FullName)
    }

    foreach ($candidate in $candidates) {
        if ($candidate -and (Test-Path -LiteralPath $candidate)) {
            return (Get-Item -LiteralPath $candidate).FullName
        }
    }
    return $null
}

$existing = Find-Hhc
if ($existing -and -not $Force) {
    Write-Host "hhc.exe is already installed at $existing - skipping (use -Force to reinstall)."
    exit 0
}

# Verify before running. This is a 1997 self-extracting installer being
# executed with SYSTEM-level write access; "the file we ship is the file we
# ship" is the whole reason it is in the repository rather than downloaded.
$actualSha256 = (Get-FileHash -LiteralPath $installer -Algorithm SHA256).Hash.ToLowerInvariant()
if ($actualSha256 -ne $expectedSha256) {
    throw @"
The vendored HTML Help Workshop installer does not match its recorded hash.

  file:     $installer
  expected: $expectedSha256
  actual:   $actualSha256

Refusing to run it. Either the file is damaged or it is not the one this
repository recorded - see build/vendor/html-help-workshop/README.md.
"@
}
Write-Host "Verified htmlhelp.exe ($([math]::Round((Get-Item -LiteralPath $installer).Length / 1KB)) KB, SHA-256 $actualSha256)"

# Extract: the installer is an EXE wrapping htmlhelp.inf, not a plain
# archive, so it has to be asked. /Q is quiet and /T: is the extraction
# folder, but those two together only make it check for an existing install:
# left at that it puts up a modal "the computer already has a newer version of
# HTML help installed" and exits 0 having written nothing at all. /C is what
# says "extract only, do not install" - without it this step is a no-op that
# looks exactly like success.
$work = Join-Path ([System.IO.Path]::GetTempPath()) ("htmlhelp-" + [Guid]::NewGuid().ToString('N'))
$staging = Join-Path $work 'htmlhelp'
New-Item -ItemType Directory -Force -Path $staging | Out-Null

try {
    Write-Host "Extracting $installer"
    $process = Start-Process -FilePath $installer `
        -ArgumentList @("/Q", "/T:$staging", "/C") -Wait -PassThru
    if ($process.ExitCode -ne 0) {
        throw "The HTML Help Workshop installer exited $($process.ExitCode) while extracting."
    }

    $inf = Join-Path $staging 'htmlhelp.inf'
    if (-not (Test-Path -LiteralPath $inf)) {
        throw "The installer extracted, but there is no htmlhelp.inf in $staging - this is not the installer this script expects."
    }

    # hhupd.exe is HTML Help's automatic updater. It cannot reach anything
    # useful on a build agent and pops a modal "you already have the latest
    # version" dialog, which is how an unattended install hangs. Commenting out
    # its RunCommands entry, exactly as the chocolatey package does, is what
    # makes this installable without a person watching it.
    $patchedInf = Join-Path $staging 'htmlhelp-noupdate.inf'
    $lines = @(Get-Content -LiteralPath $inf)
    $patched = @($lines | ForEach-Object {
        # ';' is an INF comment, and the rest of the line is left alone so the
        # file still reads the way it was written.
        if ($_ -match '^\s*"hhupd\.exe /C') { ';' + $_ } else { $_ }
    })
    Set-Content -LiteralPath $patchedInf -Value $patched -Encoding ASCII

    $neutralised = ($lines -join "`n") -ne ($patched -join "`n")
    if (-not $neutralised) {
        # No updater entry means this is not the INF the chocolatey package
        # installs and not one this script was written against. Carrying on
        # would install a tool whose behaviour nobody here has checked.
        throw "htmlhelp.inf has no hhupd.exe entry to neutralise - this is not the installer this script expects."
    }
    Write-Host "INF prepared; automatic updater neutralised."

    # advpack.dll's LaunchINFSection is how an INF section installs files. It
    # has to be the 32-bit rundll32: the tool has no 64-bit distribution, and
    # the 64-bit one silently does nothing useful with this call.
    $rundll = Join-Path $env:SystemRoot 'SysWOW64\rundll32.exe'
    if (-not (Test-Path -LiteralPath $rundll)) {
        $rundll = Join-Path $env:SystemRoot 'System32\rundll32.exe'
    }
    if (-not (Test-Path -LiteralPath $rundll)) {
        throw "rundll32.exe was not found under $env:SystemRoot."
    }

    # One argument string, not four. advpack reads the whole call as
    # <dll>,<entry> <inf>,<section>,<flags>,<reboot>, so the section name - which
    # is empty here, because the INF's default section is the one that installs
    # - has to be an empty field between two commas. Passing them as separate
    # -ArgumentList entries does not work at all: Start-Process rejects the
    # empty string outright on Windows PowerShell.
    $call = 'advpack.dll,LaunchINFSection "{0}",{1},{2},{3}' -f $patchedInf, $section, $launchFlags, $reboot

    Write-Host "Installing with $rundll"
    $install = Start-Process -FilePath $rundll -ArgumentList $call -Wait -PassThru
    if ($install.ExitCode -ne 0) {
        throw "LaunchINFSection exited $($install.ExitCode)."
    }
}
finally {
    if (Test-Path -LiteralPath $work) {
        Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue
    }
}

$installed = Find-Hhc
if (-not $installed) {
    throw "HTML Help Workshop claims to have installed, but hhc.exe is not where it should be. The compiled help cannot be built."
}

Write-Host "Installed hhc.exe at $installed" -ForegroundColor Green
$global:LASTEXITCODE = 0
