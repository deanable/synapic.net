# The outage window for the live Daminion acceptance (phase 10, DAM-03).
#
# Usage (from an elevated PowerShell):
#   build/block-daminion.ps1 -WorkDir artifacts/phase-10/block -TimeoutSeconds 300
#
# The server is not on this machine, so "the catalog goes away" is modelled the
# way it actually happens: this machine's outbound TCP to the server's address
# is blocked by a firewall rule, for a bounded window, and the rule is removed in
# a `finally` even when the run fails.
#
# The script is a watcher, not a test: it waits for `block.request` in WorkDir,
# applies the rule, proves the block took effect on this machine, writes
# `block.ack`, then waits for `unblock.request`, proves the block is gone,
# removes the rule and writes `unblock.ack`. The acceptance test writes the
# request files; it cannot elevate, and this script refuses to run unelevated.
#
# Everything it writes is in WorkDir (ack files are evidence, not secrets).
[CmdletBinding()]
param(
    [string]$WorkDir = "artifacts/phase-10/block",
    [string]$ServerUrl = $env:SYNAPIC_TEST_DAMINION_URL,
    [int]$TimeoutSeconds = 300,
    [string]$RuleName = "Synapic live outage"
)

$ErrorActionPreference = "Stop"

function Assert-Elevated {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($identity)
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        Write-Host "The outage window needs Administrator: it adds and removes a Windows firewall rule." -ForegroundColor Yellow
        exit 4
    }
}

function Remove-OutageRule {
    # No rule with this name is the success state; a stale one from an earlier
    # crashed run is removed rather than inherited.
    & netsh advfirewall firewall delete rule name="$RuleName" 2>&1 | Out-Null
}

function Test-ServerReachable {
    param([string]$Address, [int]$Port, [int]$TimeoutMs = 4000)
    $client = New-Object System.Net.Sockets.TcpClient
    try {
        $task = $client.ConnectAsync($Address, $Port)
        if (-not $task.Wait($TimeoutMs)) { return $false }
        return $client.Connected
    }
    catch {
        return $false
    }
    finally {
        $client.Dispose()
    }
}

function Read-SavedServerUrl {
    # The same connection the app saved; only the URL is read here — the
    # password stays in the blob and is never needed to block an address.
    $key = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey("Software\Synapic\Daminion")
    if ($null -eq $key) { return $null }
    try { return $key.GetValue("ServerUrl") -as [string] }
    finally { $key.Dispose() }
}

function Resolve-ServerAddresses {
    param([string]$Url)
    if ([string]::IsNullOrWhiteSpace($Url)) {
        $Url = Read-SavedServerUrl
    }
    if ([string]::IsNullOrWhiteSpace($Url)) {
        throw "No server URL: pass -ServerUrl, set SYNAPIC_TEST_DAMINION_URL, or save a connection in the app."
    }
    $uri = [Uri]$Url
    $addresses = [System.Net.Dns]::GetHostAddresses($uri.Host) |
        Where-Object { $_.AddressFamily -eq [System.Net.Sockets.AddressFamily]::InterNetwork } |
        ForEach-Object { $_.IPAddressToString } |
        Sort-Object -Unique
    if (-not $addresses) { throw "Could not resolve $($uri.Host) to an IPv4 address." }
    return [pscustomobject]@{ Host = $uri.Host; Port = if ($uri.Port -gt 0) { $uri.Port } else { 80 }; Addresses = @($addresses) }
}

function Wait-ForFile {
    param([string]$Path, [int]$Seconds)
    $deadline = (Get-Date).AddSeconds($Seconds)
    while ((Get-Date) -lt $deadline) {
        if (Test-Path $Path) { return $true }
        Start-Sleep -Milliseconds 250
    }
    return $false
}

Assert-Elevated

$repoRoot = Split-Path -Parent $PSScriptRoot
Set-Location $repoRoot
$dir = (New-Item -ItemType Directory -Force $WorkDir).FullName

# A previous run's requests must not trigger this one.
Remove-Item (Join-Path $dir "block.request"), (Join-Path $dir "unblock.request") -ErrorAction SilentlyContinue
Remove-Item (Join-Path $dir "block.ack"), (Join-Path $dir "unblock.ack") -ErrorAction SilentlyContinue

$server = Resolve-ServerAddresses -Url $ServerUrl
Write-Host "Watching $dir for block.request; the outage rule targets $($server.Host) ($($server.Addresses -join ', ')):$($server.Port)."

$ruleApplied = $false
try {
    if (-not (Wait-ForFile -Path (Join-Path $dir "block.request") -Seconds $TimeoutSeconds)) {
        Write-Host "No block was requested within $TimeoutSeconds seconds; nothing was changed." -ForegroundColor Yellow
        exit 5
    }

    $reachableBefore = Test-ServerReachable -Address $server.Addresses[0] -Port $server.Port

    Remove-OutageRule
    & netsh advfirewall firewall add rule name="$RuleName" dir=out action=block protocol=TCP `
        remoteip=$($server.Addresses -join ",") enable=yes profile=any | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "netsh could not add the outage rule (exit $LASTEXITCODE)." }
    $ruleApplied = $true

    # The block is only real if a new connection now fails. The rule applies to
    # new connections, so a probe that still connects means the rule did not take
    # (wrong profile, wrong address) and the test must not be told it was blocked.
    $blocked = $false
    for ($i = 0; $i -lt 3 -and -not $blocked; $i++) {
        $blocked = -not (Test-ServerReachable -Address $server.Addresses[0] -Port $server.Port)
        if (-not $blocked) { Start-Sleep -Seconds 1 }
    }

    $ack = @(
        "blocked_at=$((Get-Date).ToString('o'))"
        "host=$($server.Host)"
        "port=$($server.Port)"
        "addresses=$($server.Addresses -join ',')"
        "reachable_before_block=$reachableBefore"
        "blocked_after_apply=$blocked"
        "rule=$RuleName"
    )
    Set-Content -Path (Join-Path $dir "block.ack") -Value $ack
    Write-Host "Outage rule applied; server reachable before=$reachableBefore blocked=$blocked."

    if (-not (Wait-ForFile -Path (Join-Path $dir "unblock.request") -Seconds $TimeoutSeconds)) {
        Write-Host "No unblock was requested within $TimeoutSeconds seconds; removing the rule anyway." -ForegroundColor Yellow
    }
}
finally {
    if ($ruleApplied) {
        Remove-OutageRule
        $ruleApplied = $false
    }

    $reachableAfter = Test-ServerReachable -Address $server.Addresses[0] -Port $server.Port
    $ack = @(
        "unblocked_at=$((Get-Date).ToString('o'))"
        "host=$($server.Host)"
        "reachable_after_unblock=$reachableAfter"
        "rule=$RuleName"
    )
    Set-Content -Path (Join-Path $dir "unblock.ack") -Value $ack
    Write-Host "Outage rule removed; server reachable=$reachableAfter."
}
