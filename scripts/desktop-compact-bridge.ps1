#requires -Version 7.0
param([string]$CodexPath, [string]$DesktopPath, [string]$BridgePath, [switch]$CheckOnly, [switch]$PassThru, [switch]$ReuseRunning)
$ErrorActionPreference = 'Stop'
$taskRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
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
    $taskNativeDesktop = Join-Path $taskPackage.InstallLocation 'app/Codex.exe'
    if (Test-Path -LiteralPath $taskNativeDesktop) { $taskNativeDesktop } else {
        $taskManifest = $taskPackage | Get-AppxPackageManifest
        $taskApplication = @($taskManifest.Package.Applications.Application | Where-Object Id -EQ 'App')
        if ($taskApplication.Count -ne 1) { throw '无法唯一确定 Desktop 入口，请用 -DesktopPath 指定。' }
        Join-Path $taskPackage.InstallLocation $taskApplication[0].Executable
    }
}
if (-not (Test-Path -LiteralPath $taskDesktop)) { throw 'Desktop 可执行文件不存在。' }
$taskDesktopFolder = Split-Path $taskDesktop -Parent
$taskDesktopCandidates = @($taskDesktop, (Join-Path $taskDesktopFolder 'Codex.exe'), (Join-Path $taskDesktopFolder 'ChatGPT.exe'))
$taskDesktopProcesses = @(Get-CimInstance Win32_Process -Filter "Name='Codex.exe' OR Name='ChatGPT.exe'" | Where-Object {
    $_.ExecutablePath -and $taskDesktopCandidates -contains $_.ExecutablePath
})
$taskBridgeProcesses = @(Get-CimInstance Win32_Process -Filter "Name='CodexPool.Bridge.exe'" | Where-Object {
    $_.ExecutablePath -and $_.ExecutablePath.Equals($taskBridge,[StringComparison]::OrdinalIgnoreCase) -and
    $taskDesktopProcesses.ProcessId -contains $_.ParentProcessId
})
$taskAlreadyBridged = $taskDesktopProcesses.Count -gt 0 -and $taskBridgeProcesses.Count -gt 0
if ($CheckOnly) {
    if ($PassThru) { return [pscustomobject]@{ DesktopPath=$taskDesktop; CodexPath=$taskCodex; BridgePath=$taskBridge; DesktopRunning=($taskDesktopProcesses.Count -gt 0); RunningThroughBridge=$taskAlreadyBridged } }
    Write-Host '启动入口检查通过；没有重启 Desktop、改写配置或触发模型调用。'; return
}
if ($taskAlreadyBridged -and $ReuseRunning) { Write-Host 'Codex Desktop 已通过当前桥接运行，保留现有实例。'; return }
if ($taskDesktopProcesses.Count -gt 0) {
    throw '请先结束工作并退出 Codex Desktop。启动覆盖仅对新进程生效；脚本不会停止现有实例。'
}
$taskNames = @('CODEX_CLI_PATH','CODEX_POOL_UPSTREAM_CLI','CODEX_POOL_COMPACT_BRIDGE','CODEX_APP_SERVER_FORCE_CLI')
$taskPrevious = @{}
foreach ($taskName in $taskNames) { $taskPrevious[$taskName] = [Environment]::GetEnvironmentVariable($taskName,'Process') }
try {
    $env:CODEX_CLI_PATH = $taskBridge
    $env:CODEX_POOL_UPSTREAM_CLI = $taskCodex
    $env:CODEX_POOL_COMPACT_BRIDGE = '1'
    $env:CODEX_APP_SERVER_FORCE_CLI = '1'
    Start-Process -FilePath $taskDesktop -WindowStyle Normal
    Write-Host 'Desktop 已通过实验桥接启动。Daemon 须显式开启实验能力；原有模型 provider 配置继续生效。'
} finally {
    foreach ($taskName in $taskNames) {
        $taskRestore = if ($null -eq $taskPrevious[$taskName]) { [NullString]::Value } else { $taskPrevious[$taskName] }
        [Environment]::SetEnvironmentVariable($taskName,$taskRestore,'Process')
    }
}
