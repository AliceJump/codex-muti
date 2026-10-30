#requires -Version 5.1
[CmdletBinding()]
param([string]$FrameworkRoot,[string]$RuntimeRoot='C:\QSE-Host')
$ErrorActionPreference='Stop'
Import-Module (Join-Path $PSScriptRoot 'QuickSettingsLifecycle.psm1')
$runtime=Get-QseRuntime $RuntimeRoot;$lock=Enter-QsePluginLock $runtime
try{
    Assert-QseLifecycle $runtime;$record=Get-QseRecord $runtime;Assert-QseRecord $runtime $record
    $state=Get-QsePlugin $runtime
    if(-not $state.installed){throw 'This plugin has been uninstalled. Deploy it explicitly before starting it.'}
    if(-not [string]::Equals($state.providerPath,$record.providerPath,[StringComparison]::OrdinalIgnoreCase)){throw 'The QSE binding and local provider record differ; start was refused.'}
    $null=Invoke-QseManagement $runtime @('provider-enable','codex-pool');Start-QseProvider $runtime $record;$state=Wait-QseProvider $runtime $record
    [pscustomobject]@{plugin='codex-pool';contractVersion=2;registered=$true;added=$state.added;frameworkUntouched=$true;proxyServiceUntouched=$true}
}finally{Exit-QsePluginLock $lock}
