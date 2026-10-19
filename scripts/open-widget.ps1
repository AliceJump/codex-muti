param([switch]$Show, [switch]$NoDaemonStart, [switch]$SkipProxyTray)
$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path $PSScriptRoot -Parent
$taskBuild = Join-Path $taskRoot '.local\release'
$taskPointer = Join-Path $taskRoot '.local\current-build.json'
if (Test-Path -LiteralPath $taskPointer) {
    $taskBuild = [IO.Path]::GetFullPath((Get-Content -Raw -LiteralPath $taskPointer | ConvertFrom-Json).path)
    if (-not $taskBuild.StartsWith((Join-Path $taskRoot '.local') + '\',[StringComparison]::OrdinalIgnoreCase)) { throw '构建目录不在本仓库 .local 内。' }
}
$taskWindow = Join-Path $taskBuild 'Widget\CodexPool.Floating.exe'
if (-not (Test-Path -LiteralPath $taskWindow)) {
    throw '请先运行 scripts\build-widget.ps1。'
}
# The widget starts the adjacent daemon if needed; Web is already part of that daemon.
. (Join-Path $PSScriptRoot 'desktop-client.ps1')
Start-PoolDesktopClient -BuildDirectory $taskBuild -Tray -Show:$Show -NoDaemonStart:$NoDaemonStart
if (-not $SkipProxyTray -and (Test-Path -LiteralPath (Join-Path $taskRoot '.local/proxy-tray/current-build.json'))) { & (Join-Path $PSScriptRoot 'start-proxy-tray.ps1') }
