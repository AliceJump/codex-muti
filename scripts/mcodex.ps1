# Delegates directly to official Codex. No workflow logic or account-specific command variants.
$poolAssembly = Join-Path $PSScriptRoot '..\src\CodexPool.Daemon\bin\Debug\net10.0-windows\CodexPool.Daemon.dll'
& dotnet $poolAssembly codex @args
exit $LASTEXITCODE
