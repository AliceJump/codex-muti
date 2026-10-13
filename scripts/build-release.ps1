param([switch]$SelfContained, [string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path $PSScriptRoot -Parent
$taskRelease = if ($OutputDirectory) { [IO.Path]::GetFullPath($OutputDirectory) } else { Join-Path $taskRoot '.local\release' }
$taskMode = if ($SelfContained) { 'true' } else { 'false' }
foreach ($taskProject in @('Daemon', 'Floating')) {
    & dotnet publish (Join-Path $taskRoot "src\CodexPool.$taskProject") -c Release -r win-x64 --self-contained $taskMode -o (Join-Path $taskRelease $taskProject)
    if ($LASTEXITCODE -ne 0) { throw "Publish failed: $taskProject" }
}
& (Join-Path $PSScriptRoot 'build-widget.ps1') -SelfContained:$SelfContained -OutputDirectory (Join-Path $taskRelease 'Widget')
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'pool.ps1') -Destination $taskRelease
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'mcodex.ps1') -Destination $taskRelease
Write-Host "Release ready: $taskRelease"
