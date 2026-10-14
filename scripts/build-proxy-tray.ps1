#requires -Version 7.0
param([switch]$SelfContained)
$ErrorActionPreference = 'Stop'
$taskRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$taskBuild = Join-Path $taskRoot ('.local/proxy-tray/builds/' + [Guid]::NewGuid().ToString('N'))
& (Join-Path $PSScriptRoot 'build-widget.ps1') -SelfContained:$SelfContained -OutputDirectory (Join-Path $taskBuild 'Widget')
if ($LASTEXITCODE -ne 0) { throw '代理托盘构建失败，现有进程保持运行。' }
$taskPointer = Join-Path $taskRoot '.local/proxy-tray/current-build.json'
[IO.File]::WriteAllText($taskPointer + '.tmp', (@{path=$taskBuild} | ConvertTo-Json -Compress))
Move-Item -LiteralPath ($taskPointer + '.tmp') -Destination $taskPointer -Force
Write-Host '代理托盘已构建。启动：scripts/start-proxy-tray.ps1；没有重启代理或账户面板。'
