# SPDX-License-Identifier: AGPL-3.0-only
# Runs the unchanged CLI file in a fixture directory with isolated module doubles.
$ErrorActionPreference='Stop'
$root=Join-Path ([IO.Path]::GetTempPath()) ('LegionTweaks-cli-fixture-'+[Guid]::NewGuid().ToString('N'));$null=New-Item -ItemType Directory -Path $root
Copy-Item -LiteralPath (Join-Path $PSScriptRoot '..\scripts\LegionTweaks-Maintenance-v1.ps1') -Destination (Join-Path $root 'LegionTweaks-Maintenance-v1.ps1')
$modules=@{
    MaintenanceCliEnvironment=@'
function Get-LtCliIdentity { [pscustomobject]@{Sid='S-1-5-18';Name='fixture'} }
function Get-LtCliLocations { [pscustomobject]@{CommonData=$env:LT_CLI_ROOT;ProgramFiles=$env:LT_CLI_ROOT} }
Export-ModuleMember -Function Get-LtCliIdentity,Get-LtCliLocations
'@
    MaintenanceHardware=@'
function Assert-LtSupportedHardware { return $true }
Export-ModuleMember -Function Assert-LtSupportedHardware
'@
    MaintenanceProtection=@'
function Get-LtProtectedSnapshot { param($Roots,[switch]$WithoutDescendants) if(-not $Roots[0]){throw 'Ruta ausente'};[pscustomobject]@{Items=1;AuthorizesMaintenance=$false} }
Export-ModuleMember -Function Get-LtProtectedSnapshot
'@
    MaintenanceCoordinatorLease=@'
function Open-LtCoordinatorLease { param($Path,$AssertProtection) if(-not (& $AssertProtection (Split-Path -Parent $Path))){throw 'ACL fixture'};[pscustomobject]@{Path=$Path} }
function Test-LtCoordinatorLease { param($Lease) return [bool]$Lease.Path }
function Close-LtCoordinatorLease { param($Lease) [IO.File]::WriteAllText((Join-Path $env:LT_CLI_ROOT 'lease-closed'),$Lease.Path);return $true }
Export-ModuleMember -Function Open-LtCoordinatorLease,Test-LtCoordinatorLease,Close-LtCoordinatorLease
'@
    MaintenanceBackupSet=@'
function Test-LtBackupSet { param($Destination,$ExpectedManifestSha256,$AssertProtection) if(-not $Destination -or -not $ExpectedManifestSha256){throw 'Respaldo fixture'};[pscustomobject]@{AuthorizesMaintenance=$false;Groups=1} }
Export-ModuleMember -Function Test-LtBackupSet
'@
    MaintenanceWindowsEvidence=@'
function Get-LtWindowsPublicationFingerprint { param($Configuration,[switch]$IncludeStartupState) if($Configuration.ExpectedSid -ne 'S-1-5-18' -or -not $Configuration.WidgetPath){throw 'Configuración fixture'};if($IncludeStartupState){return [pscustomobject]@{Sha256=('A'*64);StartupState='absent'}};return ('A'*64) }
Export-ModuleMember -Function Get-LtWindowsPublicationFingerprint
'@
    MaintenancePublicationDecision=@'
function Read-LtPublishReady { param($Path,$AssertProtection) if(-not $Path -or -not (& $AssertProtection $Path)){throw 'Ready fixture'};[pscustomobject]@{Id=$env:LT_CLI_ID;BackupSha256=('B'*64)} }
Export-ModuleMember -Function Read-LtPublishReady
'@
    MaintenanceJournal=@'
function Read-LtTransactionJournal { param($Path,$AssertProtection) return [pscustomobject]@{Id=$env:LT_CLI_ID} }
Export-ModuleMember -Function Read-LtTransactionJournal
'@
    MaintenanceReconcile=@'
function Invoke-LtPublicationReconciliation {
    param($ReadyPath,$JournalPath,$AssertProtection,$ReadExternalFingerprint,$ConfirmRecoverySafe,$VerifyBackup,$AssertExclusive,$Reservation,$Recovery,[switch]$Finalize)
    $r=[pscustomobject]@{BackupSha256=('B'*64)}
    if(-not $ReadyPath -or -not $JournalPath -or -not (& $AssertProtection $ReadyPath) -or (& $ReadExternalFingerprint $r) -ne ('A'*64) -or -not (& $VerifyBackup $r) -or -not (& $AssertExclusive $r) -or (& $ConfirmRecoverySafe $r)){throw 'Conexión de CLI fixture inválida.'}
    [IO.File]::WriteAllText((Join-Path $env:LT_CLI_ROOT 'finalize-observed'),([bool]$Finalize).ToString())
    [pscustomobject]@{Publication='published';Annotated=[bool]$Finalize;MaintenanceAuthorized=$false}
}
Export-ModuleMember -Function Invoke-LtPublicationReconciliation
'@
    MaintenanceProductionInputs=@'
function Read-LtMaintenanceRequest { param($Path,$ExpectedSha256,$AssertProtection,$PackageStore) if(-not (& $AssertProtection $Path)){throw 'Solicitud fixture'};[pscustomobject]@{Id=$env:LT_CLI_ID;Direction=$env:LT_CLI_ACTION;AuthorizesMaintenance=$false} }
Export-ModuleMember -Function Read-LtMaintenanceRequest
'@
    MaintenanceProductionPaths=@'
function Get-LtProductionPaths {
 param($Request,$ExpectedSid)
 $base=Join-Path $env:LT_CLI_ROOT 'LegionTweaks\maintenance-v1';$tx=Join-Path (Join-Path $base 'transactions') $Request.Id
 [pscustomobject]@{Base=$base;PackageStore=(Join-Path $base 'packages');Transaction=$tx;UserData=(Join-Path $env:LT_CLI_ROOT 'userdata');Staging=(Join-Path $env:LT_CLI_ROOT 'stage');Previous=(Join-Path $env:LT_CLI_ROOT 'previous');Failed=(Join-Path $env:LT_CLI_ROOT 'failed');Restore=(Join-Path $env:LT_CLI_ROOT 'restore');AuthorizesMaintenance=$false}
}
function Assert-LtProductionVolumes {param($Paths)return $true}
Export-ModuleMember -Function Get-LtProductionPaths,Assert-LtProductionVolumes
'@
    MaintenanceTransactionDirectory=@'
function Test-LtPriorTransactions {param($Directory,$AssertProtection)return $true}
function New-LtTransactionDirectory {param($Directory,$Id,$AssertProtection)return (Join-Path $Directory $Id)}
Export-ModuleMember -Function Test-LtPriorTransactions,New-LtTransactionDirectory
'@
    MaintenanceScope=@'
function New-LtScopeGuard {param($Anchors,$AllowedRoots)return [pscustomobject]@{AuthorizesMaintenance=$false}}
Export-ModuleMember -Function New-LtScopeGuard
'@
    MaintenanceProductionFactory=@'
function New-LtProductionCoordinator {param($Request,$Paths,$ExpectedSid,$Lease,$ScopeGuard)return [pscustomobject]@{Direction=$Request.Direction}}
Export-ModuleMember -Function New-LtProductionCoordinator
'@
    MaintenanceCoordinator=@'
function Invoke-LtMaintenanceCoordinator {param($Coordinator,$Request)[IO.File]::WriteAllText((Join-Path $env:LT_CLI_ROOT 'operation-observed'),$Coordinator.Direction);return [pscustomobject]@{Stage='complete';Committed=$true}}
Export-ModuleMember -Function Invoke-LtMaintenanceCoordinator
'@
}
foreach($name in $modules.Keys){[IO.File]::WriteAllText((Join-Path $root ($name+'.psm1')),[string]$modules[$name],[Text.UTF8Encoding]::new($false))}
$id='a'*32;$script=Join-Path $root 'LegionTweaks-Maintenance-v1.ps1'
foreach($action in @('Inspect','Finalize')){
    $info=[Diagnostics.ProcessStartInfo]::new();$info.FileName=Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe'
    $info.Arguments='-NoProfile -NonInteractive -ExecutionPolicy Bypass -File "'+$script+'" -Action '+$action+' -TransactionId '+$id+' -WidgetPath "'+(Join-Path $root 'widget.fixture')+'" -WidgetSha256 '+('A'*64)+' -PackageVersion 0.1.49.0 -Signer '+('B'*40)
    $info.UseShellExecute=$false;$info.CreateNoWindow=$true;$info.RedirectStandardOutput=$true;$info.RedirectStandardError=$true
    $info.EnvironmentVariables['LT_CLI_ROOT']=$root;$info.EnvironmentVariables['LT_CLI_ID']=$id
    $child=[Diagnostics.Process]::Start($info)
    try{$stdout=$child.StandardOutput.ReadToEnd();$stderr=$child.StandardError.ReadToEnd();$child.WaitForExit();$expected=($action -eq 'Finalize').ToString();if($child.ExitCode -ne 0 -or $stdout -notmatch 'published' -or $stdout -notmatch 'StartupObservation' -or $stdout -notmatch '\babsent\b' -or -not (Test-Path -LiteralPath (Join-Path $root 'lease-closed')) -or [IO.File]::ReadAllText((Join-Path $root 'finalize-observed')) -cne $expected){throw ('CLI '+$action+' fixture falló: '+$stderr+' / '+$stdout)}}finally{$child.Dispose()}
    Write-Output ('PASS CLI real inicia '+$action+' con módulos y rutas de fixture')
}
foreach($action in @('Update','Return49')){
    $info=[Diagnostics.ProcessStartInfo]::new();$info.FileName=Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe'
    $request=Join-Path $root 'LegionTweaks\maintenance-v1\requests\request.json'
    $info.Arguments='-NoProfile -NonInteractive -ExecutionPolicy Bypass -File "'+$script+'" -Action '+$action+' -TransactionId '+$id+' -RequestPath "'+$request+'" -RequestSha256 '+('A'*64)+' -Prepared'
    $info.UseShellExecute=$false;$info.CreateNoWindow=$true;$info.RedirectStandardOutput=$true;$info.RedirectStandardError=$true
    $info.EnvironmentVariables['LT_CLI_ROOT']=$root;$info.EnvironmentVariables['LT_CLI_ID']=$id;$info.EnvironmentVariables['LT_CLI_ACTION']=$action
    $child=[Diagnostics.Process]::Start($info)
    try{$stdout=$child.StandardOutput.ReadToEnd();$stderr=$child.StandardError.ReadToEnd();$child.WaitForExit();if($child.ExitCode -ne 0 -or $stdout -notmatch 'complete' -or [IO.File]::ReadAllText((Join-Path $root 'operation-observed')) -cne $action){throw ('CLI '+$action+' fixture falló: '+$stderr+' / '+$stdout)}}finally{$child.Dispose()}
    Write-Output ('PASS CLI real inicia '+$action+' con módulos y rutas de fixture')
}
$isolated=Join-Path $root 'unprepared';$null=New-Item -ItemType Directory -Path $isolated
$info=[Diagnostics.ProcessStartInfo]::new();$info.FileName=Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe'
$info.Arguments='-NoProfile -NonInteractive -ExecutionPolicy Bypass -File "'+$script+'" -Action Update -TransactionId '+$id+' -RequestPath "'+(Join-Path $isolated 'request.json')+'" -RequestSha256 '+('A'*64)
$info.UseShellExecute=$false;$info.CreateNoWindow=$true;$info.RedirectStandardOutput=$true;$info.RedirectStandardError=$true;$info.EnvironmentVariables['LT_CLI_ROOT']=$isolated;$info.EnvironmentVariables['LT_CLI_ID']=$id;$info.EnvironmentVariables['LT_CLI_ACTION']='Update'
$child=[Diagnostics.Process]::Start($info)
try{$null=$child.StandardOutput.ReadToEnd();$null=$child.StandardError.ReadToEnd();$child.WaitForExit();if($child.ExitCode -eq 0 -or (Test-Path -LiteralPath (Join-Path $isolated 'lease-closed')) -or (Test-Path -LiteralPath (Join-Path $isolated 'operation-observed'))){throw 'CLI sin preparación llegó a ejecutar una operación.'}}finally{$child.Dispose()}
Write-Output 'PASS CLI sin preparación bloquea antes de adquirir reserva o ejecutar efectos'
