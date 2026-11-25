#requires -Version 7.0
param([Parameter(Mandatory)][string]$Archive, [Parameter(Mandatory)][string]$ExpectedVersion)
$ErrorActionPreference = 'Stop'
$taskRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$taskSandbox = Join-Path $taskRoot ('.research/package smoke ' + [Guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory -Path $taskSandbox
Expand-Archive -LiteralPath $Archive -DestinationPath $taskSandbox
$taskApp = Join-Path $taskSandbox "CodexPool-$ExpectedVersion-win-x64"
foreach ($taskFile in @('CodexPool.exe','CodexPool.Floating.dll','CodexPool.Floating.runtimeconfig.json','coreclr.dll','PresentationFramework.dll','codex-pool.json','Daemon/CodexPool.Daemon.exe','Daemon/coreclr.dll','Daemon/e_sqlite3.dll','使用说明.md')) {
    if (-not (Test-Path -LiteralPath (Join-Path $taskApp $taskFile))) { throw "发行包缺失：$taskFile" }
}
$taskManifest = Get-Content -LiteralPath (Join-Path $taskApp 'codex-pool.json') -Raw | ConvertFrom-Json
if ($taskManifest.version -cne $ExpectedVersion -or $taskManifest.runtime -ne 'win-x64') { throw '发行包版本或架构错误。' }
if (Get-ChildItem -LiteralPath $taskApp -Recurse -File | Where-Object { $_.Name -match '^(?:auth\.json|vault\.dpapi|usage\.(?:dpapi|sqlite)|widget\.json)$' }) { throw '发行包含有运行期用户数据。' }
$taskTools = Join-Path $taskApp 'QuickSettings'
foreach ($script in @('Deploy-QuickSettings','Start-QuickSettings','Stop-QuickSettings','Set-QuickSettingsPinned','Get-QuickSettingsOperation','Uninstall-QuickSettings')) {
    if (!(Test-Path -LiteralPath (Join-Path $taskTools ($script+'.ps1')) -PathType Leaf)) { throw "发行包缺少磁贴管理脚本 $script" }
}
# Load the packaged module in the child process below.
$taskProvider = Join-Path $taskSandbox 'staged provider'
Set-Content -LiteralPath (Join-Path $taskApp 'Daemon/unlisted-test-file') -Value 'test'
# Windows PowerShell 5.1 is the packaged installer's minimum supported host.
$taskStageProvider = Join-Path $taskSandbox 'stage-provider.ps1'
@'
param([string]$Application, [string]$Destination)
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $Application 'QuickSettings/QuickSettingsLifecycle.psm1') -DisableNameChecking
Copy-QsePortableProvider $Application $Destination
'@ | Set-Content -LiteralPath $taskStageProvider -Encoding utf8
& powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File $taskStageProvider $taskApp $taskProvider
if ($LASTEXITCODE) { throw 'Windows PowerShell 无法从发行包准备 Provider。' }
if (Test-Path -LiteralPath (Join-Path $taskProvider 'unlisted-test-file')) { throw 'Provider 意外复制了非发布文件。' }
if ((Get-Content (Join-Path $taskProvider 'qse-manifest.json') -Raw | ConvertFrom-Json).id -ne 'codex-pool') { throw '磁贴声明无效。' }
$taskData = Join-Path $taskSandbox 'must-not-be-created'
$taskResult = Join-Path $taskSandbox 'check.json'
$taskStart = [Diagnostics.ProcessStartInfo]::new((Join-Path $taskApp 'CodexPool.exe'))
$taskStart.UseShellExecute = $false; $taskStart.CreateNoWindow = $true; $taskStart.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
$taskStart.Environment['CODEX_POOL_DATA_DIR'] = $taskData
$taskStart.Environment['DOTNET_ROOT'] = (Join-Path $taskSandbox 'no-dotnet-installed')
$taskStart.Environment['DOTNET_MULTILEVEL_LOOKUP'] = '0'
$taskStart.ArgumentList.Add('--package-check'); $taskStart.ArgumentList.Add($taskResult)
$taskProcess = [Diagnostics.Process]::Start($taskStart)
try {
    if (-not $taskProcess.WaitForExit(30000) -or $taskProcess.ExitCode -ne 0) { throw '自包含 GUI 启动检查失败。' }
} finally { $taskProcess.Dispose() }
if (-not (Test-Path -LiteralPath $taskResult) -or (Get-Content -LiteralPath $taskResult -Raw | ConvertFrom-Json).package -ne 'ok') { throw '未收到离线启动检查结果。' }
$taskStart.FileName = Join-Path $taskApp 'Daemon/CodexPool.Daemon.exe'
$taskStart.ArgumentList.Clear(); $taskStart.ArgumentList.Add('help'); $taskStart.RedirectStandardOutput = $true
$taskProcess = [Diagnostics.Process]::Start($taskStart)
try {
    $taskHelp = $taskProcess.StandardOutput.ReadToEndAsync()
    if (-not $taskProcess.WaitForExit(30000) -or $taskProcess.ExitCode -ne 0 -or $taskHelp.GetAwaiter().GetResult() -notmatch 'serve') { throw '自包含 Daemon 离线检查失败。' }
} finally { $taskProcess.Dispose() }
$taskStart.FileName = Join-Path $taskProvider 'CodexPool.Daemon.exe'
$taskProcess = [Diagnostics.Process]::Start($taskStart)
try {
    $taskHelp = $taskProcess.StandardOutput.ReadToEndAsync()
    if (!$taskProcess.WaitForExit(30000) -or $taskProcess.ExitCode -ne 0 -or $taskHelp.GetAwaiter().GetResult() -notmatch 'serve') { throw '磁贴 Provider 缺少自包含运行文件。' }
} finally { $taskProcess.Dispose() }
if (Test-Path -LiteralPath $taskData) { throw '离线检查意外创建用户数据。' }
Write-Output 'PASS 自包含 GUI / Daemon、完整文件、版本、架构、无用户数据；没有启动托盘、代理服务或模型请求。'
