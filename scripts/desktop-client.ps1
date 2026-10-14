# Shared launcher: preserve Windows notification-area identity across versioned builds.
# This file is dot-sourced. It never stops a UI or daemon process.
function Start-PoolDesktopClient {
    param([Parameter(Mandatory)][string]$BuildDirectory, [switch]$Tray = $true, [switch]$PassThru, [switch]$NoDaemonStart, [switch]$InstallOnly)
    $taskClientRoot = [IO.Path]::GetFullPath((Split-Path $PSScriptRoot -Parent))
    $taskSourceBuild = [IO.Path]::GetFullPath($BuildDirectory)
    $taskLocalRoot = Join-Path $taskClientRoot '.local'
    if (-not $taskSourceBuild.StartsWith($taskLocalRoot + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'UI 构建必须位于当前仓库 .local 内。' }
    $taskKind = if ($Tray) { 'Widget' } else { 'Floating' }
    $taskSource = Join-Path $taskSourceBuild $taskKind
    $taskDesktop = Join-Path $taskLocalRoot 'desktop'
    $taskStable = Join-Path $taskDesktop $taskKind
    $taskExecutable = Join-Path $taskStable 'CodexPool.Floating.exe'
    if (-not (Test-Path -LiteralPath (Join-Path $taskSource 'CodexPool.Floating.exe'))) { throw 'UI 构建不完整。' }
    # Serialize copying and launch. Separate roots may coexist, but a root has one installation.
    $taskIdentity = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($taskDesktop.ToLowerInvariant())))
    $taskClientMutex = [Threading.Mutex]::new($false, ('Local\CodexPoolDesktop-' + $taskIdentity))
    $taskClientOwned = $false
    try {
        try { $taskClientOwned = $taskClientMutex.WaitOne(10000) } catch [Threading.AbandonedMutexException] { $taskClientOwned = $true }
        if (-not $taskClientOwned) { throw '另一个界面启动正在进行，请稍后重试。' }
        foreach ($taskPath in @($taskLocalRoot, $taskDesktop, $taskStable)) {
            if ((Test-Path -LiteralPath $taskPath) -and ((Get-Item -LiteralPath $taskPath).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'UI 安装路径不能是链接或联接。' }
        }
        $taskRunning = @(Get-CimInstance Win32_Process -Filter "Name='CodexPool.Floating.exe'" | Where-Object {
            $_.ExecutablePath -and [IO.Path]::GetFullPath($_.ExecutablePath).StartsWith($taskStable + '\', [StringComparison]::OrdinalIgnoreCase)
        })
        $taskManifest = Join-Path $taskStable 'source-build.txt'
        $taskInstalled = if (Test-Path -LiteralPath $taskManifest) { [IO.File]::ReadAllText($taskManifest) } else { '' }
        if ($taskInstalled -ne $taskSourceBuild) {
            if ($taskRunning.Count -gt 0) { Write-Warning '旧界面仍在运行，已唤醒旧界面。请退出该 UI 后再次启动以更新；代理无需退出。' }
            else {
                $null = New-Item -ItemType Directory -Path $taskDesktop -Force
                $taskStaged = Join-Path $taskDesktop ('.' + $taskKind + '-stage-' + [Guid]::NewGuid().ToString('N'))
                $taskBackup = Join-Path $taskDesktop ('.' + $taskKind + '-backup-' + [Guid]::NewGuid().ToString('N'))
                # All move targets are resolved siblings inside this checkout's .local/desktop.
                foreach ($taskTarget in @($taskStable,$taskStaged,$taskBackup)) {
                    if (-not [IO.Path]::GetFullPath($taskTarget).StartsWith($taskDesktop + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'UI 安装目标越界。' }
                }
                $null = New-Item -ItemType Directory -Path $taskStaged
                foreach ($taskFile in Get-ChildItem -LiteralPath $taskSource -Force) { Copy-Item -LiteralPath $taskFile.FullName -Destination $taskStaged -Recurse -Force }
                [IO.File]::WriteAllText((Join-Path $taskStaged 'source-build.txt'), $taskSourceBuild)
                if (Test-Path -LiteralPath $taskStable) { Move-Item -LiteralPath $taskStable -Destination $taskBackup }
                try { Move-Item -LiteralPath $taskStaged -Destination $taskStable }
                catch {
                    if (Test-Path -LiteralPath $taskBackup) { Move-Item -LiteralPath $taskBackup -Destination $taskStable }
                    throw
                }
            }
        }
        if ($InstallOnly) { return }
        $taskPreviousDaemon = [Environment]::GetEnvironmentVariable('CODEX_POOL_DAEMON')
        $taskPreviousAutoStart = [Environment]::GetEnvironmentVariable('CODEX_POOL_DAEMON_AUTOSTART')
        try {
            # The proxy remains versioned and independent; only the tray UI needs a fixed path.
            [Environment]::SetEnvironmentVariable('CODEX_POOL_DAEMON', (Join-Path $taskSourceBuild 'Daemon\CodexPool.Daemon.exe'))
            if ($NoDaemonStart) { [Environment]::SetEnvironmentVariable('CODEX_POOL_DAEMON_AUTOSTART', '0') }
            if ($Tray) { $taskStartedClient = Start-Process -FilePath $taskExecutable -ArgumentList '--tray' -WorkingDirectory $taskStable -WindowStyle Hidden -PassThru }
            else { $taskStartedClient = Start-Process -FilePath $taskExecutable -WorkingDirectory $taskStable -WindowStyle Normal -PassThru }
            if ($PassThru) { return $taskStartedClient }
        } finally {
            [Environment]::SetEnvironmentVariable('CODEX_POOL_DAEMON', $taskPreviousDaemon)
            [Environment]::SetEnvironmentVariable('CODEX_POOL_DAEMON_AUTOSTART', $taskPreviousAutoStart)
        }
    } finally {
        if ($taskClientOwned) { $taskClientMutex.ReleaseMutex() }
        $taskClientMutex.Dispose()
    }
}
