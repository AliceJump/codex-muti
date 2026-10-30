#requires -Version 5.1
[CmdletBinding()]
param([switch]$Force,[string]$FrameworkRoot,[string]$RuntimeRoot='C:\QSE-Host',[string]$ProviderPackage)
$ErrorActionPreference='Stop'
Import-Module (Join-Path $PSScriptRoot 'QuickSettingsLifecycle.psm1')
$repo=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$runtime=Get-QseRuntime $RuntimeRoot;$lock=Enter-QsePluginLock $runtime
try{
    Assert-QseLifecycle $runtime
    $old=Get-QseRecord $runtime;$before=Get-QsePlugin $runtime
    if($old){Assert-QseRecord $runtime $old;if(-not $Force){throw 'Use -Force to replace only the Codex Pool registration helper.'}}
    if($before.installed -and (-not $old -or -not [string]::Equals($before.providerPath,$old.providerPath,[StringComparison]::OrdinalIgnoreCase))){throw 'The installed plugin binding does not match the local deployment record.'}
    if($before.installed -and $before.pending){throw 'The provider is processing an operation. Update was refused without stopping it.'}
    $servicePath=Resolve-QseServicePath $repo $old
    $version=(Get-Date -Format 'yyyyMMdd-HHmmss')+'-'+[Guid]::NewGuid().ToString('N').Substring(0,6)
    $providerDir=Join-Path $runtime.providerRoot ('provider-'+$version)
    if($ProviderPackage){
        $package=[IO.Path]::GetFullPath($ProviderPackage)
        if($providerDir.StartsWith($package.TrimEnd('\')+'\',[StringComparison]::OrdinalIgnoreCase)){throw 'The destination cannot be inside the provider package.'}
        if(-not(Test-Path -LiteralPath (Join-Path $package 'CodexPool.Daemon.exe')) -or -not(Test-Path -LiteralPath (Join-Path $package 'qse-manifest.json'))){throw 'The provider package is incomplete.'}
        New-Item -ItemType Directory -Path $providerDir -Force | Out-Null
        foreach($file in Get-ChildItem -LiteralPath $package){Copy-Item -LiteralPath $file.FullName -Destination $providerDir -Recurse}
    }else{
        & dotnet publish "$repo/src/CodexPool.Daemon/CodexPool.Daemon.csproj" -c Release -r win-x64 --self-contained true -o $providerDir --verbosity quiet
        if($LASTEXITCODE -ne 0){throw 'Registration helper publish failed; the existing provider is unchanged.'}
    }
    $manifest=Join-Path $providerDir 'qse-manifest.json'
    $declaration=Get-Content -LiteralPath $manifest -Raw -Encoding utf8 | ConvertFrom-Json
    if($declaration.id -ne 'codex-pool'){throw 'The provider manifest does not declare Codex Pool.'}
    $managerPath=Join-Path $providerDir 'CodexPool.QseManager.exe'
    Copy-Item -LiteralPath (Join-Path $providerDir 'CodexPool.Daemon.exe') -Destination $managerPath -Force
    $dataDirectory=if($env:CODEX_POOL_DATA_DIR){$env:CODEX_POOL_DATA_DIR}elseif($old){$old.dataDirectory}else{Join-Path $env:LOCALAPPDATA 'CodexPool'}
    if($declaration.PSObject.Properties['activation']){[IO.File]::WriteAllText((Join-Path $providerDir 'qse-provider.json'),(@{configVersion=1;servicePath=$servicePath;dataDirectory=$dataDirectory} | ConvertTo-Json),[Text.UTF8Encoding]::new($false))}
    $record=[pscustomobject]@{kind='codex-pool';version=$version;brokerPath=$runtime.broker;providerPath=(Join-Path $providerDir 'CodexPool.Daemon.exe');managerPath=$managerPath;servicePath=$servicePath;dataDirectory=$dataDirectory}
    $changed=$false
    try{
        if($before.installed){$changed=$true;$null=Invoke-QseManagement $runtime @('provider-disable','codex-pool')}
        if($old){$null=Stop-QseProvider $runtime $old}
        $expected=if($before.installed){$before.providerPath}else{'-'}
        $changed=$true;$null=Install-QseBinding $runtime $manifest $record.providerPath $expected
        Write-QseRecord $runtime $record
        if($before.installed -and $before.disabled){$state=Get-QsePlugin $runtime}
        else{$null=Invoke-QseManagement $runtime @('provider-enable','codex-pool');Start-QseProvider $runtime $record;$state=Wait-QseProvider $runtime $record}
        $owner=Invoke-QseOwnedManagement $runtime $record 'status'
        if($owner.registrationId -ne $state.registrationId){throw 'The dedicated management client is not bound to this registration.'}
        $registration=Invoke-QseOwnedManagement $runtime $record 'operation-status' $owner.registerOperation
        $registration=Wait-QseOperation $runtime $record $registration 2
        [pscustomobject]@{plugin='codex-pool';version=$version;contractVersion=2;registered=$state.online;disabled=$state.disabled;added=$state.added;operationId=$registration.operationId;nativeState=$registration.state;nativeConfirmed=$registration.nativeConfirmed;frameworkUntouched=$true;proxyServiceUntouched=$true}
    }catch{
        $failure=$_
        if($changed){
            try{
                $current=Get-QsePlugin $runtime
                if($current.installed -and [string]::Equals($current.providerPath,$record.providerPath,[StringComparison]::OrdinalIgnoreCase)){
                    $null=Invoke-QseManagement $runtime @('provider-disable','codex-pool');$null=Stop-QseProvider $runtime $record
                    if($before.installed){$null=Install-QseBinding $runtime (Join-Path (Split-Path $old.providerPath) 'qse-manifest.json') $old.providerPath $record.providerPath}
                    else{$null=Invoke-QseManagement $runtime @('uninstall','codex-pool')}
                }elseif($current.installed -and (-not $old -or -not [string]::Equals($current.providerPath,$old.providerPath,[StringComparison]::OrdinalIgnoreCase))){throw 'The registration changed outside this operation; automatic rollback was refused.'}
                if($old){Write-QseRecord $runtime $old}elseif(Test-Path -LiteralPath $runtime.recordPath){Remove-Item -LiteralPath $runtime.recordPath}
                if($before.installed -and -not $before.disabled){
                    $null=Invoke-QseManagement $runtime @('provider-enable','codex-pool')
                    if($before.online){Start-QseProvider $runtime $old;$null=Wait-QseProvider $runtime $old}
                }
            }catch{throw 'Provider update failed and safe rollback was not completed. Framework and proxy were not restarted; keep both provider packages for recovery.'}
        }
        throw $failure
    }
}finally{Exit-QsePluginLock $lock}
