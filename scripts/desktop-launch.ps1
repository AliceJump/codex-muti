# Shared, side-effect-free Desktop launch configuration. Dot-source from launchers/tests.
function Resolve-PoolDesktopExecutable {
    param([Parameter(Mandatory)]$Package, [Parameter(Mandatory)]$Manifest)
    # Codex.exe may be a small activation shim. Launch the actual package application
    # so process-scoped bridge variables reach Electron instead of an activation broker.
    $taskApplication = @($Manifest.Package.Applications.Application | Where-Object Id -EQ 'App')
    if ($taskApplication.Count -ne 1 -or -not $taskApplication[0].Executable) { throw '无法唯一确定 Desktop 应用清单入口，请用 -DesktopPath 指定。' }
    $taskPackageRoot = [IO.Path]::GetFullPath($Package.InstallLocation)
    $taskExecutable = [IO.Path]::GetFullPath((Join-Path $taskPackageRoot $taskApplication[0].Executable))
    if (-not $taskExecutable.StartsWith($taskPackageRoot + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Desktop 应用清单入口越界。' }
    return $taskExecutable
}

function New-PoolDesktopStartInfo {
    param([Parameter(Mandatory)][string]$DesktopPath, [Parameter(Mandatory)][string]$CodexPath, [Parameter(Mandatory)][string]$BridgePath)
    $taskStart = [Diagnostics.ProcessStartInfo]::new($DesktopPath)
    $taskStart.UseShellExecute = $false
    $taskStart.WorkingDirectory = Split-Path $DesktopPath -Parent
    $taskStart.WindowStyle = [Diagnostics.ProcessWindowStyle]::Normal
    $taskStart.Environment['CODEX_CLI_PATH'] = $BridgePath
    $taskStart.Environment['CODEX_POOL_UPSTREAM_CLI'] = $CodexPath
    $taskStart.Environment['CODEX_POOL_COMPACT_BRIDGE'] = '1'
    $taskStart.Environment['CODEX_APP_SERVER_FORCE_CLI'] = '1'
    return $taskStart
}

function Start-PoolDesktopProcess {
    param([Parameter(Mandatory)][Diagnostics.ProcessStartInfo]$StartInfo)
    return [Diagnostics.Process]::Start($StartInfo)
}
