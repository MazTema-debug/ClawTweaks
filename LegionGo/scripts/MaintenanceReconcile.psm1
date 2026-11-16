# SPDX-License-Identifier: AGPL-3.0-only
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
Import-Module (Join-Path $PSScriptRoot 'MaintenanceJournal.psm1') -ErrorAction Stop
Import-Module (Join-Path $PSScriptRoot 'MaintenancePublicationDecision.psm1') -ErrorAction Stop

# Reconciliation is independent of the dead coordinator. The observations
# and recovery predicate are read-only callbacks; it never reopens/releases a
# dead session or changes the Y3 journal, original, marker or backup.
function Invoke-LtPublicationReconciliation {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$ReadyPath,[Parameter(Mandatory)][string]$JournalPath,
          [Parameter(Mandatory)][scriptblock]$AssertProtection,[Parameter(Mandatory)][scriptblock]$ReadExternalFingerprint,
          [Parameter(Mandatory)][scriptblock]$ConfirmRecoverySafe,[Parameter(Mandatory)][scriptblock]$VerifyBackup,
          [scriptblock]$AssertExclusive,
          [ValidateSet('clear','residual','busy','abandoned_mutex','unknown')][string]$Reservation='unknown',
          [ValidateSet('restored','pending','unknown')][string]$Recovery='unknown',[switch]$Finalize)
    $journal=Read-LtTransactionJournal -Path $JournalPath -AssertProtection $AssertProtection
    try{$readyItem=Get-Item -LiteralPath $ReadyPath -Force -ErrorAction Stop}catch [System.Management.Automation.ItemNotFoundException]{$readyItem=$null}
    if(-not $readyItem){
        return [pscustomobject]@{Publication='recovery-required';PublicationReason='publish-ready-absent';JournalStage=$journal.Stage;Annotated=$false;
            PriorY3Classification=$null;PriorY3ContentSha256=$null;PriorY3IdentitySha256=$null;PriorY3ProtectionSha256=$null;
            Reservation=$Reservation;ReservationCleanupPending=($Reservation -ne 'clear');PhysicalRecovery=$Recovery;RecoveryReviewPending=($Recovery -ne 'restored');MaintenanceAuthorized=$false}
    }
    $ready=Read-LtPublishReady -Path $ReadyPath -AssertProtection $AssertProtection
    if($ready.Id -ne $journal.Id -or $ready.Direction -ne $journal.Direction -or $ready.BackupSha256 -ne $journal.BackupManifestSha256 -or $journal.Stage -notin @('old-renamed','publish-ready','committed')){throw 'Registro y publicación no pertenecen a la misma frontera.'}
    $offset=switch($journal.Stage){'old-renamed'{0};'publish-ready'{1};'committed'{2}}
    if([long]$journal.Sequence -lt $offset){throw 'Secuencia de frontera inválida.'}
    $prior=[pscustomobject]@{Version=1;Id=$journal.Id;Direction=$journal.Direction;Stage='old-renamed';Sequence=[long]($journal.Sequence-$offset);OldSha256=$journal.OldSha256;NewSha256=$journal.NewSha256;CoordinatorSha256=$journal.CoordinatorSha256;BackupManifestSha256=$journal.BackupManifestSha256}
    if((Get-LtJournalBodySha256 -Body $prior) -ne $ready.JournalSha256){throw 'La evidencia del registro anterior no coincide.'}
    $decision=Get-LtPublicationDecision -ReadyPath $ReadyPath -AssertProtection $AssertProtection -ReadExternalFingerprint $ReadExternalFingerprint -ConfirmRecoverySafe $ConfirmRecoverySafe
    if($journal.Stage -eq 'old-renamed' -and $decision.Decision -in @('published','published-review-required')){
        $decision=[pscustomobject]@{Decision='recovery-required';Reason='publication-before-ready-checkpoint'}
    }
    if($journal.Stage -eq 'committed' -and $decision.Decision -eq 'compensation-permitted'){
        $decision=[pscustomobject]@{Decision='recovery-required';Reason='committed-tree-missing'}
    }
    if($decision.Decision -eq 'published'){
        try{$backup=@(& $VerifyBackup $ready)}catch{$backup=@()}
        if($backup.Count -ne 1 -or $backup[0] -isnot [bool] -or -not $backup[0]){$decision=[pscustomobject]@{Decision='published-review-required';Reason='backup-unverifiable'}}
    }
    $annotated=$false
    if($Finalize -and $decision.Decision -eq 'published' -and $journal.Stage -eq 'publish-ready'){
        if(-not $AssertExclusive){throw 'La anotación requiere exclusión de coordinadores.'}
        $exclusive=@(& $AssertExclusive $ready)
        if($exclusive.Count -ne 1 -or $exclusive[0] -isnot [bool] -or -not $exclusive[0]){throw 'Exclusión de coordinadores no confirmada.'}
        $body=[ordered]@{Version=1;Id=$journal.Id;Direction=$journal.Direction;Stage='committed';Sequence=[long]($journal.Sequence+1);OldSha256=$journal.OldSha256;NewSha256=$journal.NewSha256;CoordinatorSha256=$journal.CoordinatorSha256;BackupManifestSha256=$journal.BackupManifestSha256}
        $journal=Write-LtTransactionJournal -Path $JournalPath -Body $body -AssertProtection $AssertProtection -ExpectedStage 'publish-ready'
        $annotated=$true
    }
    $report=New-LtReconciliationReport -Publication $decision -Reservation $Reservation -Recovery $Recovery
    return [pscustomobject]@{Publication=$report.Publication;PublicationReason=$report.PublicationReason;JournalStage=$journal.Stage;Annotated=$annotated;
        PriorY3Classification=$ready.Y3Classification;PriorY3ContentSha256=$ready.Y3ContentSha256;PriorY3IdentitySha256=$ready.Y3IdentitySha256;PriorY3ProtectionSha256=$ready.Y3ProtectionSha256;
        Reservation=$report.Reservation;ReservationCleanupPending=$report.ReservationCleanupPending;PhysicalRecovery=$report.PhysicalRecovery;RecoveryReviewPending=$report.RecoveryReviewPending;MaintenanceAuthorized=$false}
}
Export-ModuleMember -Function Invoke-LtPublicationReconciliation
