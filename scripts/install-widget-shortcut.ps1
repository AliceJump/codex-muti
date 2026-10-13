$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path $PSScriptRoot -Parent
$taskShell = New-Object -ComObject WScript.Shell
$taskDesktop = [Environment]::GetFolderPath('Desktop')
$taskShortcut = $taskShell.CreateShortcut((Join-Path $taskDesktop 'Codex Pool 小组件.lnk'))
$taskShortcut.TargetPath = (Get-Command pwsh -ErrorAction Stop).Source
$taskShortcut.Arguments = '-NoProfile -WindowStyle Hidden -File "' + (Join-Path $PSScriptRoot 'open-widget.ps1') + '"'
$taskShortcut.WorkingDirectory = $taskRoot
$taskShortcut.IconLocation = (Join-Path $taskRoot '.local\release\Widget\CodexPool.Floating.exe') + ',0'
$taskShortcut.Save()
Write-Host '桌面快捷方式已创建：Codex Pool 小组件'
