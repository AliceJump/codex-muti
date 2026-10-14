#requires -Version 7.0
$ErrorActionPreference = 'Stop'
$taskRepository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$taskFixtures = Join-Path $taskRepository ('.research/deployment-targets-' + [Guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory -Path $taskFixtures
function Assert-Test($Condition, [string]$Message) { if (-not $Condition) { throw $Message } }
function New-TestFile([string]$Path, [string]$Value) {
    $null = New-Item -ItemType Directory -Path (Split-Path $Path -Parent) -Force
    [IO.File]::WriteAllText($Path, $Value)
}
function New-TestRoot([string]$Name) {
    $taskSandbox = Join-Path $taskFixtures $Name
    $null = New-Item -ItemType Directory -Path (Join-Path $taskSandbox 'scripts') -Force
    foreach ($taskFile in @('build-release.ps1','build-widget.ps1','pool.ps1','mcodex.ps1','deploy-local.ps1')) {
        Copy-Item -LiteralPath (Join-Path $taskRepository "scripts/$taskFile") -Destination (Join-Path $taskSandbox 'scripts')
    }
    foreach ($taskKind in @('Daemon','Widget','Floating')) {
        $taskExe = if ($taskKind -eq 'Daemon') { 'CodexPool.Daemon.exe' } else { 'CodexPool.Floating.exe' }
        New-TestFile (Join-Path $taskSandbox ".local/base/$taskKind/$taskExe") "old-$taskKind"
    }
    New-TestFile (Join-Path $taskSandbox '.local/current-build.json') (@{path=(Join-Path $taskSandbox '.local/base')} | ConvertTo-Json -Compress)
    return $taskSandbox
}
# Publish is the only external operation in the real build scripts.
function global:dotnet {
    $taskOutputIndex = [Array]::IndexOf($args, '-o')
    $taskOutput = $args[$taskOutputIndex + 1]
    $taskKind = Split-Path $taskOutput -Leaf
    $global:taskPublishCalls.Add($taskKind)
    $taskExe = if ($taskKind -eq 'Daemon') { 'CodexPool.Daemon.exe' } else { 'CodexPool.Floating.exe' }
    New-TestFile (Join-Path $taskOutput $taskExe) "new-$taskKind"
    $global:LASTEXITCODE = 0
}
foreach ($taskTarget in @('All','Core','Components')) {
    $taskSandbox = New-TestRoot "build-$taskTarget"
    $taskOutput = Join-Path $taskSandbox '.local/new'
    $global:taskPublishCalls = [Collections.Generic.List[string]]::new()
    & (Join-Path $taskSandbox 'scripts/build-release.ps1') -Target $taskTarget -BaseDirectory (Join-Path $taskSandbox '.local/base') -OutputDirectory $taskOutput
    $taskExpected = if ($taskTarget -eq 'Core') { 'Daemon' } elseif ($taskTarget -eq 'Components') { 'Widget' } else { 'Daemon,Widget' }
    Assert-Test (($global:taskPublishCalls -join ',') -eq $taskExpected) "Wrong publish scope: $taskTarget"
    foreach ($taskKind in @('Daemon','Widget')) {
        $taskExe = if ($taskKind -eq 'Daemon') { 'CodexPool.Daemon.exe' } else { 'CodexPool.Floating.exe' }
        $taskValue = Get-Content -LiteralPath (Join-Path $taskOutput "$taskKind/$taskExe") -Raw
        $taskRetained = ($taskTarget -eq 'Core' -and $taskKind -ne 'Daemon') -or ($taskTarget -eq 'Components' -and $taskKind -eq 'Daemon')
        $taskExpectedValue = "$(if ($taskRetained) {'old'} else {'new'})-$taskKind"
        Assert-Test ($taskValue -eq $taskExpectedValue) "Wrong retained version: $taskTarget/$taskKind actual=$taskValue expected=$taskExpectedValue retained=$taskRetained"
    }
    Assert-Test (-not (Test-Path -LiteralPath (Join-Path $taskOutput 'Floating'))) 'Removed floating UI was still published or retained.'
    Write-Output "PASS build scope and retained binaries: $taskTarget"
}
$taskSandbox = New-TestRoot 'no-baseline'
$global:taskPublishCalls.Clear()
$taskRejected = $false
try { & (Join-Path $taskSandbox 'scripts/build-release.ps1') -Target Components -OutputDirectory (Join-Path $taskSandbox '.local/new') }
catch { $taskRejected = $true }
Assert-Test ($taskRejected -and $global:taskPublishCalls.Count -eq 0) 'Partial first deployment must fail before publishing.'
Write-Output 'PASS partial deployment requires a complete baseline'

# The real deployment orchestration runs with process/status IO replaced in an
# isolated copy. No native process, proxy, account store or UI is touched.
function New-TestProcess([int]$Id, [string]$Path, [bool]$Exited = $false) {
    $taskProcess = [pscustomobject]@{Id=$Id;Path=$Path;ProcessName=[IO.Path]::GetFileNameWithoutExtension($Path);StartTime=[DateTime]::UtcNow.AddSeconds($Id);HasExited=$Exited}
    $taskProcess | Add-Member ScriptMethod Refresh { }
    $taskProcess | Add-Member ScriptMethod WaitForExit { param($Milliseconds) return $this.HasExited }
    $global:taskProcesses[$Id] = $taskProcess
    return $taskProcess
}
function global:git { 'worktree ' + $global:taskSandbox; $global:LASTEXITCODE = 0 }
function global:Get-NetTCPConnection {
    param($LocalPort,$State,$ErrorAction)
    if ($global:taskCore -and -not $global:taskCore.HasExited) { [pscustomobject]@{OwningProcess=$global:taskCore.Id} }
}
function global:Get-Process {
    param($Id,$ErrorAction)
    $taskProcess = $global:taskProcesses[[int]$Id]
    if ($taskProcess -and -not $taskProcess.HasExited) { return $taskProcess }
}
function global:Stop-Process {
    param($InputObject,[switch]$Force)
    $global:taskTrace.Add("stop:$($InputObject.Id)"); $InputObject.HasExited = $true
}
function global:Get-CimInstance {
    param($ClassName,$Filter)
    foreach ($taskId in @(201,202)) {
        $taskProcess = $global:taskProcesses[$taskId]
        [pscustomobject]@{ProcessId=$taskId;ExecutablePath=$taskProcess.Path;CommandLine=$(if ($taskId -eq 201) {'--tray'} else {''})}
    }
}
function global:Start-Process {
    param($FilePath,$ArgumentList,$WorkingDirectory,$WindowStyle,[switch]$PassThru)
    Assert-Test ($WindowStyle -eq 'Hidden') 'Core must start hidden.'
    $global:taskTrace.Add('start:core')
    $global:taskCore = New-TestProcess 301 $FilePath ($global:taskFailure -eq 'Core' -and $FilePath -notmatch '[\\/]base[\\/]')
    return $global:taskCore
}
function global:Invoke-RestMethod { param($Uri,[switch]$NoProxy,$TimeoutSec) [pscustomobject]@{status='running'} }
foreach ($taskCase in @('All','Core','Components','FailCore','FailComponents','FailComponentsOffline')) {
    $global:taskSandbox = New-TestRoot "deploy-$taskCase"
    $global:taskTrace = [Collections.Generic.List[string]]::new()
    $global:taskProcesses = @{}
    $global:taskFailure = if ($taskCase -eq 'FailCore') {'Core'} elseif ($taskCase.StartsWith('FailComponents')) {'Components'} else {''}
    $taskTarget = if ($taskCase -eq 'FailComponentsOffline') {'Components'} else {$taskCase.Replace('Fail','')}
    $global:taskCore = New-TestProcess 101 (Join-Path $taskSandbox '.local/base/Daemon/CodexPool.Daemon.exe') ($taskCase -eq 'FailComponentsOffline')
    foreach ($taskId in @(201,202)) {
        $taskKind = if ($taskId -eq 201) {'Widget'} else {'Floating'}
        $null = New-TestProcess $taskId (Join-Path $taskSandbox ".local/desktop/$taskKind/CodexPool.Floating.exe")
        New-TestFile (Join-Path $taskSandbox ".local/desktop/$taskKind/source-build.txt") (Join-Path $taskSandbox '.local/base')
    }
    $taskStatusStub = @'
function Get-PoolStatus([string]$Path) {
    [pscustomobject]@{boundaryProbe=$true;accounts=@();nativeLoginAttempts=@();settings=[pscustomobject]@{autoSwitch=$false;fiveHourThreshold=95;weeklyThreshold=95;windowThreshold=$null}}
}
'@
    $taskSourcePath = Join-Path $taskSandbox 'scripts/deploy-local.ps1'
    $taskAst = [Management.Automation.Language.Parser]::ParseFile($taskSourcePath,[ref]$null,[ref]$null)
    $taskStatusFunction = $taskAst.Find({param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Get-PoolStatus'},$true)
    $taskSource = Get-Content -LiteralPath $taskSourcePath -Raw
    $taskSource = $taskSource.Remove($taskStatusFunction.Extent.StartOffset,$taskStatusFunction.Extent.EndOffset-$taskStatusFunction.Extent.StartOffset).Insert($taskStatusFunction.Extent.StartOffset,$taskStatusStub)
    [IO.File]::WriteAllText($taskSourcePath,$taskSource)
    New-TestFile (Join-Path $taskSandbox 'scripts/desktop-client.ps1') @'
function Start-PoolDesktopClient {
    param($BuildDirectory,[switch]$Tray,[switch]$PassThru,[switch]$NoDaemonStart,[switch]$InstallOnly)
    if ($InstallOnly) { $global:taskTrace.Add('restore:files'); return }
    $global:taskTrace.Add("start:ui:$NoDaemonStart")
    if ($BuildDirectory -match '[\\/]base$') { $global:taskTrace.Add('restore:ui') }
    $taskStarted = New-TestProcess $(if ($Tray) {401} else {402}) (Join-Path $BuildDirectory 'Widget/CodexPool.Floating.exe') ($global:taskFailure -eq 'Components' -and $BuildDirectory -notmatch '[\\/]base$')
    if ($PassThru) { return $taskStarted }
}
'@
    $taskFailed = $false
    try { & $taskSourcePath -Target $taskTarget }
    catch { $taskFailed = $true; $taskFailureMessage = $_.Exception.Message }
    Assert-Test ($taskFailed -eq $taskCase.StartsWith('Fail')) "Unexpected result for ${taskCase}: $taskFailureMessage"
    $taskPointer = Get-Content -LiteralPath (Join-Path $taskSandbox '.local/current-build.json') -Raw | ConvertFrom-Json
    if ($taskTarget -eq 'Core') {
        Assert-Test (-not ($global:taskTrace | Where-Object {$_ -match 'ui|stop:20'})) 'Core deployment touched UI.'
    } elseif ($taskTarget -eq 'Components') {
        Assert-Test (-not ($global:taskTrace | Where-Object {$_ -match 'start:core|stop:101'})) 'Component deployment touched core.'
        Assert-Test (-not ($global:taskTrace | Where-Object {$_ -eq 'start:ui:False'})) 'Component UI may start the core.'
    }
    if ($taskFailed) {
        Assert-Test ($taskPointer.path -eq (Join-Path $taskSandbox '.local/base')) 'Failure published the new pointer.'
        if ($taskTarget -eq 'Core') { Assert-Test ($global:taskTrace.Count -eq 3 -and $global:taskCore.Path -match '[\\/]base[\\/]') 'Old core was not restored.' }
        elseif ($taskCase -eq 'FailComponentsOffline') { Assert-Test ($global:taskTrace -contains 'restore:files' -and $global:taskTrace -notcontains 'restore:ui') 'Offline core was restarted by old UI rollback.' }
        else { Assert-Test ($global:taskTrace -contains 'restore:ui') 'Old UI was not restored.' }
    } else {
        Assert-Test ($taskPointer.path -ne (Join-Path $taskSandbox '.local/base')) 'Success did not publish a new pointer.'
        if ($taskTarget -ne 'Core') { Assert-Test (@($global:taskTrace | Where-Object { $_ -match '^start:ui:' }).Count -eq 1) 'Unified tray panel must start only one UI.' }
    }
    Write-Output "PASS deployment isolation and recovery: $taskCase"
}
$taskMenuRoot = New-TestRoot 'cmd-entry'
Copy-Item -LiteralPath (Join-Path $taskRepository 'deploy.cmd') -Destination $taskMenuRoot
Copy-Item -LiteralPath (Join-Path $taskRepository 'scripts/deploy-menu.ps1') -Destination (Join-Path $taskMenuRoot 'scripts')
New-TestFile (Join-Path $taskMenuRoot 'scripts/deploy-local.ps1') 'param([string]$Target,[switch]$Dashboard); Write-Output ("ACTUAL_TARGET=" + $Target)'
foreach ($taskTarget in @('All','Core','Components')) {
    $taskCommand = 'call "{0}" {1} <nul' -f (Join-Path $taskMenuRoot 'deploy.cmd'),$taskTarget
    $taskOutput = & $env:ComSpec /d /c $taskCommand 2>&1
    Assert-Test ($LASTEXITCODE -eq 0 -and ($taskOutput -join "`n") -match ('ACTUAL_TARGET=' + $taskTarget)) "CMD forwarded the wrong scope: $taskTarget"
    Write-Output "PASS actual CMD entry: $taskTarget"
}
$taskOutput = & $env:ComSpec /d /c ('call "{0}" InvalidTarget <nul' -f (Join-Path $taskMenuRoot 'deploy.cmd')) 2>&1
Assert-Test ($LASTEXITCODE -ne 0 -and ($taskOutput -join "`n") -notmatch 'ACTUAL_TARGET=') 'Invalid CMD target fell back to All.'
Write-Output 'PASS invalid CMD target cannot deploy All'
$taskBatchBytes = [IO.File]::ReadAllBytes((Join-Path $taskRepository 'deploy.cmd'))
Assert-Test (@($taskBatchBytes | Where-Object { $_ -gt 127 }).Count -eq 0) 'Batch wrapper must remain ASCII.'
Assert-Test ([IO.File]::ReadAllText((Join-Path $taskRepository 'deploy.cmd')) -notmatch '(?<!\r)\n') 'Batch wrapper must use CRLF.'
Write-Output '14/14 deployment target checks passed; simulated publishing/processes only'
