#requires -Version 5.1
Set-StrictMode -Version Latest

function Get-QseRuntime([string]$RuntimeRoot){
    $root=[IO.Path]::GetFullPath($RuntimeRoot)
    $config=Join-Path $root 'framework-deployment.json'
    if(-not(Test-Path -LiteralPath $config)){throw 'QSE is not installed. Deploy/start the framework separately first.'}
    $framework=Get-Content -LiteralPath $config -Raw -Encoding utf8 | ConvertFrom-Json
    $broker=[IO.Path]::GetFullPath((Join-Path $root 'Registered/qse-broker.exe'))
    $control=Join-Path $root 'Registered/qse-control.exe'
    if(-not [string]::Equals([IO.Path]::GetFullPath($framework.brokerPath),$broker,[StringComparison]::OrdinalIgnoreCase) -or -not(Test-Path -LiteralPath $broker) -or -not(Test-Path -LiteralPath $control)){throw 'The installed QSE runtime paths are invalid.'}
    $providerRoot=Join-Path $env:LOCALAPPDATA 'Programs/CodexPool/QuickSettingsProvider'
    [pscustomobject]@{root=$root;broker=$broker;control=$control;recordPath=(Join-Path $root 'codex-pool-deployment.json');providerRoot=$providerRoot;operationPath=(Join-Path $providerRoot 'last-operation.json')}
}
function Invoke-QseManagement($Runtime,[string[]]$Arguments){
    $encoding=[Console]::OutputEncoding
    try{
        [Console]::OutputEncoding=[Text.UTF8Encoding]::new($false)
        $text=& $Runtime.control $Runtime.broker @Arguments 2>$null
        $code=$LASTEXITCODE
    }catch{throw 'QSE management is unavailable. Start or update the framework separately; no framework process was changed.'}
    finally{[Console]::OutputEncoding=$encoding}
    if($code -ne 0){throw 'QSE rejected the plugin management operation; no shared framework process was changed.'}
    $response=($text -join "`n") | ConvertFrom-Json
    if($response.type -eq 'error'){throw 'QSE rejected the plugin management operation.'}
    $response
}
function Assert-QseLifecycle($Runtime){
    $capabilities=Invoke-QseManagement $Runtime @('capabilities')
    if($capabilities.protocol -ne 2 -or -not $capabilities.PSObject.Properties['management']){throw 'The QSE framework needs the plugin-lifecycle update. Update it separately first.'}
    foreach($feature in @('plugin-status','provider-enable','provider-disable','install-cas','uninstall','on-demand-v1','owned-management-v1','native-confirmation-v1')){if($feature -notin $capabilities.management){throw 'The installed QSE framework lacks required lifecycle capabilities.'}}
}
function Get-QsePlugin($Runtime){Invoke-QseManagement $Runtime @('plugin-status','codex-pool')}
function Get-QseRecord($Runtime){if(Test-Path -LiteralPath $Runtime.recordPath){Get-Content -LiteralPath $Runtime.recordPath -Raw -Encoding utf8 | ConvertFrom-Json}}
function Assert-QseRecord($Runtime,$Record){
    if(-not $Record -or $Record.kind -ne 'codex-pool'){throw 'The Codex Pool registration-helper deployment record is missing.'}
    $provider=[IO.Path]::GetFullPath($Record.providerPath)
    $ownedRoot=[IO.Path]::GetFullPath((Join-Path $Runtime.root 'CodexPool'))+'\'
    $applicationRoot=[IO.Path]::GetFullPath($Runtime.providerRoot)+'\'
    if((-not $provider.StartsWith($ownedRoot,[StringComparison]::OrdinalIgnoreCase) -and -not $provider.StartsWith($applicationRoot,[StringComparison]::OrdinalIgnoreCase)) -or [IO.Path]::GetFileName($provider) -ne 'CodexPool.Daemon.exe' -or [string]::Equals($provider,[IO.Path]::GetFullPath($Record.servicePath),[StringComparison]::OrdinalIgnoreCase) -or -not [string]::Equals([IO.Path]::GetFullPath($Record.brokerPath),$Runtime.broker,[StringComparison]::OrdinalIgnoreCase)){
        throw 'The helper deployment record does not identify a dedicated Codex Pool provider.'
    }
    if($Record.PSObject.Properties['managerPath']){
        $manager=[IO.Path]::GetFullPath($Record.managerPath)
        if(-not [string]::Equals($manager,(Join-Path (Split-Path $provider) 'CodexPool.QseManager.exe'),[StringComparison]::OrdinalIgnoreCase)){throw 'The management client is not the dedicated application controller.'}
    }
}
function Enter-QsePluginLock($Runtime){
    $hash=[Security.Cryptography.SHA256]::Create()
    try{$suffix=([BitConverter]::ToString($hash.ComputeHash([Text.Encoding]::UTF8.GetBytes($Runtime.root.ToUpperInvariant())))).Replace('-','').Substring(0,16)}finally{$hash.Dispose()}
    $mutex=[Threading.Mutex]::new($false,('Local\CodexPool.QseLifecycle.'+$suffix))
    $entered=$false;try{$entered=$mutex.WaitOne(0)}catch [Threading.AbandonedMutexException]{$entered=$true}
    if(-not $entered){$mutex.Dispose();throw 'Another Codex Pool Quick Settings lifecycle operation is already running.'}
    $mutex
}
function Exit-QsePluginLock($Mutex){if($Mutex){$Mutex.ReleaseMutex();$Mutex.Dispose()}}
function Write-QseRecord($Runtime,$Record){
    $temporary=$Runtime.recordPath+'.'+[Guid]::NewGuid().ToString('N')+'.tmp'
    [IO.File]::WriteAllText($temporary,($Record | ConvertTo-Json -Depth 8),[Text.UTF8Encoding]::new($false))
    if(Test-Path -LiteralPath $Runtime.recordPath){[IO.File]::Replace($temporary,$Runtime.recordPath,[NullString]::Value)}else{[IO.File]::Move($temporary,$Runtime.recordPath)}
}
function Stop-QseProvider($Runtime,$Record){
    Assert-QseRecord $Runtime $Record
    $session=[Diagnostics.Process]::GetCurrentProcess().SessionId
    $identity=[Security.Principal.WindowsIdentity]::GetCurrent();try{$user=$identity.User.Value}finally{$identity.Dispose()}
    $listeners=@(Get-NetTCPConnection -LocalPort 3210 -State Listen -ErrorAction SilentlyContinue | Where-Object LocalAddress -eq '127.0.0.1')
    $stopped=0
    foreach($process in @(Get-Process CodexPool.Daemon -ErrorAction SilentlyContinue)){
        if(-not [string]::Equals($process.Path,$Record.providerPath,[StringComparison]::OrdinalIgnoreCase)){continue}
        $null=$process.Handle
        if($process.SessionId -ne $session -or $process.Id -in @($listeners | ForEach-Object OwningProcess)){throw 'The provider path belongs to another session or the active proxy; it will not be stopped.'}
        $details=Get-CimInstance Win32_Process -Filter ('ProcessId='+$process.Id)
        if(-not $details -or $process.HasExited){continue}
        $owner=Invoke-CimMethod -InputObject $details -MethodName GetOwnerSid
        if($owner.ReturnValue -ne 0 -or $owner.Sid -ne $user -or $details.CommandLine -notmatch '(?:^|\s)(quick-settings|qse-provider)(?:\s|$)'){throw 'The executable is not the current user dedicated registration helper; stop was refused.'}
        $process.Kill();if(-not $process.WaitForExit(5000)){throw 'The registration helper has not exited.'};++$stopped
    }
    $stopped
}
function Start-QseProvider($Runtime,$Record){
    Assert-QseRecord $Runtime $Record
    if(-not(Test-Path -LiteralPath $Record.providerPath)){throw 'The installed registration helper is missing.'}
    if(-not @(Get-Process CodexPool.Daemon -ErrorAction SilentlyContinue | Where-Object Path -eq $Record.providerPath).Count){
        $previous=$env:CODEX_POOL_DATA_DIR
        try{
            $env:CODEX_POOL_DATA_DIR=$Record.dataDirectory
            $evidence=Join-Path (Split-Path $Record.providerPath) 'logs';New-Item -ItemType Directory -Path $evidence -Force | Out-Null
            $declaration=Get-Content -LiteralPath (Join-Path (Split-Path $Record.providerPath) 'qse-manifest.json') -Raw -Encoding utf8 | ConvertFrom-Json
            $arguments=if($declaration.PSObject.Properties['activation']){@('qse-provider',('"'+$Runtime.broker+'"'),'codex-pool',[Guid]::NewGuid().ToString())}else{@('quick-settings',('"'+$Runtime.broker+'"'),('"'+$Record.servicePath+'"'))}
            Start-Process $Record.providerPath -ArgumentList $arguments -WorkingDirectory (Split-Path $Record.providerPath) -WindowStyle Hidden -RedirectStandardOutput (Join-Path $evidence 'provider-out.txt') -RedirectStandardError (Join-Path $evidence 'provider-error.txt')
        }finally{$env:CODEX_POOL_DATA_DIR=$previous}
    }
}
function Wait-QseProvider($Runtime,$Record){
    $deadline=[DateTime]::UtcNow.AddSeconds(10)
    do{
        $state=Get-QsePlugin $Runtime
        if($state.installed -and $state.online -and -not $state.disabled -and [string]::Equals($state.providerPath,$Record.providerPath,[StringComparison]::OrdinalIgnoreCase)){return $state}
        Start-Sleep -Milliseconds 200
    }while([DateTime]::UtcNow -lt $deadline)
    throw 'The registration helper has not registered. The framework and proxy service were not restarted.'
}
function Resolve-QseServicePath([string]$Repository,$Old){
    $listeners=@(Get-NetTCPConnection -LocalPort 3210 -State Listen -ErrorAction SilentlyContinue | Where-Object LocalAddress -eq '127.0.0.1')
    if($listeners.Count -eq 1){$path=(Get-Process -Id $listeners[0].OwningProcess).Path}elseif($listeners.Count -eq 0 -and $Old){$path=$Old.servicePath}else{throw 'Cannot identify the installed proxy service.'}
    if(-not $path.StartsWith((Join-Path $Repository '.local')+'\',[StringComparison]::OrdinalIgnoreCase) -or [IO.Path]::GetFileName($path) -ne 'CodexPool.Daemon.exe' -or -not(Test-Path -LiteralPath $path)){throw 'The service installation is not trusted.'}
    $path
}
function Resolve-QseDataDirectory($Old,[string]$Explicit){
    $value=if($Explicit){$Explicit}elseif($env:CODEX_POOL_DATA_DIR){$env:CODEX_POOL_DATA_DIR}elseif($Old -and $Old.PSObject.Properties['dataDirectory']){$Old.dataDirectory}else{throw 'The proxy data directory is unknown. Supply -DataDirectory with the running daemon absolute data path.'}
    if($value -notmatch '^[A-Za-z]:[\\/]'){throw 'The proxy data directory must be an explicit local absolute path; relative and network paths are refused.'}
    $path=[IO.Path]::GetFullPath($value)
    if($path.StartsWith('\\',[StringComparison]::Ordinal)){throw 'Network data directories are refused.'}
    $path
}
function Install-QseBinding($Runtime,[string]$Manifest,[string]$Provider,[string]$Expected){
    $arguments=@('install',$Manifest,$Provider,$Expected)
    $declaration=Get-Content -LiteralPath $Manifest -Raw -Encoding utf8 | ConvertFrom-Json
    if($declaration.PSObject.Properties['activation']){$arguments+='--allow-activation'}
    $manager=Join-Path (Split-Path $Provider) 'CodexPool.QseManager.exe'
    if(Test-Path -LiteralPath $manager){$arguments+=@('--owner',$manager)}
    Invoke-QseManagement $Runtime $arguments
}
function Invoke-QseOwnedManagement($Runtime,$Record,[string]$Action,[string]$OperationId){
    Assert-QseRecord $Runtime $Record
    if(-not $Record.PSObject.Properties['managerPath']){throw 'Update this plugin before using its owned management client.'}
    if($Action -notin @('status','pin','unpin','unregister','operation-status')){throw 'Unknown registration management operation.'}
    $encoding=[Console]::OutputEncoding
    try{
        [Console]::OutputEncoding=[Text.UTF8Encoding]::new($false)
        $arguments=@('qse-manage',$Runtime.broker,$Action);if($OperationId){$arguments+=$OperationId}
        $text=& $Record.managerPath @arguments;$code=$LASTEXITCODE
    }finally{[Console]::OutputEncoding=$encoding}
    $result=($text -join "`n") | ConvertFrom-Json
    if($result.PSObject.Properties['operationId'] -and $result.operationId){
        New-Item -ItemType Directory -Path $Runtime.providerRoot -Force | Out-Null
        $temporary=$Runtime.operationPath+'.'+[Guid]::NewGuid().ToString('N')+'.tmp'
        [IO.File]::WriteAllText($temporary,(@{record=$Record;operationId=$result.operationId} | ConvertTo-Json -Depth 8),[Text.UTF8Encoding]::new($false))
        if(Test-Path -LiteralPath $Runtime.operationPath){[IO.File]::Replace($temporary,$Runtime.operationPath,[NullString]::Value)}else{[IO.File]::Move($temporary,$Runtime.operationPath)}
    }
    if($code -ne 0){throw 'The management result is unknown. Query the saved operation before taking further action; no mutation was replayed.'}
    $result
}
function Wait-QseOperation($Runtime,$Record,$Operation,[int]$Seconds=2){
    if($Seconds -lt 0 -or $Seconds -gt 30){throw 'Operation wait must be between 0 and 30 seconds.'}
    $deadline=[DateTime]::UtcNow.AddSeconds($Seconds);$current=$Operation
    while($current.state -eq 'awaiting-shell' -and [DateTime]::UtcNow -lt $deadline){
        Start-Sleep -Milliseconds 200;$current=Invoke-QseOwnedManagement $Runtime $Record 'operation-status' $Operation.operationId
    }
    $current
}
Export-ModuleMember -Function Get-QseRuntime,Invoke-QseManagement,Assert-QseLifecycle,Get-QsePlugin,Get-QseRecord,Assert-QseRecord,Enter-QsePluginLock,Exit-QsePluginLock,Write-QseRecord,Stop-QseProvider,Start-QseProvider,Wait-QseProvider,Resolve-QseServicePath,Resolve-QseDataDirectory,Install-QseBinding,Invoke-QseOwnedManagement,Wait-QseOperation
