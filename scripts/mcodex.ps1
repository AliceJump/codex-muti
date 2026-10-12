# Delegates directly to official Codex. No workflow logic or account-specific command variants.
$taskPublished = Join-Path $PSScriptRoot 'Daemon\CodexPool.Daemon.exe'
if (Test-Path -LiteralPath $taskPublished) {
    & $taskPublished codex @args
} else {
    $poolAssembly = Join-Path $PSScriptRoot '..\src\CodexPool.Daemon\bin\Debug\net10.0-windows\CodexPool.Daemon.dll'
    & dotnet $poolAssembly codex @args
}
exit $LASTEXITCODE
