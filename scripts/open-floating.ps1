$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path $PSScriptRoot -Parent
$taskDaemon = Join-Path $taskRoot '.local\release\Daemon\CodexPool.Daemon.exe'
$taskWindow = Join-Path $taskRoot '.local\release\Floating\CodexPool.Floating.exe'
if (-not (Test-Path -LiteralPath $taskDaemon) -or -not (Test-Path -LiteralPath $taskWindow)) {
    throw '请先运行 scripts\build-release.ps1。'
}
$taskOnline = $false
try {
    $taskHealth = Invoke-RestMethod -Uri 'http://127.0.0.1:3210/health' -TimeoutSec 2 -NoProxy
    $taskOnline = $taskHealth.status -eq 'running'
} catch {
    if (Get-NetTCPConnection -LocalPort 3210 -State Listen -ErrorAction SilentlyContinue) {
        throw '端口 3210 已有服务，但无法确认代理状态。'
    }
}
if (-not $taskOnline) {
    # The desktop launcher opts into account migration; the saved AutoSwitch setting still controls scheduling.
    Start-Process -FilePath $taskDaemon -ArgumentList @('serve', '--live-boundary-probe') -WorkingDirectory (Split-Path $taskDaemon -Parent) -WindowStyle Hidden
}
Start-Process -FilePath $taskWindow -WorkingDirectory (Split-Path $taskWindow -Parent) -WindowStyle Normal
