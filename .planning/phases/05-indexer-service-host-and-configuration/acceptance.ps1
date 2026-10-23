$ErrorActionPreference = 'Continue'
$repo = 'C:\Users\Dean\source\repos\Synapic.NET'
$exe = Join-Path $repo 'src\Synapic.Indexer\bin\Debug\net10.0-windows\Synapic.Indexer.exe'
$cfg = 'C:\ProgramData\Synapic\indexer-config.json'
$logDir = 'C:\ProgramData\Synapic\Logs'
$transcript = Join-Path $repo '.planning\phases\05-indexer-service-host-and-configuration\acceptance-transcript.txt'

function Note($text) { $text | Add-Content -Path $transcript -Encoding utf8 }
function Run($title, [string[]]$argv) {
    Note "--- $title ---"
    $out = & $argv[0] $argv[1..($argv.Count - 1)] 2>&1 | Out-String
    $code = $LASTEXITCODE
    Note $out.TrimEnd()
    Note "$title exit code: $code"
    return $code
}

"=== acceptance run $(Get-Date -Format o) ===" | Set-Content $transcript -Encoding utf8
Note '--- config under test ---'
Note ((Get-Content $cfg) -join "`n")

Run 'Synapic.Indexer.exe --install' @($exe, '--install') | Out-Null
Run 'sc.exe query (after install)' @('sc.exe', 'query', 'SynapicIndexer') | Out-Null

Run 'sc.exe start' @('sc.exe', 'start', 'SynapicIndexer') | Out-Null
$deadline = (Get-Date).AddSeconds(60)
do {
    Start-Sleep -Milliseconds 500
    $status = (Get-Service -Name SynapicIndexer -ErrorAction SilentlyContinue).Status
} while (($status -ne 'Running') -and ((Get-Date) -lt $deadline))
Note "service status after start: $status"

$logFile = Join-Path $logDir ('indexer-' + (Get-Date -Format 'yyyyMMdd') + '.log')
$readyBefore = @(Select-String -Path $logFile -Pattern 'Indexer ready' -ErrorAction SilentlyContinue).Count
Note '--- waiting for readiness (max 240 s) ---'
$readyStart = Get-Date
$deadline = $readyStart.AddSeconds(240)
do {
    Start-Sleep -Seconds 1
    $readyAfter = @(Select-String -Path $logFile -Pattern 'Indexer ready' -ErrorAction SilentlyContinue).Count
} while (($readyAfter -le $readyBefore) -and ((Get-Date) -lt $deadline))
Note "readiness reached: $($readyAfter -gt $readyBefore) (waited $([math]::Round(((Get-Date) - $readyStart).TotalSeconds, 1)) s)"

Note '--- file log (start of this run) ---'
if (Test-Path $logFile) {
    Get-Content $logFile -Tail 40 | Where-Object { $_ -match 'Synapic log started|Indexer service started|Sidecar|Indexer ready' } | ForEach-Object { Note $_ }
}

Note '--- Application log entries from source SynapicIndexer ---'
try {
    Get-WinEvent -FilterHashtable @{ ProviderName = 'SynapicIndexer' } -MaxEvents 5 -ErrorAction Stop |
        Select-Object TimeCreated, LevelDisplayName, Message |
        Format-Table -AutoSize -Wrap | Out-String -Width 200 | ForEach-Object { Note $_ }
} catch {
    Note "no entries: $($_.Exception.Message)"
}

Note '--- sc.exe stop (timed to STOPPED) ---'
$stopwatch = [System.Diagnostics.Stopwatch]::StartNew()
$stopOutput = & sc.exe stop SynapicIndexer 2>&1 | Out-String
Note $stopOutput.TrimEnd()
$deadline = (Get-Date).AddSeconds(30)
do {
    Start-Sleep -Milliseconds 100
    $status = (Get-Service -Name SynapicIndexer -ErrorAction SilentlyContinue).Status
} while (($status -ne 'Stopped') -and ((Get-Date) -lt $deadline))
$stopwatch.Stop()
Note "service status after stop: $status"
Note "stop elapsed seconds: $([math]::Round($stopwatch.Elapsed.TotalSeconds, 2))"

Note '--- log lines written during the stop ---'
Get-Content $logFile -Tail 15 | ForEach-Object { Note $_ }

Note '--- orphan check ---'
$orphans = Get-Process synapic-inference -ErrorAction SilentlyContinue
if ($orphans) {
    $orphans | Select-Object Id, ProcessName | Format-Table | Out-String | ForEach-Object { Note $_ }
} else {
    Note 'no synapic-inference processes'
}

Run 'Synapic.Indexer.exe --uninstall' @($exe, '--uninstall') | Out-Null
Run 'sc.exe query (after uninstall; 1060 = not installed)' @('sc.exe', 'query', 'SynapicIndexer') | Out-Null

Note "=== done $(Get-Date -Format o) ==="
