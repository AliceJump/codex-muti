#requires -Version 7.0
$ErrorActionPreference = 'Stop'
$taskRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$taskPointer = Join-Path $taskRoot '.local/proxy-tray/current-build.json'
if (-not (Test-Path -LiteralPath $taskPointer)) { $taskPointer = Join-Path $taskRoot '.local/current-build.json' }
if (-not (Test-Path -LiteralPath $taskPointer)) { throw '请先运行 scripts/build-local.ps1 或 scripts/build-proxy-tray.ps1。' }
$taskBuild = [IO.Path]::GetFullPath((Get-Content -LiteralPath $taskPointer -Raw | ConvertFrom-Json).path)
if (-not $taskBuild.StartsWith((Join-Path $taskRoot '.local') + '\', [StringComparison]::OrdinalIgnoreCase)) { throw '代理托盘构建目录必须位于本仓库 .local 内。' }
. (Join-Path $PSScriptRoot 'desktop-client.ps1')
Start-PoolDesktopClient -BuildDirectory $taskBuild -ProxyTray -NoDaemonStart
