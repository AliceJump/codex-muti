param([switch]$Dashboard = $true, [string]$BuildDirectory)
$ErrorActionPreference = 'Stop'
$taskRoot = [IO.Path]::GetFullPath((Split-Path $PSScriptRoot -Parent))
$taskPointer = Join-Path $taskRoot '.local\current-build.json'
if ($BuildDirectory) { $taskBuild = [IO.Path]::GetFullPath($BuildDirectory) }
elseif (Test-Path -LiteralPath $taskPointer) { $taskBuild = [IO.Path]::GetFullPath((Get-Content -LiteralPath $taskPointer -Raw | ConvertFrom-Json).path) }
else { throw '请先执行 .\scripts\build-local.ps1。' }
if (-not $taskBuild.StartsWith((Join-Path $taskRoot '.local') + '\',[StringComparison]::OrdinalIgnoreCase)) { throw '构建目录必须位于本仓库 .local 内。' }
$taskDaemon = Join-Path $taskBuild 'Daemon\CodexPool.Daemon.exe'
$taskTray = Join-Path $taskBuild 'Widget\CodexPool.Floating.exe'
if (-not (Test-Path -LiteralPath $taskDaemon) -or -not (Test-Path -LiteralPath $taskTray)) { throw '构建不完整，请重新执行 build-local.ps1。' }
$taskOnline = $false
try { $taskOnline = (Invoke-RestMethod -Uri 'http://127.0.0.1:3210/health' -NoProxy -TimeoutSec 2).status -eq 'running' } catch { }
if (-not $taskOnline) {
    if (Get-NetTCPConnection -LocalPort 3210 -State Listen -ErrorAction SilentlyContinue) { throw '端口 3210 已被占用，未停止或替换现有进程。' }
    Start-Process -FilePath $taskDaemon -ArgumentList @('serve','--live-boundary-probe') -WorkingDirectory (Split-Path $taskDaemon -Parent) -WindowStyle Hidden
    for ($taskTry=0; $taskTry -lt 30; $taskTry++) {
        try { $taskOnline = (Invoke-RestMethod -Uri 'http://127.0.0.1:3210/health' -NoProxy -TimeoutSec 1).status -eq 'running' } catch { }
        if ($taskOnline) { break }
        Start-Sleep -Milliseconds 150
    }
    if (-not $taskOnline) { throw '代理尚未就绪，请检查本机运行环境。脚本未终止任何进程。' }
    Write-Host '新构建的代理已启动，Web 服务已启用。'
} else { Write-Host '代理已经运行，保留现有代理，不替换进程。新代理修复需你手动退出旧代理后再启动。' }
. (Join-Path $PSScriptRoot 'desktop-client.ps1')
Start-PoolDesktopClient -BuildDirectory $taskBuild -Tray
if (Test-Path -LiteralPath (Join-Path $taskRoot '.local/proxy-tray/current-build.json')) { & (Join-Path $PSScriptRoot 'start-proxy-tray.ps1') }
Write-Host '托盘客户端已启动。若旧客户端仍在运行，请从其托盘菜单退出界面后重试；这不会停止代理。'
if ($Dashboard) { & $taskDaemon dashboard }
