#requires -Version 5.1
[CmdletBinding()]
param([string]$RuntimeRoot='C:\QSE-Host')
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
    $null=Invoke-QseManagement $runtime @('uninstall','codex-pool')
    if((Get-QsePlugin $runtime).installed){throw 'QSE has not confirmed removal of the plugin registration.'}
    if(Test-Path -LiteralPath $runtime.recordPath){Remove-Item -LiteralPath $runtime.recordPath}
    [pscustomobject]@{plugin='codex-pool';registrationRemoved=$true;nativeCleanup='await-shell-snapshot';frameworkUntouched=$true;proxyServiceUntouched=$true}
}finally{Exit-QsePluginLock $lock}
