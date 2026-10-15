#requires -Version 7.0
$ErrorActionPreference = 'Stop'
$taskRepository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$taskSandbox = Join-Path $taskRepository ('.research/desktop-launch-' + [Guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory -Path (Join-Path $taskSandbox 'scripts') -Force
$null = New-Item -ItemType Directory -Path (Join-Path $taskSandbox 'package/app') -Force
$taskDesktop = Join-Path $taskSandbox 'package/app/ChatGPT.exe'
$taskShim = Join-Path $taskSandbox 'package/app/Codex.exe'
$taskBridge = Join-Path $taskSandbox 'CodexPool.Bridge.exe'
foreach ($taskPath in @($taskDesktop,$taskShim,$taskBridge)) { [IO.File]::WriteAllText($taskPath,'fixture-not-executable') }
. (Join-Path $taskRepository 'scripts/desktop-launch.ps1')
function Assert-Launch($Value, [string]$Message) { if (-not $Value) { throw $Message } }
$taskPackage = [pscustomobject]@{InstallLocation=(Join-Path $taskSandbox 'package')}
$taskManifest = [pscustomobject]@{Package=[pscustomobject]@{Applications=[pscustomobject]@{Application=@([pscustomobject]@{Id='App';Executable='app/ChatGPT.exe'})}}}
Assert-Launch ((Resolve-PoolDesktopExecutable $taskPackage $taskManifest) -eq $taskDesktop) 'Manifest application must win over a present Codex.exe shim.'
Write-Host 'PASS actual manifest entry selected even when Codex.exe shim exists'
$taskOutsideManifest = [pscustomobject]@{Package=[pscustomobject]@{Applications=[pscustomobject]@{Application=@([pscustomobject]@{Id='App';Executable='../outside.exe'})}}}
$taskRejected=$false
try { $null=Resolve-PoolDesktopExecutable $taskPackage $taskOutsideManifest } catch { $taskRejected=$true }
Assert-Launch $taskRejected 'Out-of-package manifest path accepted'
$taskPrior = [Environment]::GetEnvironmentVariable('CODEX_CLI_PATH','Process')
$taskStart = New-PoolDesktopStartInfo $taskDesktop 'C:\fixture\codex.exe' $taskBridge
Assert-Launch (-not $taskStart.UseShellExecute -and $taskStart.FileName -eq $taskDesktop) 'Desktop must start directly without shell activation'
Assert-Launch ($taskStart.Environment['CODEX_CLI_PATH'] -eq $taskBridge -and $taskStart.Environment['CODEX_POOL_COMPACT_BRIDGE'] -eq '1' -and
    $taskStart.Environment['CODEX_APP_SERVER_FORCE_CLI'] -eq '1' -and $taskStart.Environment['CODEX_POOL_UPSTREAM_CLI'] -eq 'C:\fixture\codex.exe') 'Missing child bridge environment'
Assert-Launch ([Environment]::GetEnvironmentVariable('CODEX_CLI_PATH','Process') -eq $taskPrior) 'Parent environment changed'
Write-Host 'PASS shell activation disabled; bridge environment belongs only to child'

# Synthetic copy: intercept process creation/inventory. No executable/server/UI is started.
Copy-Item -LiteralPath (Join-Path $taskRepository 'scripts/desktop-compact-bridge.ps1') -Destination (Join-Path $taskSandbox 'scripts')
Copy-Item -LiteralPath (Join-Path $taskRepository 'scripts/desktop-launch.ps1') -Destination (Join-Path $taskSandbox 'scripts')
Add-Content -LiteralPath (Join-Path $taskSandbox 'scripts/desktop-launch.ps1') -Value @'
function Start-PoolDesktopProcess {
    param($StartInfo)
    $taskFixture.Starts++
    $taskFixture.Started=$true
    $taskFixture.LastInfo=$StartInfo
    $taskProcess=[pscustomobject]@{HasExited=($taskFixture.Mode -eq 'exit');ExitCode=37}
    $taskProcess | Add-Member ScriptMethod Refresh { }
    $taskProcess | Add-Member ScriptMethod Dispose { $taskFixture.Disposed++ }
    return $taskProcess
}
'@
function Get-AppxPackage { param($Name) return $taskPackage }
function Get-AppxPackageManifest { param([Parameter(ValueFromPipeline)]$InputObject) return $taskManifest }
function Get-CimInstance {
    param($ClassName,$Filter)
    if ($Filter.Contains("Name='CodexPool.Bridge.exe'")) {
        if ($taskFixture.Mode -eq 'existing-bridge' -or ($taskFixture.Mode -eq 'ready' -and $taskFixture.Started)) {
            [pscustomobject]@{ExecutablePath=$taskBridge;ParentProcessId=501;ProcessId=502}
        }
    } elseif ($taskFixture.Mode -in @('existing-plain','existing-bridge') -or $taskFixture.Started -and $taskFixture.Mode -ne 'exit') {
        [pscustomobject]@{ExecutablePath=$taskDesktop;ProcessId=501}
    }
}
# A local function satisfies the CLI version probe without running any native program.
$taskCli = Join-Path $taskSandbox 'codex.exe'
[IO.File]::WriteAllText($taskCli,'fixture-not-executable')
Set-Item -LiteralPath ('Function:' + $taskCli) -Value { param($Version) $global:LASTEXITCODE=0; 'codex-cli 0.162.1' }
$taskFixture=[pscustomobject]@{Mode='ready';Starts=0;Started=$false;Disposed=0;LastInfo=$null}
$taskLauncher=Join-Path $taskSandbox 'scripts/desktop-compact-bridge.ps1'
$taskArguments=@{CodexPath=$taskCli;BridgePath=$taskBridge;StartupTimeoutSeconds=1}
$taskCheck=& $taskLauncher @taskArguments -CheckOnly -PassThru
Assert-Launch ($taskCheck.DesktopPath -eq $taskDesktop -and $taskFixture.Starts -eq 0) 'CheckOnly launched or selected shim'
& $taskLauncher @taskArguments
Assert-Launch ($taskFixture.Starts -eq 1 -and $taskFixture.Disposed -eq 1 -and $taskFixture.LastInfo.FileName -eq $taskDesktop) 'Ready launch failed'
Assert-Launch ([Environment]::GetEnvironmentVariable('CODEX_CLI_PATH','Process') -eq $taskPrior) 'Launch altered parent environment'
Write-Host 'PASS checked startup waits for matching Desktop bridge without changing parent environment'
foreach ($taskMode in @('existing-bridge','existing-plain','exit','timeout')) {
    $taskFixture.Mode=$taskMode; $taskFixture.Starts=0; $taskFixture.Started=$false; $taskFixture.Disposed=0
    $taskFailure=''
    try { & $taskLauncher @taskArguments -ReuseRunning } catch { $taskFailure=$_.Exception.Message }
    switch ($taskMode) {
        'existing-bridge' { Assert-Launch ($taskFailure -eq '' -and $taskFixture.Starts -eq 0) "Existing bridge not reused: $taskFailure" }
        'existing-plain' { Assert-Launch ($taskFailure -ne '' -and $taskFixture.Starts -eq 0) 'Ordinary Desktop must reject before launching' }
        'exit' { Assert-Launch ($taskFailure.Contains('37') -and $taskFixture.Disposed -eq 1) 'Early exit lost specific diagnosis' }
        'timeout' { Assert-Launch ($taskFailure.Contains('未连接所选桥接') -and $taskFixture.Disposed -eq 1 -and $taskFixture.Starts -eq 1) 'Timeout misreported success or retried' }
    }
    Write-Host "PASS launch outcome: $taskMode"
}
Write-Host '8/8 Desktop launch checks passed; synthetic processes only'
