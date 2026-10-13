param([switch]$SelfContained)
$ErrorActionPreference = 'Stop'
$taskRoot = [IO.Path]::GetFullPath((Split-Path $PSScriptRoot -Parent))
$taskLabel = (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N').Substring(0,6)
$taskOutput = Join-Path $taskRoot ('.local\builds\' + $taskLabel)
# Never overwrite a running release, terminate a process, or change account settings.
& (Join-Path $PSScriptRoot 'build-release.ps1') -SelfContained:$SelfContained -OutputDirectory $taskOutput
if ($LASTEXITCODE -ne 0) { throw '构建未完成，原服务保持运行。' }
$taskPointer = Join-Path $taskRoot '.local\current-build.json'
[IO.File]::WriteAllText($taskPointer + '.tmp', (@{path=$taskOutput;builtAt=[DateTimeOffset]::UtcNow.ToString('O')} | ConvertTo-Json -Compress))
Move-Item -LiteralPath ($taskPointer + '.tmp') -Destination $taskPointer -Force
Write-Host '构建完成。没有停止或重启当前代理。'
Write-Host '启动新构建：.\scripts\start-local.ps1'
Write-Host '代理已经运行时，启动脚本会沿用旧代理；若要让代理修复生效，请在自己结束请求并退出旧代理后再次启动。'
