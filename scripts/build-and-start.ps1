param([switch]$SelfContained, [switch]$Dashboard)
$ErrorActionPreference = 'Stop'
& (Join-Path $PSScriptRoot 'build-local.ps1') -SelfContained:$SelfContained
& (Join-Path $PSScriptRoot 'start-local.ps1') -Dashboard:$Dashboard
