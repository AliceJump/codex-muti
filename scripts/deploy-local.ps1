#requires -Version 7.0
param([switch]$SelfContained, [switch]$Dashboard,
    [ValidateSet('All','Core','Components')][string]$Target = 'All')
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
$taskNewUi = @()
$taskDeployCore = $Target -ne 'Components'
$taskDeployUi = $Target -ne 'Core'

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
function Get-PoolStatus([string]$Path) {
    $taskRaw = & $Path status
    if ($LASTEXITCODE -ne 0) { throw '无法验证代理授权状态，请使用与原代理相同的账户存储环境。' }
    $taskRaw | ConvertFrom-Json
}
function Get-PoolSettingsSnapshot($Settings) {
    # Older daemons omit the new optional threshold. Compare its effective absence as null.
    [ordered]@{autoSwitch=[bool]$Settings.autoSwitch;fiveHourThreshold=[double]$Settings.fiveHourThreshold;
        weeklyThreshold=[double]$Settings.weeklyThreshold;windowThreshold=$Settings.windowThreshold} | ConvertTo-Json -Compress
}
function Get-PoolRefreshGuard($Status) {
    $taskActive = @()
    $taskReauthorize = @()
    $taskUnknown = @()
    foreach ($taskAccount in @($Status.accounts | Where-Object auth -EQ 'refresh_pending')) {
        $taskAvailability = @($Status.accountAvailability | Where-Object accountId -EQ $taskAccount.id)
        if ($taskAvailability.Count -eq 0) { $taskUnknown += $taskAccount.id }
        elseif ($taskAvailability[0].reason -eq 'auth_temporarily_unavailable') { $taskActive += $taskAccount.id }
        elseif ($taskAvailability[0].reason -eq 'auth_unavailable') { $taskReauthorize += $taskAccount.id }
        else { $taskUnknown += $taskAccount.id }
    }
    [pscustomobject]@{Active=$taskActive;Reauthorize=$taskReauthorize;Unknown=$taskUnknown}
}
function Wait-PoolDaemon($Process, [string]$Path) {
    for ($taskAttempt=0; $taskAttempt -lt 30; $taskAttempt++) {
        $Process.Refresh()
        if ($Process.HasExited) { throw '新代理进程提前退出。' }
        try {
            $taskListener = @(Get-NetTCPConnection -LocalPort 3210 -State Listen -ErrorAction SilentlyContinue)
            $taskHealth = Invoke-RestMethod -Uri 'http://127.0.0.1:3210/health' -NoProxy -TimeoutSec 1
            if ($taskHealth.status -eq 'running' -and $taskListener.Count -gt 0 -and @($taskListener | Where-Object OwningProcess -NE $Process.Id).Count -eq 0) {
                return (Get-PoolStatus $Path)
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
    . (Join-Path $PSScriptRoot 'desktop-client.ps1')
    $taskWorktrees = & git -C $taskRoot worktree list --porcelain
    if ($LASTEXITCODE -ne 0) { throw '无法确认本仓库工作树，停止部署。' }
    $taskCheckouts = @($taskWorktrees | Where-Object { $_.StartsWith('worktree ') } | ForEach-Object { [IO.Path]::GetFullPath($_.Substring(9)) })
    $taskBuild = Join-Path $taskRoot ('.local\builds\deploy-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N').Substring(0,6))
    $taskBase = if (Test-Path -LiteralPath $taskPointer) { (Get-Content -LiteralPath $taskPointer -Raw | ConvertFrom-Json).path } else { $null }
    Write-Host "1/4 构建 $Target 到独立目录，现有进程继续运行…"
    & (Join-Path $PSScriptRoot 'build-release.ps1') -SelfContained:$SelfContained -OutputDirectory $taskBuild -Target $Target -BaseDirectory $taskBase
    if ($LASTEXITCODE -ne 0) { throw '构建失败，原代理未停止。' }
    $taskNewExe = Join-Path $taskBuild 'Daemon\CodexPool.Daemon.exe'
    $taskNewTray = Join-Path $taskBuild 'Widget\CodexPool.Floating.exe'
    foreach ($taskFile in @($taskNewExe,$taskNewTray)) { if (-not (Test-Path -LiteralPath $taskFile)) { throw '构建产物不完整。' } }
    $taskListeners = if ($taskDeployCore) { @(Get-NetTCPConnection -LocalPort 3210 -State Listen -ErrorAction SilentlyContinue | Select-Object -ExpandProperty OwningProcess -Unique) } else { @() }
    if ($taskListeners.Count -gt 1) { throw '3210 有多个监听进程，拒绝猜测。' }
    $taskBoundary = $true
    if ($taskListeners.Count -eq 1) {
        $taskProcess = Get-Process -Id $taskListeners[0]
        if ($taskProcess.ProcessName -ne 'CodexPool.Daemon' -or -not (Test-PoolPath $taskProcess.Path)) { throw '3210 不属于本仓库发布的 Codex Pool，未停止任何进程。' }
        $taskOld = [pscustomobject]@{Id=$taskProcess.Id;Path=$taskProcess.Path;StartTicks=$taskProcess.StartTime.ToUniversalTime().Ticks;Boundary=$false}
        # A successful signed control request also proves that the launch environment uses the same local vault.
        $taskStatus = Get-PoolStatus $taskNewExe
        if (@($taskStatus.nativeLoginAttempts | Where-Object state -EQ 'waiting_for_browser').Count) { throw '账户正在登录，请完成或取消登录后再部署。' }
        $taskRefreshGuard = Get-PoolRefreshGuard $taskStatus
        for ($taskAttempt=0; $taskRefreshGuard.Active.Count -gt 0 -and $taskAttempt -lt 20; $taskAttempt++) {
            Start-Sleep -Milliseconds 500
            $taskStatus = Get-PoolStatus $taskNewExe
            $taskRefreshGuard = Get-PoolRefreshGuard $taskStatus
        }
        if ($taskRefreshGuard.Active.Count) { throw 'Token 仍在主动刷新，未中止它；刷新完成后再部署。' }
        if ($taskRefreshGuard.Unknown.Count) { throw '旧代理未提供可确认的刷新状态，无法安全停止；请先处理账户授权状态。' }
        if ($taskRefreshGuard.Reauthorize.Count) { Write-Host '检测到持久 refresh_pending：这些账户需要重新授权；部署会保留其状态，不重用旧刷新令牌。' }
        $taskBoundary = [bool]$taskStatus.boundaryProbe; $taskOld.Boundary = $taskBoundary
    }
    if ($taskDeployUi) { $taskOldUi = @(Get-CimInstance Win32_Process -Filter "Name='CodexPool.Floating.exe'" | Where-Object { (Test-PoolPath $_.ExecutablePath) -and $_.CommandLine -notmatch '(?:^|\s)--proxy-tray(?:\s|$)' } | ForEach-Object {
        $taskUiProcess = Get-Process -Id $_.ProcessId
        $taskOldSource = $null
        $taskOldManifest = Join-Path (Split-Path $_.ExecutablePath -Parent) 'source-build.txt'
        if ($_.ExecutablePath.StartsWith((Join-Path $taskRoot '.local\desktop') + '\', [StringComparison]::OrdinalIgnoreCase) -and (Test-Path -LiteralPath $taskOldManifest)) {
            $taskOldSource = [IO.File]::ReadAllText($taskOldManifest)
        }
        [pscustomobject]@{Id=$_.ProcessId;Path=$_.ExecutablePath;StartTicks=$taskUiProcess.StartTime.ToUniversalTime().Ticks;Tray=($_.CommandLine -match '(?:^|\s)--(?:tray|widget)(?:\s|$)');Source=$taskOldSource}
    }) }
    Write-Host "2/4 仅停止所选范围：$Target。"
    foreach ($taskUi in $taskOldUi) { Stop-OwnedProcess $taskUi; $taskUiStopped += $taskUi }
    if ($taskOld) { Stop-OwnedProcess $taskOld; $taskStopped = $true }
    Write-Host '3/4 启动所选版本并验证状态…'
    if ($taskDeployCore) {
        if (Get-NetTCPConnection -LocalPort 3210 -State Listen -ErrorAction SilentlyContinue) { throw '3210 仍被占用。' }
        $taskNew = Start-PoolDaemon $taskNewExe $taskBoundary
        $taskNewStatus = Wait-PoolDaemon $taskNew $taskNewExe
        if ($taskOld -and (Get-PoolSettingsSnapshot $taskNewStatus.settings) -ne (Get-PoolSettingsSnapshot $taskStatus.settings)) { throw '账户池设置校验失败。' }
    }
    if ($taskDeployUi) {
        $taskClient = Start-PoolDesktopClient -BuildDirectory $taskBuild -Tray -PassThru -NoDaemonStart:($Target -eq 'Components')
        $taskNewUi += [pscustomobject]@{Id=$taskClient.Id;Path=$taskClient.Path;StartTicks=$taskClient.StartTime.ToUniversalTime().Ticks}
        if ($taskClient.WaitForExit(500)) { throw '新托盘进程提前退出。' }
    }
    [IO.File]::WriteAllText($taskPointer + '.tmp', (@{path=$taskBuild;builtAt=[DateTimeOffset]::UtcNow.ToString('O')} | ConvertTo-Json -Compress))
    Move-Item -LiteralPath ($taskPointer + '.tmp') -Destination $taskPointer -Force
    Write-Host "4/4 $Target 部署完成，未选组件保持原版本。"
    if ($Dashboard -and $Target -ne 'Components') {
        try { & $taskNewExe dashboard; if ($LASTEXITCODE -ne 0) { Write-Warning '部署已完成，Dashboard 可稍后手动打开。' } }
        catch { Write-Warning '部署已完成，Dashboard 可稍后手动打开。' }
    }
    Write-Host "部署完成：$taskBuild"
} catch {
    $taskFailure = $_
    foreach ($taskUi in $taskNewUi) { try { Stop-OwnedProcess $taskUi } catch { Write-Warning '新界面未能自动退出。' } }
    if ($taskNew) {
        $taskNew.Refresh()
        if (-not $taskNew.HasExited) { Stop-Process -InputObject $taskNew -Force; [void]$taskNew.WaitForExit(10000) }
    }
    if ($taskStopped -and $taskOld) {
        try { $taskRestored = Start-PoolDaemon $taskOld.Path $taskOld.Boundary; $null = Wait-PoolDaemon $taskRestored $taskOld.Path; Write-Warning '部署失败，已启动旧版本。' }
        catch { Write-Warning ('旧版本自动恢复失败，请手动运行：' + $taskOld.Path + ' serve') }
    }
    foreach ($taskUi in $taskUiStopped) {
        try {
            # Old UI versions may not honor NoDaemonStart. Restore their files
            # without launching if doing so could start an unselected core.
            $taskCanRestoreUi = $Target -ne 'Components' -or [bool](Get-NetTCPConnection -LocalPort 3210 -State Listen -ErrorAction SilentlyContinue)
            if ($taskUi.Source) { Start-PoolDesktopClient -BuildDirectory $taskUi.Source -Tray:$taskUi.Tray -NoDaemonStart:($Target -eq 'Components') -InstallOnly:(-not $taskCanRestoreUi) }
            elseif ($taskCanRestoreUi) { Start-PoolUi $taskUi.Path $taskUi.Tray }
            if (-not $taskCanRestoreUi) { Write-Warning '已保留或恢复旧组件文件；核心未运行，未重启可能自动拉起核心的旧界面。' }
        } catch { Write-Warning '旧界面恢复失败，请从上一版本目录手动启动。' }
    }
    throw $taskFailure
} finally {
    if ($taskOwned) { $taskMutex.ReleaseMutex() }
    $taskMutex.Dispose()
}

