#requires -Version 7.0
param([string]$CodexPath, [string]$DesktopPath, [string]$BridgePath, [switch]$CheckOnly)
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
if ($CheckOnly) { Write-Host '启动入口检查通过；没有重启 Desktop、改写配置或触发模型调用。'; return }
if (Get-Process -Name Codex -ErrorAction SilentlyContinue | Where-Object { $_.Path -and $_.Path.Equals($taskDesktop,[StringComparison]::OrdinalIgnoreCase) }) {
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
    foreach ($taskName in $taskNames) { [Environment]::SetEnvironmentVariable($taskName,$taskPrevious[$taskName],'Process') }
}
