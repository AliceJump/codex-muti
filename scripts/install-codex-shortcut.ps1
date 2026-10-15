#requires -Version 7.0
$ErrorActionPreference = 'Stop'
$taskRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
& (Join-Path $PSScriptRoot 'start-codex.ps1') -CheckOnly
$taskDesktop = [Environment]::GetFolderPath('Desktop')
$taskShell = New-Object -ComObject WScript.Shell
$taskShortcut = $taskShell.CreateShortcut((Join-Path $taskDesktop 'Codex Pool.lnk'))
# cmd keeps errors visible; the healthy path exits without a pause.
$taskShortcut.TargetPath = Join-Path $taskRoot 'start-codex.cmd'
$taskShortcut.WorkingDirectory = $taskRoot
$taskShortcut.IconLocation = (Join-Path $taskRoot 'assets/codex-pool.ico') + ',0'
$taskShortcut.Description = '启动 Codex Pool、账户面板和带额度预警压缩桥接的 Codex Desktop'
$taskShortcut.Save()
Write-Host '桌面快捷方式已创建：Codex Pool。以后双击即可启动。'
