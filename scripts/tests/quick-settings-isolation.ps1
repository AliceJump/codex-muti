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
    $record=[pscustomobject]@{kind='codex-pool';providerPath=(Join-Path $root 'CodexPool/provider-fixture/CodexPool.Daemon.exe');servicePath=(Join-Path $root 'application/CodexPool.Daemon.exe');brokerPath=$broker;dataDirectory=(Join-Path $root 'data')}
    Assert-QseRecord $runtime $record
    $service=$record.servicePath;$record.providerPath=$service;Reject {Assert-QseRecord $runtime $record}
    $record.providerPath=Join-Path $root 'Registered/qse-broker.exe';Reject {Assert-QseRecord $runtime $record}
    $record.providerPath=Join-Path $root 'CodexPool/../Registered/CodexPool.Daemon.exe';Reject {Assert-QseRecord $runtime $record}
    foreach($relative in @('scripts/Deploy-QuickSettings.ps1','scripts/Start-QuickSettings.ps1','scripts/Stop-QuickSettings.ps1','scripts/Uninstall-QuickSettings.ps1','scripts/QuickSettingsLifecycle.psm1')){
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
    Write-Output 'PASS provider path guards, shared-runtime isolation, no reinstall on start, and script syntax; no process or service mutated.'
}finally{
    $resolved=[IO.Path]::GetFullPath($root)
    if($resolved.StartsWith($research,[StringComparison]::OrdinalIgnoreCase) -and (Test-Path -LiteralPath $resolved)){Remove-Item -LiteralPath $resolved -Recurse -Force}
}
