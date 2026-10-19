#requires -Version 7.0
param([switch]$SkipBuild, [switch]$SelfContained, [switch]$Show)
$ErrorActionPreference = 'Stop'
$taskRoot = [IO.Path]::GetFullPath((Split-Path $PSScriptRoot -Parent))
if (-not $SkipBuild) {
    & (Join-Path $PSScriptRoot 'build-local.ps1') -SelfContained:$SelfContained
    if ($LASTEXITCODE -ne 0) { throw '构建失败，旧界面与代理继续运行。' }
}
$taskBuild = [IO.Path]::GetFullPath((Get-Content -LiteralPath (Join-Path $taskRoot '.local/current-build.json') -Raw | ConvertFrom-Json).path)
if (-not $taskBuild.StartsWith((Join-Path $taskRoot '.local') + '\', [StringComparison]::OrdinalIgnoreCase)) { throw '构建目录越界。' }
foreach ($taskKind in @('Widget')) {
    if (-not (Test-Path -LiteralPath (Join-Path $taskBuild "$taskKind/CodexPool.Floating.exe"))) { throw '新界面构建不完整，旧进程未停止。' }
}
$taskWorktrees = & git -C $taskRoot worktree list --porcelain
if ($LASTEXITCODE -ne 0) { throw '无法确认仓库工作树，旧进程未停止。' }
$taskRoots = @($taskWorktrees | Where-Object { $_.StartsWith('worktree ') } | ForEach-Object { [IO.Path]::GetFullPath($_.Substring(9)) })
$taskProxyBefore = @(Get-NetTCPConnection -LocalPort 3210 -State Listen -ErrorAction SilentlyContinue | Select-Object -ExpandProperty OwningProcess -Unique)
$taskOldUi = @(Get-CimInstance Win32_Process -Filter "Name='CodexPool.Floating.exe'" | Where-Object {
    $taskExecutable = $_.ExecutablePath
    $taskExecutable -and @($taskRoots | Where-Object { $taskExecutable.StartsWith((Join-Path $_ '.local') + '\', [StringComparison]::OrdinalIgnoreCase) }).Count -gt 0
} | ForEach-Object {
    $taskProcess = Get-Process -Id $_.ProcessId
    [pscustomobject]@{Id=$_.ProcessId;Path=$_.ExecutablePath;StartTicks=$taskProcess.StartTime.ToUniversalTime().Ticks;Tray=($_.CommandLine -match '(?:^|\s)--(?:tray|widget)(?:\s|$)');Proxy=($_.CommandLine -match '(?:^|\s)--proxy-tray(?:\s|$)')}
})
foreach ($taskUi in $taskOldUi) {
    $taskProcess = Get-Process -Id $taskUi.Id -ErrorAction SilentlyContinue
    if (-not $taskProcess) { continue }
    if ($taskProcess.Path -ne $taskUi.Path -or $taskProcess.StartTime.ToUniversalTime().Ticks -ne $taskUi.StartTicks) { throw 'UI 进程身份已变化，拒绝停止。' }
    Stop-Process -InputObject $taskProcess -Force
    if (-not $taskProcess.WaitForExit(10000)) { throw '旧 UI 尚未退出，停止更新。' }
}
. (Join-Path $PSScriptRoot 'desktop-client.ps1')
Start-PoolDesktopClient -BuildDirectory $taskBuild -Tray -NoDaemonStart
if (@($taskOldUi | Where-Object Proxy).Count -gt 0) { Start-PoolDesktopClient -BuildDirectory $taskBuild -ProxyTray -NoDaemonStart }
if ($Show) {
    $taskShown = $false
    for ($taskAttempt=0; $taskAttempt -lt 30; $taskAttempt++) {
        try {
            $taskSignal = [Threading.EventWaitHandle]::OpenExisting('Local\CodexPoolWidgetShow-' + [Environment]::UserName)
            try { $taskShown = $taskSignal.Set() } finally { $taskSignal.Dispose() }
            break
        } catch [Threading.WaitHandleCannotBeOpenedException] { Start-Sleep -Milliseconds 200 }
    }
    if (-not $taskShown) { throw '新界面未就绪，请检查本机 UI 环境。代理未停止。' }
}
$taskProxyAfter = @(Get-NetTCPConnection -LocalPort 3210 -State Listen -ErrorAction SilentlyContinue | Select-Object -ExpandProperty OwningProcess -Unique)
if (($taskProxyBefore -join ',') -ne ($taskProxyAfter -join ',')) { Write-Warning '代理监听进程发生变化；本脚本没有停止或重启代理。' }
Write-Host '界面已更新并自动启动，代理未停止或部署。'
