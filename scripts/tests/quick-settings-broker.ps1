#requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Archive,
    [Parameter(Mandatory)][string]$FrameworkBin,
    [Parameter(Mandatory)][string]$OutputDirectory
)
$ErrorActionPreference = 'Stop'
# The Broker pipe is per user/session. Run this integration test on a clean VM.
if ((Get-Process -Name qse-broker -ErrorAction SilentlyContinue) -or
    (Get-NetTCPConnection -State Listen -LocalPort 3210 -ErrorAction SilentlyContinue)) {
    throw 'This test requires a session without an active QSE Broker or proxy.'
}
$scratch = Join-Path ([IO.Path]::GetFullPath($OutputDirectory)) ('qse-test-' + [guid]::NewGuid())
$runtime = Join-Path $scratch 'framework'
$bin = New-Item -ItemType Directory -Path (Join-Path $runtime 'bin') -Force
foreach ($name in @('qse-broker.exe','qse-control.exe')) {
    Copy-Item -LiteralPath (Join-Path $FrameworkBin $name) -Destination $bin.FullName
}
$brokerExe = Join-Path $bin.FullName 'qse-broker.exe'
$control = Join-Path $bin.FullName 'qse-control.exe'
$stateFile = Join-Path $runtime 'registry.json'
@{brokerPath=$brokerExe} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $runtime 'framework-deployment.json') -Encoding utf8
$unpacked = Join-Path $scratch 'package'
Expand-Archive -LiteralPath $Archive -DestinationPath $unpacked
$app = @(Get-ChildItem -LiteralPath $unpacked -Directory)
if ($app.Count -ne 1) { throw 'Expected one application directory in the ZIP.' }
$scripts = Join-Path $app[0].FullName 'QuickSettings'
$data = New-Item -ItemType Directory -Path (Join-Path $scratch 'empty-data')
$profileDirectory = New-Item -ItemType Directory -Path (Join-Path $scratch 'profile')
$originalLocalAppData = $env:LOCALAPPDATA

function Start-TestBroker {
    $process = Start-Process -FilePath $brokerExe -ArgumentList @('--state', ('"' + $stateFile + '"')) -PassThru -WindowStyle Hidden
    try {
        $deadline = [DateTime]::UtcNow.AddSeconds(20)
        while ([DateTime]::UtcNow -lt $deadline -and !$process.HasExited) {
            if (Get-ChildItem -LiteralPath $bin.FullName -Filter '*.endpoint-v1.*.json') {
                $reply = & $control $brokerExe capabilities 2>$null
                if ($LASTEXITCODE -eq 0 -and ($reply | ConvertFrom-Json).type -eq 'framework.capabilities') { return $process }
            }
            Start-Sleep -Milliseconds 200
        }
        throw 'The isolated QSE Broker did not become ready.'
    } catch {
        if (!$process.HasExited) { $process.Kill(); $process.WaitForExit() }
        $process.Dispose()
        throw
    }
}
function Get-TestRegistration {
    $reply = & $control $brokerExe plugin-status codex-pool
    if ($LASTEXITCODE -ne 0) { throw 'Could not read the real Broker catalog.' }
    $reply | ConvertFrom-Json
}
function Assert-Receipt($Receipt, [string]$Kind, [bool]$Present, [bool]$Pinned) {
    if ($Receipt.kind -ne $Kind -or $Receipt.type -ne 'framework.operation' -or !$Receipt.catalogCommitted -or
        $Receipt.state -ne 'awaiting-shell' -or $Receipt.nativeConfirmed -ne $false -or
        $Receipt.target.id -ne 'codex-pool' -or $Receipt.target.registrationId -ne $registration.registrationId -or
        $Receipt.target.present -ne $Present -or $Receipt.target.pinned -ne $Pinned -or ![guid]::TryParse($Receipt.operationId,[ref]([guid]::Empty))) {
        throw "Invalid $Kind receipt from the isolated Broker."
    }
}
function Stop-TestBroker($Process) {
    if (!$Process.HasExited) { $Process.Kill(); $Process.WaitForExit() }
    $Process.Dispose()
}
$broker = $null
try {
    $env:LOCALAPPDATA = $profileDirectory.FullName
    $broker = Start-TestBroker
    $installed = & "$scripts/Deploy-QuickSettings.ps1" -RuntimeRoot $runtime -DataDirectory $data.FullName
    $registration = Get-TestRegistration
    $recordPath = Join-Path $runtime 'codex-pool-deployment.json'
    $record = Get-Content -LiteralPath $recordPath -Raw | ConvertFrom-Json
    if (!$installed.registered -or !$registration.installed -or !$registration.online -or !$record.clientOperationIds) {
        throw 'The packaged Provider or its operation-ID capability did not register.'
    }
    $pin = & "$scripts/Set-QuickSettingsPinned.ps1" -RuntimeRoot $runtime -Pinned $true -WaitSeconds 0
    Assert-Receipt $pin 'pin' $true $true
    $unpin = & "$scripts/Set-QuickSettingsPinned.ps1" -RuntimeRoot $runtime -Pinned $false -WaitSeconds 0
    Assert-Receipt $unpin 'unpin' $true $false
    $removed = & "$scripts/Uninstall-QuickSettings.ps1" -RuntimeRoot $runtime -WaitSeconds 0
    Assert-Receipt $removed.operation 'unregister' $false $false
    if (!$removed.registrationRemoved -or (Get-TestRegistration).installed -or (Test-Path -LiteralPath $recordPath) -or
        @($pin.operationId,$unpin.operationId,$removed.operation.operationId | Sort-Object -Unique).Count -ne 3 -or
        $pin.target.nativeRevision -ge $unpin.target.nativeRevision -or $unpin.target.nativeRevision -ge $removed.operation.target.nativeRevision) {
        throw 'The catalog did not commit the three distinct operations in order.'
    }
    Stop-TestBroker $broker
    $broker = $null
    $broker = Start-TestBroker
    $after = Get-TestRegistration
    $recovered = & "$scripts/Uninstall-QuickSettings.ps1" -RuntimeRoot $runtime -WaitSeconds 0
    $receipt = & "$scripts/Get-QuickSettingsOperation.ps1" -RuntimeRoot $runtime
    Assert-Receipt $receipt 'unregister' $false $false
    if ($after.installed -or $after.brokerEpoch -eq $registration.brokerEpoch -or !$recovered.registrationRemoved -or
        $recovered.operation.operationId -ne $removed.operation.operationId -or $receipt.operationId -ne $removed.operation.operationId -or
        $receipt.target.nativeRevision -ne $removed.operation.target.nativeRevision) {
        throw 'The fresh Broker did not recover the original unregister receipt.'
    }
    $windows = Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion'
    [pscustomobject]@{
        windowsBuild="$($windows.CurrentBuildNumber).$($windows.UBR)"; architecture=[Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString()
        registered=$installed.registered; pinned=$pin.target.pinned; unpinned=!$unpin.target.pinned
        registrationRemoved=$removed.registrationRemoved; receiptRecovered=$true; nativeState=$receipt.state
    } | ConvertTo-Json -Compress
} finally {
    if ($broker) { Stop-TestBroker $broker }
    $providerDirectory = [IO.Path]::GetFullPath((Join-Path $profileDirectory.FullName 'Programs/CodexPool/QuickSettingsProvider')) + '\'
    foreach ($process in Get-Process -Name CodexPool.Daemon -ErrorAction SilentlyContinue) {
        if ($process.Path -and $process.Path.StartsWith($providerDirectory,[StringComparison]::OrdinalIgnoreCase)) {
            $process.Kill()
            $process.WaitForExit()
        }
    }
    $env:LOCALAPPDATA = $originalLocalAppData
}
