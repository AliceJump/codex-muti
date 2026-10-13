param([switch]$SelfContained)
$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path $PSScriptRoot -Parent
$taskOutput = Join-Path $taskRoot '.local\release\Widget'
$taskMode = if ($SelfContained) { 'true' } else { 'false' }
# Publish only the client so an active daemon / existing floating window need not restart.
& dotnet publish (Join-Path $taskRoot 'src\CodexPool.Floating') -c Release -r win-x64 --self-contained $taskMode -o $taskOutput
if ($LASTEXITCODE -ne 0) { throw '小组件构建失败。' }
Write-Host "小组件已构建：$taskOutput"
