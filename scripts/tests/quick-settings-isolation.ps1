#requires -Version 5.1
$ErrorActionPreference='Stop'
$repo=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
Import-Module (Join-Path $repo 'scripts/QuickSettingsLifecycle.psm1') -DisableNameChecking
function Check([bool]$Condition,[string]$Message){if(-not $Condition){throw $Message}}
function Reject([scriptblock]$Action){$rejected=$false;try{& $Action}catch{$rejected=$true};Check $rejected 'An unsafe helper record was accepted.'}
$research=[IO.Path]::GetFullPath((Join-Path $repo '.research'))+'\'
$root=[IO.Path]::GetFullPath((Join-Path $research ('tests/qse-script-'+[Guid]::NewGuid().ToString('N'))))
Check ($root.StartsWith($research,[StringComparison]::OrdinalIgnoreCase)) 'Test directory is outside the ignored research root.'
New-Item -ItemType Directory -Path (Join-Path $root 'Registered') -Force | Out-Null
try{
    $broker=Join-Path $root 'Registered/qse-broker.exe';$control=Join-Path $root 'Registered/qse-control.exe'
    [IO.File]::WriteAllText($broker,'');[IO.File]::WriteAllText($control,'')
    [IO.File]::WriteAllText((Join-Path $root 'framework-deployment.json'),(@{brokerPath=$broker} | ConvertTo-Json))
    $runtime=Get-QseRuntime $root
    $previousData=$env:CODEX_POOL_DATA_DIR
    try{
        $env:CODEX_POOL_DATA_DIR=$null;Reject {Resolve-QseDataDirectory $null $null};Reject {Resolve-QseDataDirectory $null 'relative/data'};Reject {Resolve-QseDataDirectory $null '//server/share/data'}
        Check ((Resolve-QseDataDirectory ([pscustomobject]@{dataDirectory=(Join-Path $root 'data')}) $null) -eq (Join-Path $root 'data')) 'Existing absolute data path was not preserved.'
    }finally{$env:CODEX_POOL_DATA_DIR=$previousData}
    $record=[pscustomobject]@{kind='codex-pool';providerPath=(Join-Path $root 'CodexPool/provider-fixture/CodexPool.Daemon.exe');servicePath=(Join-Path $root 'application/CodexPool.Daemon.exe');brokerPath=$broker;dataDirectory=(Join-Path $root 'data')}
    Assert-QseRecord $runtime $record
    $record|Add-Member -NotePropertyName managerPath -NotePropertyValue (Join-Path (Split-Path $record.providerPath) 'CodexPool.QseManager.exe');Assert-QseRecord $runtime $record
    $manager=$record.managerPath;$record.managerPath=Join-Path $root 'unrelated/CodexPool.QseManager.exe';Reject {Assert-QseRecord $runtime $record};$record.PSObject.Properties.Remove('managerPath')
    $legacyProvider=$record.providerPath;$record.providerPath=Join-Path $runtime.providerRoot 'provider-fixture/CodexPool.Daemon.exe';Assert-QseRecord $runtime $record;$record.providerPath=$legacyProvider
    $service=$record.servicePath;$record.providerPath=$service;Reject {Assert-QseRecord $runtime $record}
    $record.providerPath=Join-Path $root 'Registered/qse-broker.exe';Reject {Assert-QseRecord $runtime $record}
    $record.providerPath=Join-Path $root 'CodexPool/../Registered/CodexPool.Daemon.exe';Reject {Assert-QseRecord $runtime $record}
    # Exercise the real management invocation with a local process fixture.
    # Keep receipts inside this test root, away from the installed provider.
    $runtime.providerRoot=Join-Path $root 'receipts';$runtime.operationPath=Join-Path $runtime.providerRoot 'last-operation.json'
    $record.providerPath=$legacyProvider
    $record|Add-Member -NotePropertyName managerPath -NotePropertyValue $manager
    $fixture=Join-Path $root 'manager-fixture';New-Item -ItemType Directory -Path $fixture -Force | Out-Null
    [IO.File]::WriteAllText((Join-Path $fixture 'manager.csproj'),'<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings><AssemblyName>CodexPool.QseManager</AssemblyName></PropertyGroup></Project>')
    [IO.File]::WriteAllText((Join-Path $fixture 'Program.cs'),@'
using System.Text.Json;
using var fixture = JsonDocument.Parse(File.ReadAllText(args[1]));
File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "invocations.txt"), "call\n");
Console.Write(fixture.RootElement.GetProperty("stdout").GetString());
return fixture.RootElement.GetProperty("code").GetInt32();
'@)
    dotnet build (Join-Path $fixture 'manager.csproj') -c Release -o (Split-Path $manager) --nologo | Out-Null
    Check ($LASTEXITCODE -eq 0) 'Management process fixture failed to build.'
    $failedId=[Guid]::NewGuid().ToString();$successId=[Guid]::NewGuid().ToString()
    $cases=@(
        @{code=2;stdout='';error='*result is unknown and no operation ID*';id=$null},
        @{code=2;stdout='not-json';error='*result is unknown and no operation ID*';id=$null},
        @{code=2;stdout='null';error='*result is unknown and no operation ID*';id=$null},
        @{code=0;stdout='not-json';error='*invalid JSON*';id=$null},
        @{code=0;stdout='';error='*no result object*';id=$null},
        @{code=0;stdout='null';error='*no result object*';id=$null},
        @{code=2;stdout=(@{operationId=$failedId;state='unknown'}|ConvertTo-Json);error='*Query the saved operation*';id=$failedId},
        @{code=0;stdout=(@{operationId=$successId;state='confirmed'}|ConvertTo-Json);error=$null;id=$successId}
    )
    $calls=0
    foreach($case in $cases){
        [IO.File]::WriteAllText($broker,($case|ConvertTo-Json),[Text.UTF8Encoding]::new($false))
        $message=$null;$value=$null
        try{$value=Invoke-QseOwnedManagement $runtime $record 'pin'}catch{$message=$_.Exception.Message}
        if($case.error){Check ($message -like $case.error) ('Unexpected management failure: '+$message)}
        else{Check ($null -eq $message -and $value.state -eq 'confirmed') 'A successful management response was lost.'}
        if($case.id){
            $saved=Get-Content -LiteralPath $runtime.operationPath -Raw | ConvertFrom-Json
            Check ($saved.operationId -eq $case.id) 'Management operation ID was not saved before reporting its result.'
        }else{Check (-not(Test-Path -LiteralPath $runtime.operationPath)) 'An invalid response created a receipt.'}
        ++$calls
        Check ((Get-Content -LiteralPath (Join-Path (Split-Path $manager) 'invocations.txt')).Count -eq $calls) 'A management mutation was replayed.'
    }
    foreach($relative in @('scripts/Deploy-QuickSettings.ps1','scripts/Start-QuickSettings.ps1','scripts/Stop-QuickSettings.ps1','scripts/Uninstall-QuickSettings.ps1','scripts/Set-QuickSettingsPinned.ps1','scripts/Get-QuickSettingsOperation.ps1','scripts/QuickSettingsLifecycle.psm1')){
        $path=Join-Path $repo $relative;$tokens=$null;$errors=$null;$ast=[Management.Automation.Language.Parser]::ParseFile($path,[ref]$tokens,[ref]$errors);Check ($errors.Count -eq 0) ('Parse failed: '+$relative)
        $source=[IO.File]::ReadAllText($path)
        Check ($source -notmatch '(Deploy|Start|Stop)-FrameworkHost\.ps1') ('Shared framework lifecycle invoked by '+$relative)
        Check ($source -notmatch 'observer-request|StopEngine|qse-native-poc\.ini') ('Shared adapter state changed by '+$relative)
        if($relative -eq 'scripts/Start-QuickSettings.ps1'){
            $install=$ast.FindAll({param($node) $node -is [Management.Automation.Language.StringConstantExpressionAst] -and $node.Value -eq 'install'},$true)
            Check (@($install).Count -eq 0) 'Start must not silently reinstall an explicitly uninstalled plugin.'
        }
    }
    $mutex=Enter-QsePluginLock $runtime;Exit-QsePluginLock $mutex
    Write-Output 'PASS provider path guards, management results and receipts, shared-runtime isolation, no reinstall on start, and script syntax; no installed process or service mutated.'
}finally{
    $resolved=[IO.Path]::GetFullPath($root)
    if($resolved.StartsWith($research,[StringComparison]::OrdinalIgnoreCase) -and (Test-Path -LiteralPath $resolved)){Remove-Item -LiteralPath $resolved -Recurse -Force}
}
