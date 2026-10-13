param([switch]$SelfContained, [switch]$Dashboard, [switch]$Floating)
$ErrorActionPreference = 'Stop'
& (Join-Path $PSScriptRoot 'build-local.ps1') -SelfContained:$SelfContained
& (Join-Path $PSScriptRoot 'start-local.ps1') -Dashboard:$Dashboard -Floating:$Floating
