param([switch]$SelfContained, [string]$OutputDirectory,
    [ValidateSet('All','Core','Components')][string]$Target = 'All', [string]$BaseDirectory)
$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path $PSScriptRoot -Parent
$taskRelease = if ($OutputDirectory) { [IO.Path]::GetFullPath($OutputDirectory) } else { Join-Path $taskRoot '.local\release' }
$taskMode = if ($SelfContained) { 'true' } else { 'false' }
$taskProjects = if ($Target -eq 'Components') { @() } else { @('Daemon') }
if ($Target -ne 'All') {
    if (-not $BaseDirectory) { throw '首次部署请选择全部；部分部署需要现有完整版本。' }
    $taskBase = [IO.Path]::GetFullPath($BaseDirectory)
    if (-not $taskBase.StartsWith((Join-Path $taskRoot '.local') + '\', [StringComparison]::OrdinalIgnoreCase) -or
        $taskRelease.Equals($taskBase, [StringComparison]::OrdinalIgnoreCase) -or
        $taskRelease.StartsWith($taskBase + '\', [StringComparison]::OrdinalIgnoreCase)) { throw '部分部署的基线或输出目录无效。' }
    $taskRetain = if ($Target -eq 'Core') { @('Widget') } else { @('Daemon') }
    foreach ($taskKind in $taskRetain) {
        $taskSource = Join-Path $taskBase $taskKind
        $taskExe = if ($taskKind -eq 'Daemon') { 'CodexPool.Daemon.exe' } else { 'CodexPool.Floating.exe' }
        if (-not (Test-Path -LiteralPath (Join-Path $taskSource $taskExe))) { throw '现有版本不完整，请先完整部署。' }
    }
    $null = New-Item -ItemType Directory -Path $taskRelease -Force
    foreach ($taskKind in $taskRetain) { Copy-Item -LiteralPath (Join-Path $taskBase $taskKind) -Destination $taskRelease -Recurse }
}
foreach ($taskProject in $taskProjects) {
    & dotnet publish (Join-Path $taskRoot "src\CodexPool.$taskProject") -c Release -r win-x64 --self-contained $taskMode -o (Join-Path $taskRelease $taskProject)
    if ($LASTEXITCODE -ne 0) { throw "Publish failed: $taskProject" }
}
if ($Target -ne 'Core') {
    & (Join-Path $PSScriptRoot 'build-widget.ps1') -SelfContained:$SelfContained -OutputDirectory (Join-Path $taskRelease 'Widget')
}
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'pool.ps1') -Destination $taskRelease
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'mcodex.ps1') -Destination $taskRelease
Write-Host "Release ready: $taskRelease"
