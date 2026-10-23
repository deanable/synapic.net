# Run the live Daminion acceptance tests (phase 10).
#
# Usage: acceptance-daminion.ps1 [-Filter <dotnet-test filter>] [-BlockDir <dir>] [-NoSavedCredential]
#
# -BlockDir is for the outage fact: point it at the directory an elevated
# `build/block-daminion.ps1 -WorkDir <dir>` is watching. Without it that fact
# skips (a closed port is not the outage it is about).
#
# Credentials come from SYNAPIC_TEST_DAMINION_URL/_USERNAME/_PASSWORD when they
# are set, and otherwise from the connection the app saved under
# HKCU\Software\Synapic\Daminion (the password is DPAPI-protected there and is
# unprotected with the same entropy the app uses). The values are placed in this
# process's environment for the child `dotnet test` only: they are never printed,
# never written to a file and never passed on a command line. With neither source
# available the script exits 3 and says which piece is missing.
[CmdletBinding()]
param(
    [string]$Filter = "FullyQualifiedName~DaminionLiveAcceptanceTests",
    [string]$Configuration = "Release",
    [string]$TestProject = "tests/Synapic.Indexer.Tests",
    [string]$OutputDir = "artifacts/phase-10",
    [string]$BlockDir = "",
    [switch]$NoSavedCredential
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
Set-Location $repoRoot

function Read-SavedConnection {
    $keyPath = "Software\Synapic\Daminion"
    $key = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($keyPath)
    if ($null -eq $key) { return $null }
    try {
        $url = $key.GetValue("ServerUrl") -as [string]
        $username = $key.GetValue("Username") -as [string]
        $blob = $key.GetValue("PasswordEncrypted") -as [byte[]]
        if ([string]::IsNullOrWhiteSpace($url) -or [string]::IsNullOrWhiteSpace($username)) { return $null }
        if ($null -eq $blob -or $blob.Length -eq 0) { return $null }

        # Windows PowerShell does not load System.Security by default, and
        # ProtectedData lives there.
        Add-Type -AssemblyName System.Security

        # The app's own entropy (DaminionConnectionStore). Wrong user or a
        # corrupted blob throws; the caller reports it as "no saved password".
        $entropy = [System.Text.Encoding]::UTF8.GetBytes("Synapic.Daminion.v1")
        $plain = [System.Security.Cryptography.ProtectedData]::Unprotect(
            $blob, $entropy, [System.Security.Cryptography.DataProtectionScope]::CurrentUser)
        $password = [System.Text.Encoding]::UTF8.GetString($plain)
        return [pscustomobject]@{ ServerUrl = $url; Username = $username; Password = $password }
    }
    finally {
        $key.Dispose()
    }
}

$url = $env:SYNAPIC_TEST_DAMINION_URL
$username = $env:SYNAPIC_TEST_DAMINION_USERNAME
$password = $env:SYNAPIC_TEST_DAMINION_PASSWORD
$source = "environment"

if ([string]::IsNullOrWhiteSpace($url) -or [string]::IsNullOrWhiteSpace($username) -or
    [string]::IsNullOrEmpty($password)) {
    if ($NoSavedCredential) {
        Write-Host "No live Daminion credentials: SYNAPIC_TEST_DAMINION_* are not set and the saved connection was skipped." -ForegroundColor Yellow
        exit 3
    }
    try {
        $saved = Read-SavedConnection
    }
    catch {
        Write-Host "The saved Daminion password could not be unprotected for this user: $($_.Exception.GetType().Name)" -ForegroundColor Yellow
        exit 3
    }
    if ($null -eq $saved) {
        Write-Host "No live Daminion credentials: SYNAPIC_TEST_DAMINION_* are not set and HKCU\Software\Synapic\Daminion holds no complete connection." -ForegroundColor Yellow
        exit 3
    }
    $url = $saved.ServerUrl
    $username = $saved.Username
    $password = $saved.Password
    $source = "saved Windows connection (HKCU\Software\Synapic\Daminion)"
}

# Child scope only: the tests read these, nothing else in this shell keeps them.
$env:SYNAPIC_TEST_DAMINION_URL = $url
$env:SYNAPIC_TEST_DAMINION_USERNAME = $username
$env:SYNAPIC_TEST_DAMINION_PASSWORD = $password

if (-not [string]::IsNullOrWhiteSpace($BlockDir)) {
    $env:SYNAPIC_TEST_DAMINION_BLOCK_DIR = (New-Item -ItemType Directory -Force $BlockDir).FullName
}

Write-Host "Live Daminion acceptance against $url as '$username' (credentials from the $source)."

$log = Join-Path (New-Item -ItemType Directory -Force $OutputDir).FullName `
    ("live-" + (Get-Date -Format "yyyyMMdd-HHmmss") + ".log")

# detailed: the report block each live fact prints is the transcript's substance,
# and xunit only forwards ITestOutputHelper at detailed verbosity.
# Tee while it runs (so the operator sees progress), then re-write as utf8:
# Windows PowerShell's Tee-Object writes utf16, and the log is committed
# evidence that has to be readable by grep and by the people reading it.
$rawLog = "$log.raw"
# Continue, not Stop: xUnit writes its failure lines to stderr, and with Stop a
# failing test aborts the script before the console log is kept — leaving a red
# exit and an empty evidence file.
$ErrorActionPreference = "Continue"
& dotnet test $TestProject -c $Configuration --nologo --filter $Filter `
    --logger "console;verbosity=detailed" 2>&1 | Tee-Object -FilePath $rawLog
$testExit = $LASTEXITCODE
$ErrorActionPreference = "Stop"
Get-Content $rawLog | Set-Content -Path $log -Encoding utf8
Remove-Item $rawLog

Write-Host "Output: $log"
exit $testExit
