# SPDX-License-Identifier: AGPL-3.0-only
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
Import-Module (Join-Path $PSScriptRoot 'MaintenanceBackupSet.psm1') -ErrorAction Stop
Import-Module (Join-Path $PSScriptRoot 'MaintenanceJournal.psm1') -ErrorAction Stop
function Assert-LtFlowDecision([scriptblock]$Callback,$Argument){
    $r=@(& $Callback $Argument)
    if($r.Count -ne 1 -or $r[0] -isnot [bool] -or -not $r[0]){throw 'Una condición de flujo no se confirmó.'}
}

# Integrates backup and durable checkpoints. The caller still must implement
# and prove complete protection, inventory, external effects and recovery.
function New-LtMaintenanceFlow {
    [CmdletBinding()]
    param([Parameter(Mandatory)][System.Collections.IDictionary]$Configuration)
    foreach($key in @('Direction','Id','BackupDestination','Groups','GetMetadata','GetY3Evidence','JournalPath','OldSha256','NewSha256','CoordinatorSha256','AssertProtection','AssertGroups','AssertPrerequisites','ReadExternalFingerprint','ConfirmPublished','ConfirmCompensationSafety','PreserveBlocked')){
        if(-not $Configuration.Contains($key) -or $null -eq $Configuration[$key]){throw "Falta configuración de flujo $key."}
    }
    if($Configuration.Direction -notin @('Update','Return49') -or $Configuration.Id -cnotmatch '^[a-f0-9]{32}$' -or @($Configuration.Groups).Count -eq 0){throw 'Identidad o grupos de flujo inválidos.'}
    foreach($key in @('OldSha256','NewSha256','CoordinatorSha256')){if($Configuration[$key] -cnotmatch '^[A-F0-9]{64}$'){throw 'Pin de flujo inválido.'}}
    foreach($key in @('GetMetadata','GetY3Evidence','AssertProtection','AssertGroups','AssertPrerequisites','ReadExternalFingerprint','ConfirmPublished','ConfirmCompensationSafety','PreserveBlocked')){if($Configuration[$key] -isnot [scriptblock]){throw "Falta callback de flujo $key."}}
    $makeBackup=Get-Command New-LtBackupSet -CommandType Function -ErrorAction Stop
    $testBackup=Get-Command Test-LtBackupSet -CommandType Function -ErrorAction Stop
    $makeRestore=Get-Command New-LtRestoreTree -CommandType Function -ErrorAction Stop
    $testRestore=Get-Command Test-LtRestoreTree -CommandType Function -ErrorAction Stop
    $getBackedY3=Get-Command Get-LtBackedY3Snapshot -CommandType Function -ErrorAction Stop
    $writeJournal=Get-Command Write-LtTransactionJournal -CommandType Function -ErrorAction Stop
    $readJournal=Get-Command Read-LtTransactionJournal -CommandType Function -ErrorAction Stop
    $check=${function:Assert-LtFlowDecision}
    $state=[pscustomobject]@{Backup=$null;Journal=$null;OldJournalSha256=$null}
    $backup={param($ctx)
        $null=& $check $Configuration.AssertGroups $Configuration.Groups
        $metadata=@(& $Configuration.GetMetadata $ctx)
        if($metadata.Count -ne 1 -or $metadata[0] -isnot [System.Collections.IDictionary]){throw 'Metadatos de respaldo inciertos.'}
        if($Configuration.Contains('RequireExistingTrust') -and $Configuration.RequireExistingTrust -eq $true -and ($metadata[0].TargetCertificateWasPresent -ne $true -or $metadata[0].SourceCertificateWasPresent -ne $true)){throw 'Las firmas de origen y destino deben estar previamente confiadas para este retorno.'}
        $state.Backup=& $makeBackup -Destination $Configuration.BackupDestination -Groups $Configuration.Groups -Metadata $metadata[0] -AssertProtection $Configuration.AssertProtection
        $confirmed=& $testBackup -Destination $Configuration.BackupDestination -ExpectedManifestSha256 $state.Backup.ManifestSha256 -AssertProtection $Configuration.AssertProtection
        if($confirmed.AuthorizesMaintenance -ne $false){throw 'Respaldo inválido.'}
        if($Configuration.Contains('RequireY3BackupMatch') -and $Configuration.RequireY3BackupMatch -eq $true){
            $y3=@(& $Configuration.GetY3Evidence $ctx)
            if($y3.Count -ne 1 -or $y3[0].AuthorizesMaintenance -ne $false){throw 'Sesión Y3 no confirmada para respaldo.'}
            $backed=& $getBackedY3 -BackupDestination $Configuration.BackupDestination -ExpectedManifestSha256 $state.Backup.ManifestSha256 -AssertProtection $Configuration.AssertProtection
            if($backed.Classification -ne $y3[0].Classification -or $backed.ContentSha256 -cne [string]$y3[0].ContentSha256 -or $backed.FileIdentitySha256 -cne [string]$y3[0].FileIdentitySha256){throw 'Diario respaldado no coincide con la sesión Y3.'}
        }
        if($Configuration.Contains('RestoreTree')){
            $null=& $makeRestore -BackupDestination $Configuration.BackupDestination -ExpectedManifestSha256 $state.Backup.ManifestSha256 -RestoreTree $Configuration.RestoreTree -AssertProtection $Configuration.AssertProtection
            $null=& $testRestore -BackupDestination $Configuration.BackupDestination -ExpectedManifestSha256 $state.Backup.ManifestSha256 -RestoreTree $Configuration.RestoreTree -AssertProtection $Configuration.AssertProtection
        }
        $body=[ordered]@{Version=1;Id=$Configuration.Id;Direction=$Configuration.Direction;Stage='prepared';Sequence=0;OldSha256=$Configuration.OldSha256;NewSha256=$Configuration.NewSha256;CoordinatorSha256=$Configuration.CoordinatorSha256;BackupManifestSha256=$state.Backup.ManifestSha256}
        $state.Journal=& $writeJournal -Path $Configuration.JournalPath -Body $body -AssertProtection $Configuration.AssertProtection
        return $true
    }.GetNewClosure()
    $record={param($stage)
        if(-not $state.Journal){throw 'Falta diario de flujo.'}
        $prior=$state.Journal;$body=[ordered]@{Version=1;Id=$prior.Id;Direction=$prior.Direction;Stage=$stage;Sequence=[long]($prior.Sequence+1);OldSha256=$prior.OldSha256;NewSha256=$prior.NewSha256;CoordinatorSha256=$prior.CoordinatorSha256;BackupManifestSha256=$prior.BackupManifestSha256}
        $state.Journal=& $writeJournal -Path $Configuration.JournalPath -Body $body -AssertProtection $Configuration.AssertProtection -ExpectedStage $prior.Stage
        return $true
    }.GetNewClosure()
    $checkpoint={param($ctx)return (& $record $ctx.Stage)}.GetNewClosure()
    $oldRenamed={param($ctx)return (& $record 'old-renamed')}.GetNewClosure()
    $evidence={param($ctx)
        $null=& $check $Configuration.AssertPrerequisites $ctx
        $null=& $testBackup -Destination $Configuration.BackupDestination -ExpectedManifestSha256 $state.Backup.ManifestSha256 -AssertProtection $Configuration.AssertProtection
        if($Configuration.Contains('RestoreTree')){$null=& $testRestore -BackupDestination $Configuration.BackupDestination -ExpectedManifestSha256 $state.Backup.ManifestSha256 -RestoreTree $Configuration.RestoreTree -AssertProtection $Configuration.AssertProtection}
        $journal=& $readJournal -Path $Configuration.JournalPath -AssertProtection $Configuration.AssertProtection
        if($journal.Stage -ne 'old-renamed' -or $journal.Id -ne $Configuration.Id -or $journal.BackupManifestSha256 -ne $state.Backup.ManifestSha256){throw 'Diario previo a publicación no coincide.'}
        $external=@(& $Configuration.ReadExternalFingerprint $ctx)
        if($external.Count -ne 1 -or [string]$external[0] -cnotmatch '^[A-F0-9]{64}$'){throw 'Huella externa incierta.'}
        $y3=@(& $Configuration.GetY3Evidence $ctx)
        if($y3.Count -ne 1 -or $y3[0].AuthorizesMaintenance -ne $false -or $y3[0].Classification -notin @('absent','restored_with_original') -or [string]$y3[0].ProtectionSha256 -cnotmatch '^[A-F0-9]{64}$'){throw 'Evidencia Y3 incierta.'}
        if($Configuration.Contains('RequireY3BackupMatch') -and $Configuration.RequireY3BackupMatch -eq $true){
            $backed=& $getBackedY3 -BackupDestination $Configuration.BackupDestination -ExpectedManifestSha256 $state.Backup.ManifestSha256 -AssertProtection $Configuration.AssertProtection
            if($backed.Classification -ne $y3[0].Classification -or $backed.ContentSha256 -cne [string]$y3[0].ContentSha256 -or $backed.FileIdentitySha256 -cne [string]$y3[0].FileIdentitySha256){throw 'Diario respaldado no coincide con la sesión Y3.'}
        }
        $state.OldJournalSha256=(Get-FileHash -LiteralPath $Configuration.JournalPath -Algorithm SHA256).Hash
        return @{Id=$Configuration.Id;JournalSha256=$state.OldJournalSha256;BackupSha256=$state.Backup.ManifestSha256;ExternalSha256=[string]$external[0];Y3Classification=[string]$y3[0].Classification;Y3ContentSha256=[string]$y3[0].ContentSha256;Y3IdentitySha256=[string]$y3[0].FileIdentitySha256;Y3ProtectionSha256=[string]$y3[0].ProtectionSha256}
    }.GetNewClosure()
    $prerequisites={param($ready)
        $null=& $check $Configuration.AssertPrerequisites $ready
        $null=& $testBackup -Destination $Configuration.BackupDestination -ExpectedManifestSha256 $state.Backup.ManifestSha256 -AssertProtection $Configuration.AssertProtection
        if($Configuration.Contains('RestoreTree')){$null=& $testRestore -BackupDestination $Configuration.BackupDestination -ExpectedManifestSha256 $state.Backup.ManifestSha256 -RestoreTree $Configuration.RestoreTree -AssertProtection $Configuration.AssertProtection}
        $journal=& $readJournal -Path $Configuration.JournalPath -AssertProtection $Configuration.AssertProtection
        if($ready.Id -ne $Configuration.Id -or $ready.BackupSha256 -ne $state.Backup.ManifestSha256 -or $ready.JournalSha256 -ne $state.OldJournalSha256 -or $journal.Id -ne $ready.Id -or $journal.Stage -notin @('old-renamed','publish-ready') -or $journal.BackupManifestSha256 -ne $ready.BackupSha256){throw 'Evidencia previa a publicación cambió.'}
        $y3=@(& $Configuration.GetY3Evidence $ready)
        if($y3.Count -ne 1 -or $y3[0].AuthorizesMaintenance -ne $false -or $ready.Y3Classification -ne $y3[0].Classification -or $ready.Y3ContentSha256 -cne [string]$y3[0].ContentSha256 -or $ready.Y3IdentitySha256 -cne [string]$y3[0].FileIdentitySha256 -or $ready.Y3ProtectionSha256 -cne [string]$y3[0].ProtectionSha256){throw 'Snapshot Y3 cambió antes de publicar.'}
        return $true
    }.GetNewClosure()
    $commit={param($ctx)$null=& $check $Configuration.ConfirmPublished $ctx;return $true}.GetNewClosure()
    $compensation={param($ctx)
        $null=& $check $Configuration.ConfirmCompensationSafety $ctx
        $null=& $testBackup -Destination $Configuration.BackupDestination -ExpectedManifestSha256 $state.Backup.ManifestSha256 -AssertProtection $Configuration.AssertProtection
        if($Configuration.Contains('RestoreTree')){$null=& $testRestore -BackupDestination $Configuration.BackupDestination -ExpectedManifestSha256 $state.Backup.ManifestSha256 -RestoreTree $Configuration.RestoreTree -AssertProtection $Configuration.AssertProtection}
        return $true
    }.GetNewClosure()
    $blocked={param($ctx)$null=& $check $Configuration.PreserveBlocked $ctx;return $true}.GetNewClosure()
    $verifyRestore={param($root)
        return (& $testRestore -BackupDestination $Configuration.BackupDestination -ExpectedManifestSha256 $state.Backup.ManifestSha256 -RestoreTree $root -AssertProtection $Configuration.AssertProtection)
    }.GetNewClosure()
    $getMetadata={param($ctx)
        if(-not $state.Backup){throw 'Respaldo aún no está disponible.'}
        $checked=& $testBackup -Destination $Configuration.BackupDestination -ExpectedManifestSha256 $state.Backup.ManifestSha256 -AssertProtection $Configuration.AssertProtection
        return $checked.Metadata
    }.GetNewClosure()
    return @{BackupAndVerify=$backup;Checkpoint=$checkpoint;AfterOldRename=$oldRenamed;GetReadyEvidence=$evidence;ConfirmPrerequisites=$prerequisites;Commit=$commit;ConfirmCompensationSafety=$compensation;PreserveBlocked=$blocked;VerifyRestore=$verifyRestore;GetBackupMetadata=$getMetadata}
}
Export-ModuleMember -Function New-LtMaintenanceFlow
