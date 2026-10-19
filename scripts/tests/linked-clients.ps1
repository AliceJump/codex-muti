#requires -Version 7.0
$ErrorActionPreference = 'Stop'
$taskRepository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$taskFixture = Join-Path $taskRepository ('.research/linked clients ' + [Guid]::NewGuid().ToString('N'))
$taskBuild = Join-Path $taskFixture '.local/build'
$null = New-Item -ItemType Directory -Path (Join-Path $taskFixture 'scripts') -Force
$null = New-Item -ItemType Directory -Path (Join-Path $taskBuild 'Widget') -Force
[IO.File]::WriteAllText((Join-Path $taskBuild 'Widget/CodexPool.Floating.exe'), 'fixture-not-executable')
[IO.File]::WriteAllText((Join-Path $taskFixture '.local/current-build.json'), (@{path=$taskBuild} | ConvertTo-Json -Compress))
foreach ($taskScript in @('desktop-client.ps1','open-widget.ps1','start-proxy-tray.ps1')) {
    Copy-Item -LiteralPath (Join-Path $taskRepository "scripts/$taskScript") -Destination (Join-Path $taskFixture 'scripts')
}
$taskLaunches = [Collections.Generic.List[object]]::new()
function Get-CimInstance { param($ClassName,$Filter) @() }
function Start-Process {
    param($FilePath,$ArgumentList,$WorkingDirectory,$WindowStyle,[switch]$PassThru)
    $taskLaunches.Add([pscustomobject]@{Path=$FilePath;Arguments=@($ArgumentList);Root=$env:CODEX_POOL_ROOT;AutoStart=$env:CODEX_POOL_DAEMON_AUTOSTART;Daemon=$env:CODEX_POOL_DAEMON;Style=$WindowStyle})
    if ($PassThru) { [pscustomobject]@{Id=123;HasExited=$false} }
}
function Assert-Linked($Condition,[string]$Message) { if (-not $Condition) { throw $Message } }
$taskPrior = @{}
foreach ($taskKey in @('CODEX_POOL_ROOT','CODEX_POOL_DAEMON_AUTOSTART','CODEX_POOL_DAEMON')) { $taskPrior[$taskKey]=[Environment]::GetEnvironmentVariable($taskKey,'Process') }
& (Join-Path $taskFixture 'scripts/open-widget.ps1') -Show -NoDaemonStart -SkipProxyTray
Assert-Linked ($taskLaunches.Count -eq 1) 'Opening account panel unexpectedly started another client.'
$taskAccount = $taskLaunches[0]
Assert-Linked (($taskAccount.Arguments -join ',') -eq '--tray,--show') 'Cold launch did not show the account panel.'
Assert-Linked ($taskAccount.Root -eq $taskFixture -and $taskAccount.AutoStart -eq '0') 'Linked launch lost its root or may restart a stopped daemon.'
Assert-Linked ($taskAccount.Path -eq (Join-Path $taskFixture '.local/desktop/Widget/CodexPool.Floating.exe')) 'Account launch bypassed the stable installed path.'
Write-Output 'PASS open account client shows panel without starting proxy or proxy tray'
$taskOldBuild = Join-Path $taskFixture '.local/old-proxy'
$null = New-Item -ItemType Directory -Path (Join-Path $taskOldBuild 'Widget') -Force
$null = New-Item -ItemType Directory -Path (Join-Path $taskFixture '.local/proxy-tray') -Force
[IO.File]::WriteAllText((Join-Path $taskOldBuild 'Widget/CodexPool.Floating.exe'), 'fixture-not-executable')
$taskProxyPointer = Join-Path $taskFixture '.local/proxy-tray/current-build.json'
[IO.File]::WriteAllText($taskProxyPointer, (@{path=$taskOldBuild} | ConvertTo-Json -Compress))
[IO.File]::SetLastWriteTimeUtc($taskProxyPointer, [DateTime]::UtcNow.AddMinutes(-5))
& (Join-Path $taskFixture 'scripts/start-proxy-tray.ps1')
Assert-Linked ($taskLaunches.Count -eq 2) 'Proxy control launch started extra clients.'
$taskProxy = $taskLaunches[1]
Assert-Linked (($taskProxy.Arguments -join ',') -eq '--proxy-tray' -and $taskProxy.AutoStart -eq '0') 'Proxy control launch was not independent.'
Assert-Linked ($taskProxy.Path -eq (Join-Path $taskFixture '.local/desktop/Proxy/CodexPool.Floating.exe') -and $taskProxy.Root -eq $taskFixture) 'Proxy launch did not use stable path/current build fallback.'
foreach ($taskKey in $taskPrior.Keys) { Assert-Linked ([Environment]::GetEnvironmentVariable($taskKey,'Process') -ceq $taskPrior[$taskKey]) "Parent environment changed: $taskKey" }
Assert-Linked (@($taskLaunches | Where-Object Style -NE Hidden).Count -eq 0) 'A helper window would be visible.'
Write-Output 'PASS proxy control uses current-build fallback, independent stable path and restored parent environment'
Assert-Linked ([IO.File]::ReadAllText((Join-Path $taskFixture '.local/desktop/Proxy/source-build.txt')) -eq $taskBuild) 'An old dedicated proxy pointer overrode the newly deployed UI.'
Write-Output 'PASS full UI deployment takes precedence over an older dedicated proxy build'
[IO.File]::SetLastWriteTimeUtc($taskProxyPointer, [DateTime]::UtcNow.AddMinutes(1))
& (Join-Path $taskFixture 'scripts/start-proxy-tray.ps1')
Assert-Linked ([IO.File]::ReadAllText((Join-Path $taskFixture '.local/desktop/Proxy/source-build.txt')) -eq $taskOldBuild) 'A later explicit proxy build was ignored.'
Write-Output 'PASS a later explicit proxy build remains selectable'
Write-Output '4/4 linked client checks passed; synthetic files and intercepted process creation only'
