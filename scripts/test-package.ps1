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
if (Test-Path -LiteralPath $taskData) { throw '离线检查意外创建用户数据。' }
Write-Output 'PASS 自包含 GUI / Daemon、完整文件、版本、架构、无用户数据；没有启动托盘、代理服务或模型请求。'
