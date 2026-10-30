#requires -Version 5.1
[CmdletBinding()]
param([string]$RuntimeRoot='C:\QSE-Host')
$ErrorActionPreference='Stop'
Import-Module (Join-Path $PSScriptRoot 'QuickSettingsLifecycle.psm1')
$runtime=Get-QseRuntime $RuntimeRoot
Assert-QseLifecycle $runtime
if(-not(Test-Path -LiteralPath $runtime.operationPath)){throw 'No saved Codex Pool registration operation exists.'}
$saved=Get-Content -LiteralPath $runtime.operationPath -Raw -Encoding utf8 | ConvertFrom-Json
Invoke-QseOwnedManagement $runtime $saved.record 'operation-status' $saved.operationId
