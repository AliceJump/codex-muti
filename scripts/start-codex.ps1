#requires -Version 7.0
param([switch]$CheckOnly)
$ErrorActionPreference = 'Stop'
$taskRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$taskPointer = Join-Path $taskRoot '.local/current-build.json'
if (-not (Test-Path -LiteralPath $taskPointer)) { throw '缺少本地构建，请先运行 scripts/build-local.ps1 和 scripts/build-compact-bridge.ps1。' }
$taskBuild = [IO.Path]::GetFullPath((Get-Content -LiteralPath $taskPointer -Raw | ConvertFrom-Json).path)
if (-not $taskBuild.StartsWith((Join-Path $taskRoot '.local') + '\', [StringComparison]::OrdinalIgnoreCase)) { throw '构建目录必须位于本仓库 .local 内。' }
$taskDaemon = Join-Path $taskBuild 'Daemon/CodexPool.Daemon.exe'
if (-not (Test-Path -LiteralPath $taskDaemon)) { throw '代理构建不完整，请先运行 scripts/build-local.ps1。' }
# Resolve and validate every Desktop dependency before starting the sidecar.
$taskDesktop = & (Join-Path $PSScriptRoot 'desktop-compact-bridge.ps1') -CheckOnly -PassThru
if ($taskDesktop.DesktopRunning -and -not $taskDesktop.RunningThroughBridge) { throw '当前 Codex Desktop 未使用所选桥接构建。请结束工作并退出 Desktop，然后双击 Codex Pool；不会自动终止现有会话。' }
function Read-PoolLauncherStatus {
    # Local command captures the bearer internally. Never print the raw status/accounts.
    $taskRawStatus = & $taskDaemon status 2>$null
    if ($LASTEXITCODE -ne 0) { throw '无法读取代理状态；请检查代理版本、运行用户和本地数据目录。' }
    try { return $taskRawStatus | ConvertFrom-Json -ErrorAction Stop } catch { throw '代理状态格式无效。' }
}
function Test-PoolLauncherMode($State) {
    if (-not $State.boundaryProbe -or -not $State.compaction.enabled) { throw '运行中的代理尚未启用压缩桥接。请在请求结束后退出旧代理，再使用 Codex Pool 启动；入口不会停止现有代理。' }
}
function Test-PoolLauncherOnline {
    try { return (Invoke-RestMethod -Uri 'http://127.0.0.1:3210/health' -NoProxy -TimeoutSec 2).status -eq 'running' } catch { return $false }
}
if ($CheckOnly) {
    if (Test-PoolLauncherOnline) {
        $taskState = Read-PoolLauncherStatus
        Test-PoolLauncherMode $taskState
        Write-Host "检查通过：代理已启用压缩；自动切换=$($taskState.automaticSwitching)；沿用已保存阈值。"
    } else { Write-Host '入口检查通过：代理未运行，正式启动时将自动拉起。' }
    Write-Host "Desktop 已运行=$($taskDesktop.DesktopRunning)；使用当前桥接=$($taskDesktop.RunningThroughBridge)。没有改变进程、账户或设置。"
    return
}
$taskMutex = [Threading.Mutex]::new($false, 'Local\CodexPoolCodexLauncher')
$taskOwned = $false
try {
    try { $taskOwned = $taskMutex.WaitOne(15000) } catch [Threading.AbandonedMutexException] { $taskOwned = $true }
    if (-not $taskOwned) { throw '另一个 Codex Pool 启动正在进行，请稍后重试。' }
    # Re-check after the mutex: another click may have finished launching Desktop.
    $taskDesktop = & (Join-Path $PSScriptRoot 'desktop-compact-bridge.ps1') -CheckOnly -PassThru
    if ($taskDesktop.DesktopRunning -and -not $taskDesktop.RunningThroughBridge) { throw '请先退出未使用所选桥接构建的 Codex Desktop，再从 Codex Pool 启动。' }
    if (-not (Test-PoolLauncherOnline)) {
        if (Get-NetTCPConnection -LocalPort 3210 -State Listen -ErrorAction SilentlyContinue) { throw '端口 3210 已被占用，未停止任何进程。' }
        $taskLogDirectory = Join-Path $taskRoot '.local/logs'
        $null = New-Item -ItemType Directory -Path $taskLogDirectory -Force
        $taskLogName = 'codex-launcher-' + [Guid]::NewGuid().ToString('N')
        $taskStarted = Start-Process -FilePath $taskDaemon -ArgumentList @('serve','--live-boundary-probe','--experimental-compact-bridge') -WorkingDirectory (Split-Path $taskDaemon -Parent) -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $taskLogDirectory "$taskLogName.log") -RedirectStandardError (Join-Path $taskLogDirectory "$taskLogName.err")
        $taskOnline = $false
        for ($taskTry=0; $taskTry -lt 30; $taskTry++) {
            if (Test-PoolLauncherOnline) { $taskOnline = $true; break }
            if ($taskStarted.HasExited) { break }
            Start-Sleep -Milliseconds 200
        }
        if (-not $taskOnline) { throw '代理启动未完成，请查看 .local/logs 下的启动日志。未重启 Desktop 或其他进程。' }
    }
    $taskState = Read-PoolLauncherStatus
    Test-PoolLauncherMode $taskState
    if (-not $taskState.automaticSwitching) { Write-Warning '自动切换目前关闭。请在账户面板中打开自动切换后使用额度预警压缩；已保存的阈值保持不变。' }
    if (Test-Path -LiteralPath (Join-Path $taskBuild 'Widget/CodexPool.Floating.exe')) {
        . (Join-Path $PSScriptRoot 'desktop-client.ps1')
        Start-PoolDesktopClient -BuildDirectory $taskBuild -Tray -NoDaemonStart
    }
    & (Join-Path $PSScriptRoot 'desktop-compact-bridge.ps1') -CodexPath $taskDesktop.CodexPath -DesktopPath $taskDesktop.DesktopPath -BridgePath $taskDesktop.BridgePath -ReuseRunning
    if (-not $taskDesktop.RunningThroughBridge) {
        # Hold the launch mutex until the child is visible, so a rapid second click
        # cannot launch another Desktop while the first instance is initializing.
        $taskDeadline = [DateTimeOffset]::UtcNow.AddSeconds(20)
        $taskReady = $false
        do {
            $taskRunning = & (Join-Path $PSScriptRoot 'desktop-compact-bridge.ps1') -CodexPath $taskDesktop.CodexPath -DesktopPath $taskDesktop.DesktopPath -BridgePath $taskDesktop.BridgePath -CheckOnly -PassThru
            if ($taskRunning.RunningThroughBridge) { $taskReady = $true; break }
            Start-Sleep -Milliseconds 500
        } while ([DateTimeOffset]::UtcNow -lt $taskDeadline)
        if (-not $taskReady) { throw 'Desktop 已请求启动，但尚未确认桥接子进程。请检查应用启动状态，不要重复启动或结束当前工作。' }
    }
    Write-Host 'Codex Pool 已就绪。代理、账户和阈值沿用本机配置。'
} finally {
    if ($taskOwned) { $taskMutex.ReleaseMutex() }
    $taskMutex.Dispose()
}
