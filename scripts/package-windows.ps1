#requires -Version 7.0
param([string]$Version, [string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
$taskRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if (-not $Version) { $Version = [IO.File]::ReadAllText((Join-Path $taskRoot 'VERSION')).Trim() }
if ($Version -notmatch '^\d+\.\d+\.\d+(?:-[0-9A-Za-z]+(?:[.-][0-9A-Za-z]+)*)?$') { throw '版本号必须为 SemVer，例如 0.1.0-beta.1。' }
$taskOutput = if ($OutputDirectory) { [IO.Path]::GetFullPath($OutputDirectory) } else { Join-Path $taskRoot '.local/packages' }
$taskName = "CodexPool-$Version-win-x64"
$taskStage = Join-Path $taskRoot ('.local/package-builds/' + [Guid]::NewGuid().ToString('N'))
$taskApplication = Join-Path $taskStage $taskName
$null = New-Item -ItemType Directory -Path $taskApplication -Force
$null = New-Item -ItemType Directory -Path $taskOutput -Force
$taskArchive = Join-Path $taskOutput ($taskName + '.zip')
if (Test-Path -LiteralPath $taskArchive) { throw '目标 ZIP 已存在，请使用新的输出目录，避免覆盖已发布的软件包。' }
foreach ($taskProject in @('Floating','Daemon')) {
    $taskDestination = if ($taskProject -eq 'Floating') { $taskApplication } else { Join-Path $taskApplication 'Daemon' }
    & dotnet publish (Join-Path $taskRoot "src/CodexPool.$taskProject") -c Release -r win-x64 --self-contained true `
        "-p:Version=$Version" -p:DebugType=None -p:DebugSymbols=false -o $taskDestination --nologo
    if ($LASTEXITCODE -ne 0) { throw "自包含构建失败：$taskProject" }
}
# A renamed .NET apphost still loads its embedded original DLL and runtimeconfig names.
Move-Item -LiteralPath (Join-Path $taskApplication 'CodexPool.Floating.exe') -Destination (Join-Path $taskApplication 'CodexPool.exe')
[IO.File]::WriteAllText((Join-Path $taskApplication 'codex-pool.json'), (@{format=1;version=$Version;runtime='win-x64'} | ConvertTo-Json -Compress))
Copy-Item -LiteralPath (Join-Path $taskRoot 'docs/windows-package.md') -Destination (Join-Path $taskApplication '使用说明.md')
Copy-Item -LiteralPath (Join-Path $taskRoot 'docs/release-notes.md') -Destination (Join-Path $taskOutput 'RELEASE-NOTES.md')
Compress-Archive -LiteralPath $taskApplication -DestinationPath $taskArchive -CompressionLevel Optimal
$taskHash = (Get-FileHash -LiteralPath $taskArchive -Algorithm SHA256).Hash.ToLowerInvariant()
[IO.File]::WriteAllText((Join-Path $taskOutput 'SHA256SUMS.txt'), "$taskHash  $taskName.zip`n")
Write-Host "发行包：$taskArchive"
Write-Host "SHA256：$taskHash"
