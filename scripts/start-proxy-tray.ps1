#requires -Version 7.0
$ErrorActionPreference = 'Stop'
$taskRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$taskPointer = @('.local/current-build.json', '.local/proxy-tray/current-build.json' | ForEach-Object {
    $taskCandidate = Join-Path $taskRoot $_
    if (Test-Path -LiteralPath $taskCandidate) { Get-Item -LiteralPath $taskCandidate }
} | Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 1).FullName
if (-not $taskPointer) { throw '请先运行 scripts/build-local.ps1 或 scripts/build-proxy-tray.ps1。' }
$taskBuild = [IO.Path]::GetFullPath((Get-Content -LiteralPath $taskPointer -Raw | ConvertFrom-Json).path)
if (-not $taskBuild.StartsWith((Join-Path $taskRoot '.local') + '\', [StringComparison]::OrdinalIgnoreCase)) { throw '代理托盘构建目录必须位于本仓库 .local 内。' }
. (Join-Path $PSScriptRoot 'desktop-client.ps1')
Start-PoolDesktopClient -BuildDirectory $taskBuild -ProxyTray -NoDaemonStart
