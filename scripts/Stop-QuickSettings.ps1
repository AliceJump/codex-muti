#requires -Version 5.1
[CmdletBinding()]
param([string]$FrameworkRoot,[string]$RuntimeRoot='C:\QSE-Host')
$ErrorActionPreference='Stop'
Import-Module (Join-Path $PSScriptRoot 'QuickSettingsLifecycle.psm1')
$runtime=Get-QseRuntime $RuntimeRoot;$lock=Enter-QsePluginLock $runtime
try{
    Assert-QseLifecycle $runtime;$record=Get-QseRecord $runtime;$state=Get-QsePlugin $runtime
    if(-not $record -and -not $state.installed){[pscustomobject]@{plugin='codex-pool';stoppedProviders=0;registrationRetained=$false;frameworkUntouched=$true;proxyServiceUntouched=$true};return}
    Assert-QseRecord $runtime $record
    if($state.installed -and $state.pending){throw 'The provider is processing an operation; stop was refused without terminating it.'}
    if($state.installed -and -not [string]::Equals($state.providerPath,$record.providerPath,[StringComparison]::OrdinalIgnoreCase)){throw 'The installed plugin belongs to another provider path; stop was refused.'}
    if($state.installed){$null=Invoke-QseManagement $runtime @('provider-disable','codex-pool')}
    $stopped=Stop-QseProvider $runtime $record
    [pscustomobject]@{plugin='codex-pool';stoppedProviders=$stopped;registrationRetained=$state.installed;frameworkUntouched=$true;proxyServiceUntouched=$true}
}finally{Exit-QsePluginLock $lock}
