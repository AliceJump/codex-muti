$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path $PSScriptRoot -Parent
$taskWindow = Join-Path $taskRoot '.local\release\Widget\CodexPool.Floating.exe'
if (-not (Test-Path -LiteralPath $taskWindow)) {
    throw '请先运行 scripts\build-widget.ps1。'
}
# The widget starts the adjacent daemon if needed; Web is already part of that daemon.
Start-Process -FilePath $taskWindow -ArgumentList '--tray' -WorkingDirectory (Split-Path $taskWindow -Parent) -WindowStyle Hidden
