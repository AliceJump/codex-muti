#requires -Version 7.0
param([ValidateSet('All','Core','Components')][string]$Target)
$ErrorActionPreference = 'Stop'
if (-not $Target) {
    Write-Host '1. 全部部署（核心 + 桌面组件）'
    Write-Host '2. 仅部署核心（代理和 Dashboard）'
    Write-Host '3. 仅部署桌面组件（托盘和悬浮窗）'
    $taskChoice = Read-Host '请选择 [1-3]'
    $Target = switch ($taskChoice) {
        '1' { 'All' }
        '2' { 'Core' }
        '3' { 'Components' }
        default { throw '未选择有效部署范围，未执行部署。' }
    }
}
& (Join-Path $PSScriptRoot 'deploy-local.ps1') -Target $Target -Dashboard
