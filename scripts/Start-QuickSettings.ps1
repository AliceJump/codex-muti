#requires -Version 5.1
[CmdletBinding()]
param([string]$FrameworkRoot)
$ErrorActionPreference='Stop'
if(-not $FrameworkRoot){$FrameworkRoot=Join-Path (Split-Path (Split-Path $PSScriptRoot)) 'quick-settings-extensions'}
$root='C:\QSE-Host'
$record=Get-Content "$root\codex-pool-deployment.json" -Raw -Encoding utf8 | ConvertFrom-Json
$null=& "$FrameworkRoot\scripts\Start-FrameworkHost.ps1"
$manifest=Join-Path (Split-Path $record.providerPath) 'qse-manifest.json'
$expectedContract=1
if(Test-Path $manifest){
    & "$root\Registered\qse-control.exe" $record.brokerPath install $manifest $record.providerPath | Out-Null
    if($LASTEXITCODE -ne 0){throw 'Quick Settings manifest installation was rejected.'}
    $expectedContract=2
}
if(-not(Get-Process CodexPool.Daemon -ErrorAction SilentlyContinue | Where-Object Path -eq $record.providerPath)){
    $previous=$env:CODEX_POOL_DATA_DIR
    try{
        $env:CODEX_POOL_DATA_DIR=$record.dataDirectory
        Start-Process $record.providerPath -ArgumentList @('quick-settings',('"'+$record.brokerPath+'"'),('"'+$record.servicePath+'"')) -WorkingDirectory (Split-Path $record.providerPath) -WindowStyle Hidden -RedirectStandardOutput "$root\evidence\provider-out.txt" -RedirectStandardError "$root\evidence\provider-error.txt"
    }finally{$env:CODEX_POOL_DATA_DIR=$previous}
}
$deadline=[DateTime]::UtcNow.AddSeconds(10)
do{
    $snapshot=& "$root\Registered\qse-control.exe" $record.brokerPath | ConvertFrom-Json
    $entry=@($snapshot.extensions | Where-Object {$_.id -eq 'codex-pool' -and $_.online -and $_.contractVersion -eq $expectedContract})
    if($entry.Count -eq 1){break}
    Start-Sleep -Milliseconds 200
}while([DateTime]::UtcNow -lt $deadline)
if($entry.Count -ne 1){throw 'The standard Codex Pool provider has not registered.'}
[PSCustomObject]@{plugin='codex-pool';contractVersion=$expectedContract;registered=$true;added=$entry[0].added;proxyServiceUntouched=$true}
