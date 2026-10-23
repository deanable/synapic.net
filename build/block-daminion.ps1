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

function Test-ServerReachableByHost {
    # What the client actually does: resolve the name and connect to whatever it
    # answers on, any family. A single-address probe can pass while the app still
    # reaches the server over a different address.
    param([string]$HostName, [int]$Port, [int]$TimeoutMs = 4000)
    $client = New-Object System.Net.Sockets.TcpClient
    try {
        $task = $client.ConnectAsync($HostName, $Port)
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
    # Every family. A dual-stack host answers on IPv6 as well, and blocking only
    # the A record left the catalog reachable while this script reported the
    # server gone. The rule addresses carry no zone id; the probe addresses keep
    # one where the OS supplied it, because a link-local connect needs it.
    $resolved = [System.Net.Dns]::GetHostAddresses($uri.Host) | Sort-Object -Unique
    $addresses = @($resolved | ForEach-Object { $_.IPAddressToString })
    $ruleAddresses = @($addresses | ForEach-Object { $_ -replace '%\d+$', '' } | Sort-Object -Unique)
    if (-not $addresses) { throw "Could not resolve $($uri.Host) to any address." }
    return [pscustomobject]@{
        Host = $uri.Host
        Port = if ($uri.Port -gt 0) { $uri.Port } else { 80 }
        Addresses = @($addresses)
        RuleAddresses = @($ruleAddresses)
    }
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
Write-Host "Watching $dir for block.request; the outage rule targets $($server.Host) ($($server.RuleAddresses -join ', ')):$($server.Port)."

$ruleApplied = $false
try {
    if (-not (Wait-ForFile -Path (Join-Path $dir "block.request") -Seconds $TimeoutSeconds)) {
        Write-Host "No block was requested within $TimeoutSeconds seconds; nothing was changed." -ForegroundColor Yellow
        exit 5
    }

    $reachableBefore = Test-ServerReachableByHost -HostName $server.Host -Port $server.Port

    # One rule per resolved address: netsh takes a list, but a mixed-family list is
    # not accepted on every build, and a missing rule is a silent hole.
    Remove-OutageRule
    $ruleApplied = $true
    foreach ($address in $server.RuleAddresses) {
        & netsh advfirewall firewall add rule name="$RuleName" dir=out action=block protocol=TCP `
            remoteip=$address enable=yes profile=any | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "netsh could not add the outage rule for $address (exit $LASTEXITCODE)." }
    }

    # The block is only real if a new connection now fails, and it has to fail the
    # way the client would try it — by name, over whichever address the resolver
    # hands back. A probe that still connects means the rule did not take (wrong
    # profile, wrong address, another family) and the test must not be told it was
    # blocked.
    $blocked = $false
    for ($i = 0; $i -lt 3 -and -not $blocked; $i++) {
        $blocked = -not (Test-ServerReachableByHost -HostName $server.Host -Port $server.Port)
        if (-not $blocked) { Start-Sleep -Seconds 1 }
    }

    # Per-address detail, so a partial block is visible rather than inferred.
    $openAddresses = @($server.Addresses | Where-Object { Test-ServerReachable -Address $_ -Port $server.Port })

    $ack = @(
        "blocked_at=$((Get-Date).ToString('o'))"
        "host=$($server.Host)"
        "port=$($server.Port)"
        "addresses=$($server.RuleAddresses -join ',')"
        "reachable_before_block=$reachableBefore"
        "blocked_after_apply=$blocked"
        "open_addresses_after_apply=$($openAddresses -join ',')"
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

    $reachableAfter = Test-ServerReachableByHost -HostName $server.Host -Port $server.Port
    $ack = @(
        "unblocked_at=$((Get-Date).ToString('o'))"
        "host=$($server.Host)"
        "reachable_after_unblock=$reachableAfter"
        "rule=$RuleName"
    )
    Set-Content -Path (Join-Path $dir "unblock.ack") -Value $ack
    Write-Host "Outage rule removed; server reachable=$reachableAfter."
}
