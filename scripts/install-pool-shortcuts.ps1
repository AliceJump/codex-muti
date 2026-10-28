#requires -Version 7.0
$ErrorActionPreference = 'Stop'
$taskRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$taskDesktop = [Environment]::GetFolderPath('Desktop')
$taskShell = New-Object -ComObject WScript.Shell
foreach ($taskEntry in @(
    @{Name='Codex Pool 控制台';Target=(Join-Path $taskRoot 'start-pool.cmd');Arguments='';Description='启动本机代理、账户托盘和网页控制台'},
    @{Name='Codex Pool 部署';Target=(Join-Path $taskRoot 'deploy.cmd');Arguments='';Description='选择普通或强制部署，可更新全部、核心或桌面组件'}
)) {
    $taskShortcut = $taskShell.CreateShortcut((Join-Path $taskDesktop ($taskEntry.Name + '.lnk')))
    $taskShortcut.TargetPath = $taskEntry.Target
    $taskShortcut.Arguments = $taskEntry.Arguments
    $taskShortcut.WorkingDirectory = $taskRoot
    $taskShortcut.IconLocation = (Join-Path $taskRoot 'assets/codex-pool.ico') + ',0'
    $taskShortcut.Description = $taskEntry.Description
    $taskShortcut.Save()
    Write-Host ('桌面快捷方式已创建：' + $taskEntry.Name)
}
