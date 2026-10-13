#requires -Version 7.0
param([switch]$SelfContained, [switch]$Dashboard, [switch]$Floating)
$ErrorActionPreference = 'Stop'
$taskRoot = [IO.Path]::GetFullPath((Split-Path $PSScriptRoot -Parent))
$taskPointer = Join-Path $taskRoot '.local\current-build.json'
$taskMutex = [Threading.Mutex]::new($false, ('Local\CodexPoolDeploy-' + [Environment]::UserName))
$taskOwned = $false
$taskOld = $null
$taskNew = $null
$taskStopped = $false
$taskOldUi = @()
$taskUiStopped = @()

function Test-PoolPath([string]$Path) {
    if (-not $Path) { return $false }
    $taskResolved = [IO.Path]::GetFullPath($Path)
    foreach ($taskCheckout in $taskCheckouts) {
        if ($taskResolved.StartsWith((Join-Path $taskCheckout '.local') + '\', [StringComparison]::OrdinalIgnoreCase)) { return $true }
    }
    return $false
}
function Stop-OwnedProcess($ProcessInfo) {
    $taskProcess = Get-Process -Id $ProcessInfo.Id -ErrorAction SilentlyContinue
    if (-not $taskProcess) { return }
    if ($taskProcess.Path -ne $ProcessInfo.Path -or $taskProcess.StartTime.ToUniversalTime().Ticks -ne $ProcessInfo.StartTicks) { throw '进程身份已变化，拒绝停止。' }
    Stop-Process -InputObject $taskProcess -Force
    if (-not $taskProcess.WaitForExit(10000)) { throw '进程尚未退出，停止部署。' }
}
function Start-PoolDaemon([string]$Path, [bool]$BoundaryProbe) {
    $taskArgs = @('serve')
    if ($BoundaryProbe) { $taskArgs += '--live-boundary-probe' }
    Start-Process -FilePath $Path -ArgumentList $taskArgs -WorkingDirectory (Split-Path $Path -Parent) -WindowStyle Hidden -PassThru
}
function Get-PoolSettingsSnapshot($Settings) {
    # Older daemons omit the new optional threshold. Compare its effective absence as null.
    [ordered]@{autoSwitch=[bool]$Settings.autoSwitch;fiveHourThreshold=[double]$Settings.fiveHourThreshold;
        weeklyThreshold=[double]$Settings.weeklyThreshold;windowThreshold=$Settings.windowThreshold} | ConvertTo-Json -Compress
}
function Wait-PoolDaemon($Process, [string]$Path) {
    for ($taskAttempt=0; $taskAttempt -lt 30; $taskAttempt++) {
        $Process.Refresh()
        if ($Process.HasExited) { throw '新代理进程提前退出。' }
        try {
            $taskListener = @(Get-NetTCPConnection -LocalPort 3210 -State Listen -ErrorAction SilentlyContinue)
            $taskHealth = Invoke-RestMethod -Uri 'http://127.0.0.1:3210/health' -NoProxy -TimeoutSec 1
            if ($taskHealth.status -eq 'running' -and $taskListener.Count -gt 0 -and @($taskListener | Where-Object OwningProcess -NE $Process.Id).Count -eq 0) {
                $taskRaw = & $Path status
                if ($LASTEXITCODE -ne 0) { throw '无法读取新代理的授权状态。' }
                return ($taskRaw | ConvertFrom-Json)
            }
        } catch { }
        Start-Sleep -Milliseconds 200
    }
    throw '代理未在预期时间内就绪。'
}
function Start-PoolUi([string]$Path, [bool]$Tray) {
    if ($Tray) { Start-Process -FilePath $Path -ArgumentList '--tray' -WorkingDirectory (Split-Path $Path -Parent) -WindowStyle Hidden }
    else { Start-Process -FilePath $Path -WorkingDirectory (Split-Path $Path -Parent) -WindowStyle Normal }
}

try {
    try { $taskOwned = $taskMutex.WaitOne(0) } catch [Threading.AbandonedMutexException] { $taskOwned = $true }
    if (-not $taskOwned) { throw '已有部署在运行。' }
    $taskWorktrees = & git -C $taskRoot worktree list --porcelain
    if ($LASTEXITCODE -ne 0) { throw '无法确认本仓库工作树，停止部署。' }
    $taskCheckouts = @($taskWorktrees | Where-Object { $_.StartsWith('worktree ') } | ForEach-Object { [IO.Path]::GetFullPath($_.Substring(9)) })
    $taskBuild = Join-Path $taskRoot ('.local\builds\deploy-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N').Substring(0,6))
    Write-Host '1/4 构建到独立目录，现有代理继续运行…'
    & (Join-Path $PSScriptRoot 'build-release.ps1') -SelfContained:$SelfContained -OutputDirectory $taskBuild
    if ($LASTEXITCODE -ne 0) { throw '构建失败，原代理未停止。' }
    $taskNewExe = Join-Path $taskBuild 'Daemon\CodexPool.Daemon.exe'
    $taskNewTray = Join-Path $taskBuild 'Widget\CodexPool.Floating.exe'
    $taskNewFloating = Join-Path $taskBuild 'Floating\CodexPool.Floating.exe'
    foreach ($taskFile in @($taskNewExe,$taskNewTray,$taskNewFloating)) { if (-not (Test-Path -LiteralPath $taskFile)) { throw '构建产物不完整。' } }
    $taskListeners = @(Get-NetTCPConnection -LocalPort 3210 -State Listen -ErrorAction SilentlyContinue | Select-Object -ExpandProperty OwningProcess -Unique)
    if ($taskListeners.Count -gt 1) { throw '3210 有多个监听进程，拒绝猜测。' }
    $taskBoundary = $true
    if ($taskListeners.Count -eq 1) {
        $taskProcess = Get-Process -Id $taskListeners[0]
        if ($taskProcess.ProcessName -ne 'CodexPool.Daemon' -or -not (Test-PoolPath $taskProcess.Path)) { throw '3210 不属于本仓库发布的 Codex Pool，未停止任何进程。' }
        $taskOld = [pscustomobject]@{Id=$taskProcess.Id;Path=$taskProcess.Path;StartTicks=$taskProcess.StartTime.ToUniversalTime().Ticks;Boundary=$false}
        # A successful signed control request also proves that the launch environment uses the same local vault.
        $taskRaw = & $taskNewExe status
        if ($LASTEXITCODE -ne 0) { throw '无法验证当前账户存储。请在原代理相同的 CODEX_POOL_DATA_DIR 环境中运行。' }
        $taskStatus = $taskRaw | ConvertFrom-Json
        if (@($taskStatus.nativeLoginAttempts | Where-Object state -EQ 'waiting_for_browser').Count) { throw '账户正在登录，请完成或取消登录后再部署。' }
        for ($taskAttempt=0; @($taskStatus.accounts | Where-Object auth -EQ 'refresh_pending').Count -gt 0 -and $taskAttempt -lt 20; $taskAttempt++) {
            Start-Sleep -Milliseconds 500
            $taskRaw = & $taskNewExe status
            if ($LASTEXITCODE -ne 0) { throw '刷新期间无法验证代理状态，未停止服务。' }
            $taskStatus = $taskRaw | ConvertFrom-Json
        }
        if (@($taskStatus.accounts | Where-Object auth -EQ 'refresh_pending').Count) { throw 'Token 仍在刷新，未中止它；稍后重试部署。' }
        $taskBoundary = [bool]$taskStatus.boundaryProbe; $taskOld.Boundary = $taskBoundary
    }
    $taskOldUi = @(Get-CimInstance Win32_Process -Filter "Name='CodexPool.Floating.exe'" | Where-Object { Test-PoolPath $_.ExecutablePath } | ForEach-Object {
        $taskUiProcess = Get-Process -Id $_.ProcessId
        [pscustomobject]@{Id=$_.ProcessId;Path=$_.ExecutablePath;StartTicks=$taskUiProcess.StartTime.ToUniversalTime().Ticks;Tray=($_.CommandLine -match '(?:^|\s)--(?:tray|widget)(?:\s|$)')}
    })
    Write-Host '2/4 中止旧代理与本项目 UI。进行中的模型请求会断开，由 Codex 自己恢复。'
    foreach ($taskUi in $taskOldUi) { Stop-OwnedProcess $taskUi; $taskUiStopped += $taskUi }
    if ($taskOld) { Stop-OwnedProcess $taskOld; $taskStopped = $true }
    if (Get-NetTCPConnection -LocalPort 3210 -State Listen -ErrorAction SilentlyContinue) { throw '3210 仍被占用。' }
    Write-Host '3/4 启动新版本并验证健康状态…'
    $taskNew = Start-PoolDaemon $taskNewExe $taskBoundary
    $taskNewStatus = Wait-PoolDaemon $taskNew $taskNewExe
    if ($taskOld -and (Get-PoolSettingsSnapshot $taskNewStatus.settings) -ne (Get-PoolSettingsSnapshot $taskStatus.settings)) { throw '账户池设置校验失败。' }
    [IO.File]::WriteAllText($taskPointer + '.tmp', (@{path=$taskBuild;builtAt=[DateTimeOffset]::UtcNow.ToString('O')} | ConvertTo-Json -Compress))
    Move-Item -LiteralPath ($taskPointer + '.tmp') -Destination $taskPointer -Force
    Write-Host '4/4 新代理已就绪，启动托盘界面。'
    try {
        . (Join-Path $PSScriptRoot 'desktop-client.ps1')
        Start-PoolDesktopClient -BuildDirectory $taskBuild -Tray
        if ($Floating -or @($taskOldUi | Where-Object { -not $_.Tray }).Count) { Start-PoolDesktopClient -BuildDirectory $taskBuild }
        if ($Dashboard) { & $taskNewExe dashboard }
    } catch { Write-Warning '代理已部署成功，UI 可使用 start-local.ps1 手动打开。' }
    Write-Host "部署完成：$taskBuild"
} catch {
    $taskFailure = $_
    if ($taskNew) {
        $taskNew.Refresh()
        if (-not $taskNew.HasExited) { Stop-Process -InputObject $taskNew -Force; [void]$taskNew.WaitForExit(10000) }
    }
    if ($taskStopped -and $taskOld) {
        try { $taskRestored = Start-PoolDaemon $taskOld.Path $taskOld.Boundary; $null = Wait-PoolDaemon $taskRestored $taskOld.Path; Write-Warning '部署失败，已启动旧版本。' }
        catch { Write-Warning ('旧版本自动恢复失败，请手动运行：' + $taskOld.Path + ' serve') }
    }
    foreach ($taskUi in $taskUiStopped) { try { Start-PoolUi $taskUi.Path $taskUi.Tray } catch { } }
    throw $taskFailure
} finally {
    if ($taskOwned) { $taskMutex.ReleaseMutex() }
    $taskMutex.Dispose()
}

