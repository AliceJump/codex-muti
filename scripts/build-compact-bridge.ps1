#requires -Version 7.0
param([switch]$SelfContained)
$ErrorActionPreference = 'Stop'
$taskRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$taskOutput = Join-Path $taskRoot ('.local/compact-bridge/builds/' + [Guid]::NewGuid().ToString('N'))
& dotnet publish (Join-Path $taskRoot 'src/CodexPool.Bridge/CodexPool.Bridge.csproj') -c Release -r win-x64 --self-contained $SelfContained.IsPresent.ToString().ToLowerInvariant() -o $taskOutput
if ($LASTEXITCODE -ne 0) { throw '桥接构建失败，现有进程保持运行。' }
$taskPointer = Join-Path $taskRoot '.local/compact-bridge/current-build.json'
[IO.File]::WriteAllText($taskPointer + '.tmp', (@{path=$taskOutput} | ConvertTo-Json -Compress))
Move-Item -LiteralPath ($taskPointer + '.tmp') -Destination $taskPointer -Force
Write-Host '桥接构建完成。启动检查：scripts/desktop-compact-bridge.ps1 -CheckOnly'
