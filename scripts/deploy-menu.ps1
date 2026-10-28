#requires -Version 7.0
param([ValidateSet('All','Core','Components')][string]$Target, [switch]$Force)
$ErrorActionPreference = 'Stop'
if (-not $Target) {
    Write-Host '1. 全部部署（核心 + 桌面组件）'
    Write-Host '2. 仅部署核心（代理和 Dashboard）'
    Write-Host '3. 仅部署桌面组件（托盘面板）'
    Write-Host '4. 强制部署全部（中断代理当前请求和登录）'
    Write-Host '5. 强制部署核心（中断代理当前请求和登录）'
    $taskChoice = Read-Host '请选择 [1-5]'
    $Target = switch ($taskChoice) {
        '1' { 'All' }
        '2' { 'Core' }
        '3' { 'Components' }
        '4' { $Force = $true; 'All' }
        '5' { $Force = $true; 'Core' }
        default { throw '未选择有效部署范围，未执行部署。' }
    }
}
& (Join-Path $PSScriptRoot 'deploy-local.ps1') -Target $Target -Dashboard -Force:$Force
