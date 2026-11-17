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
$previousTemp=$env:TEMP;$previousTmp=$env:TMP
$fixtureRoot=Join-Path ([IO.Path]::GetTempPath()) ('LegionTweaks-suite-fixture-'+[Guid]::NewGuid().ToString('N'))
$null=New-Item -ItemType Directory -Path $fixtureRoot
$fixtureAcl=Get-Acl -LiteralPath $fixtureRoot
$fixtureAcl.SetAccessRuleProtection($true,$false)
foreach($sid in @([Security.Principal.WindowsIdentity]::GetCurrent().User,[Security.Principal.SecurityIdentifier]::new('S-1-5-18'),[Security.Principal.SecurityIdentifier]::new('S-1-5-32-544'))){
    $fixtureAcl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new($sid,[Security.AccessControl.FileSystemRights]::FullControl,[Security.AccessControl.InheritanceFlags]'ContainerInherit,ObjectInherit',[Security.AccessControl.PropagationFlags]::None,[Security.AccessControl.AccessControlType]::Allow))
}
Set-Acl -LiteralPath $fixtureRoot -AclObject $fixtureAcl
# Fixtures model the elevated product's descriptor-write rights in a private
# test tree. No privilege or ACL outside this newly created tree is changed.
$env:TEMP=$fixtureRoot;$env:TMP=$fixtureRoot
try{
& (Join-Path $PSScriptRoot 'Get-Return49Fixture.ps1') -Archive $TargetArchive -Extracted $TargetExtracted
if((Get-FileHash -LiteralPath $MandoHarness -Algorithm SHA256).Hash -cne $MandoHarnessSha256){throw 'The compiled Mando fixture pin differs.'}
$harness=[Reflection.Assembly]::ReflectionOnlyLoadFrom($MandoHarness)
foreach($type in @('Program+FixtureAdmission','Program+FixtureMaintenanceEnvironment','LegionTweaks.FpsY3MaintenanceSession')){if(-not $harness.GetType($type,$false)){throw ('The harness lacks '+$type)}}
$shell=Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe'
$script:calls=0
function Run-Case([string]$Name,[string[]]$Arguments){
    Write-Output ('RUN '+$Name+' '+($Arguments -join ' '))
    & $shell -NoProfile -NonInteractive -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot $Name) @Arguments
    $exitCode=$LASTEXITCODE
    if($exitCode -ne 0){
        if($Name -eq 'MaintenanceBackup.Tests.ps1'){
            # Diagnostics are confined to this suite's disposable fixture roots.
            foreach($fixture in @(Get-ChildItem -LiteralPath ([IO.Path]::GetTempPath()) -Directory -Filter 'LegionTweaks-backup-fixture-*' | Sort-Object CreationTimeUtc -Descending | Select-Object -First 1)){
                foreach($relative in @('source\nested\original.json','backup\nested\original.json')){
                    $file=Join-Path $fixture.FullName $relative
                    if(Test-Path -LiteralPath $file){[pscustomobject]@{Fixture=$relative;SHA256=(Get-FileHash -LiteralPath $file).Hash;Sddl=(Get-Acl -LiteralPath $file).Sddl;Attributes=(Get-Item -LiteralPath $file).Attributes.ToString()} | ConvertTo-Json -Compress | Write-Output}
                }
            }
        }
        throw ('PowerShell test failed: '+$Name+' (exit '+$exitCode+')')
    }
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
    foreach($flag in @('ThroughCli','FailAppx','MissingTrust','ActiveWorker','ExtraSid','ChangeProtectionAfterAppx')){Run-Case 'MaintenanceProductionFactory.Tests.ps1' ($integration+$extra+@('-'+$flag))}
    if($direction -eq 'Update'){Run-Case 'MaintenanceProductionFactory.Tests.ps1' ($integration+@('-WrongWidgetVersion'))}
    else{Write-Output 'SCOPE Return49 widget identity is read from the exact archive; synthetic WrongWidgetVersion injection is scheduled only for Update'}
    # This case deliberately injects a mismatched session digest. The real
    # fixture session supplies the correct digest and would replace that double.
    Write-Output ('SCOPE '+$direction+' MismatchY3Snapshot: injected session digest; not a real Mando session case')
    Run-Case 'MaintenanceProductionFactory.Tests.ps1' ($extra+@('-MismatchY3Snapshot'))
    foreach($case in @('busy','abandoned_mutex')){Run-Case 'MaintenanceProductionFactory.Tests.ps1' ($integration+$extra+@('-BlockedSession',$case))}
}
Write-Output ('PASS PowerShell inventory: '+$required.Count+' suites; '+$script:calls+' isolated invocations; both directions; Mando fixture for integration and digest doubles for two mismatch cases')
}finally{$env:TEMP=$previousTemp;$env:TMP=$previousTmp}
