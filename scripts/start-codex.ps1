#requires -Version 7.0
param([switch]$CheckOnly)
$ErrorActionPreference = 'Stop'
$taskRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$taskPointer = Join-Path $taskRoot '.local/current-build.json'
if (-not (Test-Path -LiteralPath $taskPointer)) { throw '缺少本地构建，请先运行 scripts/build-local.ps1。' }
$taskBuild = [IO.Path]::GetFullPath((Get-Content -LiteralPath $taskPointer -Raw | ConvertFrom-Json).path)
if (-not $taskBuild.StartsWith((Join-Path $taskRoot '.local') + '\', [StringComparison]::OrdinalIgnoreCase)) { throw '构建目录必须位于本仓库 .local 内。' }
$taskDaemon = Join-Path $taskBuild 'Daemon/CodexPool.Daemon.exe'
if (-not (Test-Path -LiteralPath $taskDaemon)) { throw '代理构建不完整，请先运行 scripts/build-local.ps1。' }
$taskPackage = Get-AppxPackage -Name OpenAI.Codex
if (-not $taskPackage) { throw '未找到 Codex Desktop。' }
function Test-PoolLauncherOnline {
    try { return (Invoke-RestMethod -Uri 'http://127.0.0.1:3210/health' -NoProxy -TimeoutSec 2).status -eq 'running' } catch { return $false }
}
if ($CheckOnly) {
    Write-Host "入口检查通过：代理在线=$(Test-PoolLauncherOnline)。没有改变进程、账户或设置。"
    return
}
$taskMutex = [Threading.Mutex]::new($false, 'Local\CodexPoolCodexLauncher')
$taskOwned = $false
try {
    try { $taskOwned = $taskMutex.WaitOne(15000) } catch [Threading.AbandonedMutexException] { $taskOwned = $true }
    if (-not $taskOwned) { throw '另一个 Codex Pool 启动正在进行，请稍后重试。' }
    if (-not (Test-PoolLauncherOnline)) {
        if (Get-NetTCPConnection -LocalPort 3210 -State Listen -ErrorAction SilentlyContinue) { throw '端口 3210 已被占用。' }
        $taskStarted = Start-Process -FilePath $taskDaemon -ArgumentList @('serve','--live-boundary-probe') -WorkingDirectory (Split-Path $taskDaemon -Parent) -WindowStyle Hidden -PassThru
        $taskOnline = $false
        for ($taskTry=0; $taskTry -lt 30; $taskTry++) {
            if (Test-PoolLauncherOnline) { $taskOnline = $true; break }
            if ($taskStarted.HasExited) { break }
            Start-Sleep -Milliseconds 200
        }
        if (-not $taskOnline) { throw '代理启动未完成。' }
    }
    if (Test-Path -LiteralPath (Join-Path $taskBuild 'Widget/CodexPool.Floating.exe')) {
        . (Join-Path $PSScriptRoot 'desktop-client.ps1')
        Start-PoolDesktopClient -BuildDirectory $taskBuild -Tray -NoDaemonStart
    }
    if (Test-Path -LiteralPath (Join-Path $taskRoot '.local/proxy-tray/current-build.json')) {
        & (Join-Path $PSScriptRoot 'start-proxy-tray.ps1')
    }
    Start-Process -FilePath explorer.exe -ArgumentList ('shell:AppsFolder\' + $taskPackage.PackageFamilyName + '!App') -WindowStyle Hidden
    Write-Host 'Codex Pool 已就绪，已打开官方 Codex Desktop。'
} finally {
    if ($taskOwned) { $taskMutex.ReleaseMutex() }
    $taskMutex.Dispose()
}
