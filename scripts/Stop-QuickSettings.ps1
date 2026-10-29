#requires -Version 5.1
[CmdletBinding()]
param([string]$FrameworkRoot)
$ErrorActionPreference='Stop'
if(-not $FrameworkRoot){$FrameworkRoot=Join-Path (Split-Path (Split-Path $PSScriptRoot)) 'quick-settings-extensions'}
$record=Get-Content 'C:\QSE-Host\codex-pool-deployment.json' -Raw -Encoding utf8 | ConvertFrom-Json
Get-Process CodexPool.Daemon -ErrorAction SilentlyContinue | Where-Object {$_.Path -eq $record.providerPath -and $_.Path -ne $record.servicePath} | Stop-Process
& "$FrameworkRoot\scripts\Stop-FrameworkHost.ps1"
