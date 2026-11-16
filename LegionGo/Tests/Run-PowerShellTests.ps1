# SPDX-License-Identifier: AGPL-3.0-only
[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$TargetArchive,
      [Parameter(Mandatory=$true)][string]$TargetExtracted,
      [Parameter(Mandatory=$true)][string]$MandoHarness,
      [Parameter(Mandatory=$true)][ValidatePattern('^[A-F0-9]{64}$')][string]$MandoHarnessSha256)
$ErrorActionPreference='Stop'
if($PSVersionTable.PSEdition -ne 'Desktop' -or $PSVersionTable.PSVersion.Major -ne 5 -or -not [Environment]::Is64BitProcess){throw 'Run these suites in Windows PowerShell 5.1 x64.'}
$inventory=Get-Content -LiteralPath (Join-Path $PSScriptRoot '..\scripts\Build-Inventory.json') -Raw | ConvertFrom-Json
$required=@($inventory.PowerShellSuites)
$present=@(Get-ChildItem -LiteralPath $PSScriptRoot -File -Filter '*.Tests.ps1' | Select-Object -ExpandProperty Name)
if(@(Compare-Object $required $present).Count -or @($required | Select-Object -Unique).Count -ne $required.Count){throw 'PowerShell suite inventory differs; no suite may silently disappear.'}
& (Join-Path $PSScriptRoot 'Get-Return49Fixture.ps1') -Archive $TargetArchive -Extracted $TargetExtracted
if((Get-FileHash -LiteralPath $MandoHarness -Algorithm SHA256).Hash -cne $MandoHarnessSha256){throw 'The compiled Mando fixture pin differs.'}
$harness=[Reflection.Assembly]::ReflectionOnlyLoadFrom($MandoHarness)
foreach($type in @('Program+FixtureAdmission','Program+FixtureMaintenanceEnvironment','LegionTweaks.FpsY3MaintenanceSession')){if(-not $harness.GetType($type,$false)){throw ('The harness lacks '+$type)}}
$shell=Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe'
$script:calls=0
function Run-Case([string]$Name,[string[]]$Arguments){
    Write-Output ('RUN '+$Name)
    & $shell -NoProfile -NonInteractive -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot $Name) @Arguments
    if($LASTEXITCODE -ne 0){throw ('PowerShell test failed: '+$Name+' (exit '+$LASTEXITCODE+')')}
    $script:calls++
}
$integration=@('-MandoHarness',$MandoHarness,'-MandoHarnessSha256',$MandoHarnessSha256)
$return49=@('-TargetArchive',$TargetArchive,'-TargetExtracted',$TargetExtracted)
foreach($name in $required){
    if($name -eq 'MaintenanceProductionCrash.Tests.ps1'){Run-Case $name ($integration+$return49)}
    elseif($name -eq 'MaintenanceProductionFactory.Tests.ps1'){
        Run-Case $name $integration
        Run-Case $name ($integration+$return49)
    }else{Run-Case $name @()}
}
foreach($direction in @('Update','Return49')){
    $extra=if($direction -eq 'Return49'){$return49}else{@()}
    foreach($case in @('target-read','external-read','external-mismatch','annotation')){Run-Case 'MaintenanceProductionFactory.Tests.ps1' ($integration+$extra+@('-PostPublishCase',$case))}
    foreach($flag in @('ThroughCli','FailAppx','MissingTrust','WrongWidgetVersion','MismatchY3Snapshot','ActiveWorker','ExtraSid','ChangeProtectionAfterAppx')){Run-Case 'MaintenanceProductionFactory.Tests.ps1' ($integration+$extra+@('-'+$flag))}
    foreach($case in @('busy','abandoned_mutex')){Run-Case 'MaintenanceProductionFactory.Tests.ps1' ($integration+$extra+@('-BlockedSession',$case))}
}
Write-Output ('PASS PowerShell inventory: '+$required.Count+' suites; '+$script:calls+' isolated invocations; both directions and Mando fixture supplied')
