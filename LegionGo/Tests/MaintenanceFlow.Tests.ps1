# SPDX-License-Identifier: AGPL-3.0-only
# Real backup set, transaction journal, decision record and tree adapter on fixtures.
$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'MaintenanceTreePublication.Tests.ps1')
Import-Module (Join-Path $PSScriptRoot '..\scripts\MaintenanceFlow.psm1') -Force
Import-Module (Join-Path $PSScriptRoot '..\scripts\MaintenancePublicationDecision.psm1') -Force
foreach($direction in @('Update','Return49')){
    $rig=Set-RealTreeAdapter (New-PublicationRig 'none') 'none';$state=$rig.State
    $metadata=@{TaskXml='<fixture/>';TaskWasEnabled=$true;PackageFullName='fixture';TargetCertificateWasPresent=$true;SourceCertificateWasPresent=$true}
    $groups=@([pscustomobject]@{Name='helper';SourceRoot=$state.Live})
    $cfg=@{Direction=$direction;Id=[Guid]::NewGuid().ToString('N');BackupDestination=(Join-Path $state.Root 'complete-backup');Groups=$groups;
        GetMetadata={param($ctx)return $metadata}.GetNewClosure();GetY3Evidence={param($ctx)return [pscustomobject]@{Classification='absent';ContentSha256='';FileIdentitySha256='';ProtectionSha256=('D'*64);AuthorizesMaintenance=$false}};JournalPath=(Join-Path $state.Root 'flow-journal.json');OldSha256=$state.Hash;NewSha256=$state.Hash;CoordinatorSha256=$state.Hash;
        AssertProtection={param($a,$b,$c)return $true};AssertGroups={param($g)return (@($g).Count -eq 1)};AssertPrerequisites={param($ctx)return $true};
        ReadExternalFingerprint={param($ctx)return ('A'*64)};ConfirmPublished={param($ctx)return $true};ConfirmCompensationSafety={param($ctx)return $true};PreserveBlocked={param($ctx)return $true}}
    $flow=New-LtMaintenanceFlow -Configuration $cfg
    foreach($name in @('BackupAndVerify','Checkpoint','Commit','ConfirmCompensationSafety','PreserveBlocked')){$rig.Adapter[$name]=$flow[$name]}
    $rig.TreeConfig.AfterOldRename=$flow.AfterOldRename;$rig.TreeConfig.GetReadyEvidence=$flow.GetReadyEvidence;$rig.TreeConfig.ConfirmPrerequisites=$flow.ConfirmPrerequisites
    $tree=New-LtTreePublicationAdapters -Configuration $rig.TreeConfig
    foreach($name in @('ArmBarrier','VerifyBarrier','PreparePublication','PublishTree','ReconcileCommit','VerifyPublished','Compensate','VerifyCompensated')){$rig.Adapter[$name]=$tree[$name]}
    $result=Invoke-LtMaintenanceTransaction -Direction $direction -Adapter $rig.Adapter -Request @{}
    if($result.Stage -ne 'complete' -or -not $result.Committed){throw ('Flujo fixture no completó: '+$result.Stage+'/'+$result.Error+'/'+$result.CleanupError)}
    $journal=Read-LtTransactionJournal -Path $cfg.JournalPath -AssertProtection $cfg.AssertProtection
    $ready=Read-LtPublishReady -Path $rig.TreeConfig.ReadyPath -AssertProtection $cfg.AssertProtection
    if($journal.Stage -ne 'committed' -or $journal.Id -ne $ready.Id -or $journal.BackupManifestSha256 -ne $ready.BackupSha256){throw 'Registro y respaldo no quedaron vinculados.'}
    Write-Output ('PASS flujo real de respaldo y diario con publicación fixture '+$direction)
}
$rejectRoot=Join-Path ([IO.Path]::GetTempPath()) ('LegionTweaks-trust-fixture-'+[Guid]::NewGuid().ToString('N'));$null=New-Item -ItemType Directory -Path $rejectRoot
$cfg.Id=[Guid]::NewGuid().ToString('N');$cfg.BackupDestination=Join-Path $rejectRoot 'backup';$cfg.JournalPath=Join-Path $rejectRoot 'journal.json';$cfg.RequireExistingTrust=$true
$metadata.TargetCertificateWasPresent=$false
$flow=New-LtMaintenanceFlow -Configuration $cfg;$blocked=$false
try{& $flow.BackupAndVerify @{}|Out-Null}catch{$blocked=$true}
if(-not $blocked -or (Test-Path -LiteralPath $cfg.BackupDestination)){throw 'Confianza faltante no bloqueó antes de crear respaldo.'}
Write-Output 'PASS confianza faltante bloquea antes de efectos o respaldo'
