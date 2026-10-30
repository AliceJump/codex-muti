#requires -Version 5.1
[CmdletBinding()]
param([string]$RuntimeRoot='C:\QSE-Host',[ValidateRange(0,30)][int]$WaitSeconds=2)
$ErrorActionPreference='Stop'
Import-Module (Join-Path $PSScriptRoot 'QuickSettingsLifecycle.psm1')
$runtime=Get-QseRuntime $RuntimeRoot;$lock=Enter-QsePluginLock $runtime
try{
    Assert-QseLifecycle $runtime;$record=Get-QseRecord $runtime;$state=Get-QsePlugin $runtime
    if($record){Assert-QseRecord $runtime $record}
    if($state.installed){
        if($state.pending){throw 'The provider is processing an operation; uninstall was refused without terminating it.'}
        if(-not $record -or -not [string]::Equals($state.providerPath,$record.providerPath,[StringComparison]::OrdinalIgnoreCase)){throw 'A matching local provider record is required to uninstall this binding.'}
        $null=Invoke-QseManagement $runtime @('provider-disable','codex-pool')
    }
    if($record){$null=Stop-QseProvider $runtime $record}
    $operation=$null
    if($state.installed){$operation=Invoke-QseOwnedManagement $runtime $record 'unregister'}
    elseif(Test-Path -LiteralPath $runtime.operationPath){
        $saved=Get-Content -LiteralPath $runtime.operationPath -Raw -Encoding utf8 | ConvertFrom-Json
        $known=Invoke-QseOwnedManagement $runtime $saved.record 'operation-status' $saved.operationId
        if($known.kind -eq 'unregister'){$operation=$known;$record=$saved.record}
    }
    if((Get-QsePlugin $runtime).installed){throw 'QSE has not confirmed removal of the plugin registration.'}
    if(Test-Path -LiteralPath $runtime.recordPath){Remove-Item -LiteralPath $runtime.recordPath}
    if($operation){$operation=Wait-QseOperation $runtime $record $operation $WaitSeconds}
    $native=if($operation -and $operation.nativeConfirmed){'confirmed'}elseif($operation){$operation.state}else{'unknown'}
    [pscustomobject]@{plugin='codex-pool';registrationRemoved=$true;nativeCleanup=$native;operation=$operation;frameworkUntouched=$true;proxyServiceUntouched=$true}
}finally{Exit-QsePluginLock $lock}
