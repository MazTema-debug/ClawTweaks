# SPDX-License-Identifier: AGPL-3.0-only
# Exercises runtime consumer with injectable inventories and no product processes.
$ErrorActionPreference='Stop'
Import-Module (Join-Path $PSScriptRoot '..\scripts\MaintenanceRuntime.psm1') -Force
Import-Module (Join-Path $PSScriptRoot '..\scripts\MaintenanceTransaction.psm1') -Force
$sid='S-1-5-21-1-2-3-1001';$hash='A'*64;$signer='B'*40
Add-Type -TypeDefinition @'
namespace LegionTweaks {
    public sealed class FpsY3MaintenanceSession { }
    public sealed class FpsY3LegacyInstanceFence { }
}
'@
$mando=[LegionTweaks.FpsY3MaintenanceSession].Assembly
$state=[pscustomobject]@{Items=@();FenceReleased=0;SessionReleased=0;Revalidations=0;CoverageSid=$sid;CountOverride=1;Assembly=$mando;SourceHash=$hash;Packages=[Collections.Generic.List[string]]::new();LoadedPath=''}
$coordinator=@{Archive='X:\coordinator.zip';Extracted='X:\coordinator';ArchiveSha256=$hash;HelperPath='X:\coordinator\Helper\LegionTweaks.Helper.exe';QamPath='X:\coordinator\Helper\Qam\LegionTweaks.QamProbe.exe';WidgetPath='X:\coordinator\Widget\fixture.msix';HelperSha256=$hash;QamSha256=$hash;Signer=$signer}
$target=@{Archive='X:\target49.zip';Extracted='X:\target49';ArchiveSha256='5225CA985580E24DB634B022E6C83805CDA5B2514846B6AFB523885BE698B39A';HelperPath='X:\target49\Helper\LegionTweaks.Helper.exe';QamPath='X:\target49\Helper\Qam\LegionTweaks.QamProbe.exe';WidgetPath='X:\target49\Widget\fixture.msix';HelperSha256='9D485EF0E4354278BB9FC496110A7536585B735EA898ED94A4CE4BD9BF438B00';QamSha256='0874C28F843071381FC54A406FAA28682980480FA4ECB8B603B5088C4FA3B50C';Signer='4AB5A8F9579DFDA51130307D543BEEF06EBF4DA0'}
$source=@{HelperSha256=$hash;QamSha256=$hash;Signer=$signer;PackageFamilyName='MazTema.LegionTweaks_wsckx05r95yk0';Publisher='CN=LegionTweaks Dev';Version='0.1.50.0'}
$config=@{Direction='Return49';Coordinator=$coordinator;Target=$target;SourcePackage=$coordinator;Source=$source;CurrentSid=$sid;ExpectedPaths=@('X:\fixture.exe');TaskSids=@();AssertScope={param($c)return $true}}
$ops=@{
    ObserveSource={param($c)return [pscustomobject]@{HelperSha256=$state.SourceHash;QamSha256=$c.Source.QamSha256;HelperSigner=$c.Source.Signer;QamSigner=$c.Source.Signer;PackageFamilyName=$c.Source.PackageFamilyName;Publisher=$c.Source.Publisher;Version=$c.Source.Version;PackageFullName='synthetic';InstallLocation='X:\installed-fixture';AuthorizesMaintenance=$false}}.GetNewClosure()
    Package={param($c)$state.Packages.Add($c.Archive);return [pscustomobject]@{ArchiveSha256=$c.ArchiveSha256;AuthorizesMaintenance=$false}}.GetNewClosure()
    LoadHelper={param($c)$state.LoadedPath=$c.HelperPath;return [pscustomobject]@{Sha256=$c.HelperSha256;Signer=$c.Signer;Assembly=$state.Assembly;AuthorizesMaintenance=$false}}.GetNewClosure()
    VerifyWidget={param($s,$p)return $true}
    Inventory={param($c)return $state.Items}.GetNewClosure()
    Coverage={param($c,$items)return [pscustomobject]@{FenceSids=@($state.CoverageSid);InspectionSids=@($c.CurrentSid);AuthorizesMaintenance=$false}}.GetNewClosure()
    Fence={param($c,$h,$ids)return [pscustomobject]@{SidCount=$state.CountOverride}}.GetNewClosure()
    OpenSession={param($c,$h)return [pscustomobject]@{AuthorizesMaintenance=$false;Classification='absent';ContentSha256='';FileIdentitySha256='';ProtectionSha256=('D'*64)}}
    Revalidate={param($s)$state.Revalidations++;return $true}.GetNewClosure()
    CloseSession={param($s)$state.SessionReleased++;return $true}.GetNewClosure()
    PreserveSession={param($s)return $true}
    CloseFence={param($f)$state.FenceReleased++;return $true}.GetNewClosure()
}
function Assert([bool]$ok,[string]$message){if(-not $ok){throw $message}}
$runtime=New-LtRuntimeAdapters -Configuration $config -Operations $ops
$ctx=[ordered]@{}
foreach($step in @('ValidateRequest','StageAndVerify','InventoryAndFence','AwaitQuiescence','BeginSessions','RevalidateAll','ReleaseSessions','ReleaseFence')){Assert (& $runtime[$step] $ctx) ('Falló '+$step)}
Assert ($state.Revalidations -eq 1 -and $state.FenceReleased -eq 1 -and $state.SessionReleased -eq 1) 'Recursos no se cerraron.'
Assert ($state.Packages.Count -eq 3 -and $state.Packages[0] -eq $coordinator.Archive -and $state.Packages[1] -eq $target.Archive -and $state.Packages[2] -eq $coordinator.Archive -and $state.LoadedPath -eq $coordinator.HelperPath) 'Coordinador, origen y destino se confundieron.'
Write-Output 'PASS consumidor runtime usa inventario, cobertura y sesión de fixture'
Write-Output 'PASS retorno .49 verifica destino y carga API del coordinador'
Import-Module (Join-Path $PSScriptRoot '..\scripts\MaintenanceSidCoverage.psm1') -DisableNameChecking
$originalCoverage=$ops.Coverage;$coverageFunction=Get-Command Get-LtSidCoverage -CommandType Function
$coverageState=[pscustomobject]@{Dirs=@([pscustomobject]@{Name=$sid;Attributes=[IO.FileAttributes]::Directory},[pscustomobject]@{Name='maintenance-v1';FullName='X:\fixture\maintenance-v1';Attributes=[IO.FileAttributes]::Directory});Valid=$true}
$readComputer={return [pscustomobject]@{PartOfDomain=$false}};$readAccounts={return [pscustomobject]@{SID=$sid}}.GetNewClosure();$readDirs={return $coverageState.Dirs}.GetNewClosure();$validateDir={param($d)return $coverageState.Valid}.GetNewClosure()
$ops.Coverage={param($c,$items)& $coverageFunction -CurrentSid $c.CurrentSid -Processes $items -TaskSids @() -ReadComputer $readComputer -ReadLocalAccounts $readAccounts -ReadDataDirectories $readDirs -ValidateMaintenanceDirectory $validateDir}.GetNewClosure()
$actualRuntime=New-LtRuntimeAdapters -Configuration $config -Operations $ops
foreach($step in @('ValidateRequest','StageAndVerify','InventoryAndFence','AwaitQuiescence','ReleaseFence')){Assert (& $actualRuntime[$step] $ctx) ('Falló cobertura real '+$step)}
$coverageState.Dirs+= [pscustomobject]@{Name='unknown-folder';Attributes=[IO.FileAttributes]::Directory};$blocked=$false
try{& $actualRuntime.InventoryAndFence $ctx|Out-Null}catch{$blocked=$true}
Assert $blocked 'Carpeta desconocida no bloqueó cobertura real del consumidor.'
$ops.Coverage=$originalCoverage
Write-Output 'PASS consumidor usa cobertura real con carpeta administrativa y rechaza carpeta desconocida'
$state.Items=@([pscustomobject]@{Role='fps-y3-io'});$blocked=$false
try{& $runtime.AwaitQuiescence $ctx|Out-Null}catch{$blocked=$true}
Assert $blocked 'Worker activo fue tratado como quiescente.'
Write-Output 'PASS worker activo impide quiescencia'
$state.Items=@();$state.CountOverride=0;$before=$state.FenceReleased;$blocked=$false
try{& $runtime.InventoryAndFence $ctx|Out-Null}catch{$blocked=$true}
Assert ($blocked -and $state.FenceReleased -eq $before+1) 'Barrera parcial no fue liberada.'
Write-Output 'PASS barrera parcial se libera y bloquea'
$state.Assembly=[string].Assembly;$blocked=$false
try{& $runtime.StageAndVerify $ctx|Out-Null}catch{$blocked=$true}
Assert $blocked 'Una asamblea sin API fue aceptada como coordinador.'
Write-Output 'PASS asamblea sin API no se acepta como coordinador'
$state.SourceHash='0'*64;$blocked=$false
try{& $runtime.ValidateRequest $ctx|Out-Null}catch{$blocked=$true}
Assert $blocked 'Se aceptó un origen instalado que no coincide con sus pins.'
Write-Output 'PASS origen observado diferente bloquea'
$ctx['MayHaveEffects']=$false;$blocked=$false
try{& $runtime.AwaitQuiescence $ctx|Out-Null}catch{$blocked=$true}
Assert $blocked 'No se revalidó el origen antes de mutar.'
Write-Output 'PASS cambio de origen antes de mutar bloquea'
Add-Type -TypeDefinition @'
using System.Collections.Generic;
public sealed class LegionTweaksFenceFixture {
    public int SidCount { get; private set; }
    public static LegionTweaksFenceFixture AcquireVerifiedSids(IEnumerable<string> ids) {
        int count=0; foreach (string id in ids) count++;
        return new LegionTweaksFenceFixture { SidCount=count };
    }
}
'@
$module=Get-Module MaintenanceRuntime;$method=[LegionTweaksFenceFixture].GetMethod('AcquireVerifiedSids')
foreach($sids in @(,@($sid),,@($sid,'S-1-5-21-1-2-3-1002'))){
    $fixture=& $module {param($m,$ids) Invoke-LtFenceFactory -Method $m -Sids $ids} $method $sids
    Assert ($fixture.SidCount -eq $sids.Count) 'Reflexión no entregó una colección única de SID.'
}
Write-Output 'PASS reflexión de fence recibe una lista completa con uno y dos SID'
