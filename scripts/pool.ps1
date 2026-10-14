param([Parameter(ValueFromRemainingArguments = $true)][string[]]$PoolArguments)
$ErrorActionPreference = 'Stop'
$taskPublished = Join-Path $PSScriptRoot 'Daemon\CodexPool.Daemon.exe'
$taskLocal = Join-Path $PSScriptRoot '..\src\CodexPool.Daemon\bin\Debug\net10.0-windows\CodexPool.Daemon.exe'
$taskDaemon = if (Test-Path -LiteralPath $taskPublished) { $taskPublished } else { $taskLocal }
if ($PoolArguments.Count -eq 0 -or $PoolArguments[0] -eq 'start') {
    Start-Process -FilePath $taskDaemon -ArgumentList 'serve' -WindowStyle Hidden
    Write-Host 'Codex Pool starting at http://127.0.0.1:3210'
} elseif ($PoolArguments[0] -in @('widget', 'tray')) {
    $taskFolder = 'Widget'
    $taskPublishedUi = Join-Path $PSScriptRoot "$taskFolder\CodexPool.Floating.exe"
    $taskLocalUi = Join-Path $PSScriptRoot '..\src\CodexPool.Floating\bin\Debug\net10.0-windows\CodexPool.Floating.exe'
    $taskUi = if (Test-Path -LiteralPath $taskPublishedUi) { $taskPublishedUi } else { $taskLocalUi }
    Start-Process -FilePath $taskUi -ArgumentList '--tray' -WindowStyle Hidden
} else {
    & $taskDaemon @PoolArguments
    exit $LASTEXITCODE
}
