$exe = 'C:\Users\Dean\source\repos\Synapic.NET\src\Synapic.Indexer\bin\Debug\net10.0-windows\Synapic.Indexer.exe'
$transcript = 'C:\Users\Dean\source\repos\Synapic.NET\.planning\phases\05-indexer-service-host-and-configuration\idempotency-transcript.txt'

"=== idempotency run $(Get-Date -Format o) ===" | Set-Content $transcript -Encoding utf8
& $exe --install *>> $transcript
"first install exit: $LASTEXITCODE" | Add-Content $transcript
& $exe --install *>> $transcript
"second install exit: $LASTEXITCODE" | Add-Content $transcript
& sc.exe query SynapicIndexer *>> $transcript
"query exit: $LASTEXITCODE" | Add-Content $transcript
& $exe --uninstall *>> $transcript
"uninstall exit: $LASTEXITCODE" | Add-Content $transcript
& sc.exe query SynapicIndexer *>> $transcript
"query after uninstall exit: $LASTEXITCODE" | Add-Content $transcript
"=== done ===" | Add-Content $transcript
