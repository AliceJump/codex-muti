#requires -Version 5.1
[CmdletBinding()]
param([switch]$Force,[string]$FrameworkRoot)
$ErrorActionPreference='Stop'
$repo=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if(-not $FrameworkRoot){$FrameworkRoot=Join-Path (Split-Path $repo) 'quick-settings-extensions'}
$root='C:\QSE-Host'
if(-not(Test-Path "$root\host-authorized.txt")){throw 'Install and authorize the supported QSE host runtime first.'}
$recordPath="$root\codex-pool-deployment.json"
if((Test-Path $recordPath) -and -not $Force){throw 'Use -Force to replace the registration helper; the actual proxy service is preserved.'}
$old=if(Test-Path $recordPath){Get-Content $recordPath -Raw -Encoding utf8 | ConvertFrom-Json}else{$null}
$listeners=@(Get-NetTCPConnection -LocalPort 3210 -State Listen -ErrorAction SilentlyContinue | Where-Object LocalAddress -eq '127.0.0.1')
if($listeners.Count -eq 1){$servicePath=(Get-Process -Id $listeners[0].OwningProcess).Path}
elseif($listeners.Count -eq 0 -and $old){$servicePath=$old.servicePath}
else{throw 'Cannot identify the installed proxy service.'}
if(-not $servicePath.StartsWith((Join-Path $repo '.local')+'\',[StringComparison]::OrdinalIgnoreCase) -or (Split-Path $servicePath -Leaf) -ne 'CodexPool.Daemon.exe'){throw 'The listener is not the trusted Codex Pool installation.'}
$version=(Get-Date -Format 'yyyyMMdd-HHmmss')+'-'+[Guid]::NewGuid().ToString('N').Substring(0,6)
$providerDir="$root\CodexPool\provider-$version"
& dotnet publish "$repo/src/CodexPool.Daemon/CodexPool.Daemon.csproj" -c Release -r win-x64 --self-contained true -o $providerDir --verbosity quiet
if($LASTEXITCODE -ne 0){throw 'Registration helper publish failed; the proxy and existing helper are unchanged.'}
if($old){Get-Process CodexPool.Daemon -ErrorAction SilentlyContinue | Where-Object {$_.Path -eq $old.providerPath -and $_.Path -ne $servicePath} | Stop-Process}
try{$framework=& "$FrameworkRoot\scripts\Deploy-FrameworkHost.ps1" -Force}
catch{
    $failure=$_
    if($old){try{$null=& "$PSScriptRoot\Start-QuickSettings.ps1" -FrameworkRoot $FrameworkRoot}catch{}}
    throw $failure
}
$dataDirectory=if($env:CODEX_POOL_DATA_DIR){$env:CODEX_POOL_DATA_DIR}elseif($old){$old.dataDirectory}else{Join-Path $env:LOCALAPPDATA 'CodexPool'}
$record=[ordered]@{kind='codex-pool';version=$version;brokerPath=$framework.brokerPath;providerPath="$providerDir\CodexPool.Daemon.exe";servicePath=$servicePath;dataDirectory=$dataDirectory;adapterFileName=$framework.adapterFileName}
[IO.File]::WriteAllText($recordPath,($record | ConvertTo-Json),[Text.UTF8Encoding]::new($false))
& "$PSScriptRoot\Start-QuickSettings.ps1" -FrameworkRoot $FrameworkRoot
