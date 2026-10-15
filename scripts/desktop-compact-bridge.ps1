#requires -Version 7.0
param([string]$CodexPath, [string]$DesktopPath, [string]$BridgePath, [switch]$CheckOnly, [switch]$PassThru, [switch]$ReuseRunning,
    [ValidateRange(1,120)][int]$StartupTimeoutSeconds = 60)
$ErrorActionPreference = 'Stop'
$taskRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
. (Join-Path $PSScriptRoot 'desktop-launch.ps1')
$taskCodex = if ($CodexPath) { [IO.Path]::GetFullPath($CodexPath) } else { (Get-Command codex.exe -CommandType Application -ErrorAction Stop | Select-Object -First 1).Source }
if (-not (Test-Path -LiteralPath $taskCodex) -or [IO.Path]::GetExtension($taskCodex) -ne '.exe') { throw '请用 -CodexPath 指定官方原生 codex.exe。' }
$taskVersion = & $taskCodex --version
if ($LASTEXITCODE -ne 0 -or "$taskVersion".Trim() -ne 'codex-cli 0.162.1') { throw '当前桥接只验证过官方 CLI 0.162.1。' }
$taskBridge = if ($BridgePath) { [IO.Path]::GetFullPath($BridgePath) } else {
    $taskPointer = Join-Path $taskRoot '.local/compact-bridge/current-build.json'
    if (-not (Test-Path -LiteralPath $taskPointer)) { throw '请先运行 scripts/build-compact-bridge.ps1。' }
    Join-Path (Get-Content -LiteralPath $taskPointer -Raw | ConvertFrom-Json).path 'CodexPool.Bridge.exe'
}
if (-not (Test-Path -LiteralPath $taskBridge) -or $taskBridge.Equals($taskCodex,[StringComparison]::OrdinalIgnoreCase)) { throw '桥接或官方 CLI 路径无效。' }
$taskDesktop = if ($DesktopPath) { [IO.Path]::GetFullPath($DesktopPath) } else {
    $taskPackage = Get-AppxPackage -Name OpenAI.Codex
    if (-not $taskPackage -or @($taskPackage).Count -ne 1) { throw '请用 -DesktopPath 指定 Codex Desktop 可执行文件。' }
    Resolve-PoolDesktopExecutable -Package $taskPackage -Manifest ($taskPackage | Get-AppxPackageManifest)
}
if (-not (Test-Path -LiteralPath $taskDesktop)) { throw 'Desktop 可执行文件不存在。' }
$taskDesktopFolder = Split-Path $taskDesktop -Parent
$taskDesktopCandidates = @($taskDesktop, (Join-Path $taskDesktopFolder 'Codex.exe'), (Join-Path $taskDesktopFolder 'ChatGPT.exe'))
function Read-PoolDesktopProcesses {
    @(Get-CimInstance Win32_Process -Filter "Name='Codex.exe' OR Name='ChatGPT.exe'" | Where-Object {
    $_.ExecutablePath -and $taskDesktopCandidates -contains $_.ExecutablePath
    })
}
function Read-PoolBridgeProcesses($DesktopProcesses) {
    @(Get-CimInstance Win32_Process -Filter "Name='CodexPool.Bridge.exe'" | Where-Object {
    $_.ExecutablePath -and $_.ExecutablePath.Equals($taskBridge,[StringComparison]::OrdinalIgnoreCase) -and
    $DesktopProcesses.ProcessId -contains $_.ParentProcessId
    })
}
$taskDesktopProcesses = @(Read-PoolDesktopProcesses)
$taskBridgeProcesses = @(Read-PoolBridgeProcesses $taskDesktopProcesses)
$taskAlreadyBridged = $taskDesktopProcesses.Count -gt 0 -and $taskBridgeProcesses.Count -gt 0
if ($CheckOnly) {
    if ($PassThru) { return [pscustomobject]@{ DesktopPath=$taskDesktop; CodexPath=$taskCodex; BridgePath=$taskBridge; DesktopRunning=($taskDesktopProcesses.Count -gt 0); RunningThroughBridge=$taskAlreadyBridged } }
    Write-Host '启动入口检查通过；没有重启 Desktop、改写配置或触发模型调用。'; return
}
if ($taskAlreadyBridged -and $ReuseRunning) { Write-Host 'Codex Desktop 已通过当前桥接运行，保留现有实例。'; return }
if ($taskDesktopProcesses.Count -gt 0) {
    throw '请先结束工作并退出 Codex Desktop。启动覆盖仅对新进程生效；脚本不会停止现有实例。'
}
$taskStart = New-PoolDesktopStartInfo -DesktopPath $taskDesktop -CodexPath $taskCodex -BridgePath $taskBridge
$taskChild = Start-PoolDesktopProcess -StartInfo $taskStart
if (-not $taskChild) { throw 'Desktop 进程未能创建。' }
Write-Host '已请求启动 Desktop，正在等待桥接连接…'
$taskDeadline = [DateTimeOffset]::UtcNow.AddSeconds($StartupTimeoutSeconds)
try {
    do {
        $taskObservedDesktop = @(Read-PoolDesktopProcesses)
        $taskObservedBridges = @(Read-PoolBridgeProcesses $taskObservedDesktop)
        if ($taskObservedBridges.Count -gt 0) {
            Write-Host 'Desktop 和实验桥接已启动。原有模型 provider、账户和阈值继续生效。'
            return
        }
        $taskChild.Refresh()
        if ($taskChild.HasExited -and $taskObservedDesktop.Count -eq 0) {
            throw "Desktop 在桥接建立前退出（退出码 $($taskChild.ExitCode)）。请检查应用启动状态；没有自动重试或结束其他进程。"
        }
        Start-Sleep -Milliseconds 500
    } while ([DateTimeOffset]::UtcNow -lt $taskDeadline)
    if ($taskObservedDesktop.Count -gt 0) {
        throw 'Desktop 进程已启动，但未连接所选桥接。请先结束工作并完全退出 Desktop 后重试 Codex Pool；没有自动停止现有进程。'
    }
    throw 'Desktop 未在预期时间内启动。请检查应用安装状态；没有自动重试或结束其他进程。'
} finally {
    $taskChild.Dispose()
}
