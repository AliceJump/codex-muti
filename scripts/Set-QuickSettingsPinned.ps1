#requires -Version 5.1
[CmdletBinding()]
param([Parameter(Mandatory=$true)][bool]$Pinned,[string]$RuntimeRoot='C:\QSE-Host',[ValidateRange(0,30)][int]$WaitSeconds=2)
$ErrorActionPreference='Stop'
Import-Module (Join-Path $PSScriptRoot 'QuickSettingsLifecycle.psm1')
$runtime=Get-QseRuntime $RuntimeRoot;$lock=Enter-QsePluginLock $runtime
try{
    Assert-QseLifecycle $runtime;$record=Get-QseRecord $runtime;Assert-QseRecord $runtime $record
    $action=if($Pinned){'pin'}else{'unpin'}
    $operation=Invoke-QseOwnedManagement $runtime $record $action
    Wait-QseOperation $runtime $record $operation $WaitSeconds
}finally{Exit-QsePluginLock $lock}
