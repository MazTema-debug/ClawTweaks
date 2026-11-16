# SPDX-License-Identifier: AGPL-3.0-only
# Child processes operate only on unique fixture directories. No product paths.
param([ValidateSet('Parent','Child')][string]$Mode='Parent',[string]$Root,[string]$Boundary,[string]$Direction)
$ErrorActionPreference='Stop'
Import-Module (Join-Path $PSScriptRoot '..\scripts\MaintenancePublicationDecision.psm1') -Force
Import-Module (Join-Path $PSScriptRoot '..\scripts\MaintenanceNative.psm1') -ErrorAction Stop
$protect={param($p)return $true}
function Assert([bool]$ok,[string]$message){if(-not $ok){throw $message}}
function Pause-At([string]$name){
    if($Boundary -eq $name){[IO.File]::WriteAllText((Join-Path $Root 'child-ready'),$name);while($true){Start-Sleep -Milliseconds 100}}
}
function Require-Fixture([string]$path){
    $full=[IO.Path]::GetFullPath($path)
    $prefix=[IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\')+'\LegionTweaks-publish-fixture-'
    if(-not $full.StartsWith($prefix,[StringComparison]::OrdinalIgnoreCase) -or -not [IO.File]::Exists((Join-Path $full 'fixture-only.marker'))){throw 'Ruta de fixture inválida.'}
    return $full
}
if($Mode -eq 'Child'){
    $Root=Require-Fixture $Root
    $ready=Join-Path $Root 'publish-ready.json';$live=Join-Path $Root 'live';$staging=Join-Path $Root 'staging';$previous=Join-Path $Root 'previous'
    $external=(Get-FileHash -LiteralPath (Join-Path $Root 'external.fixture') -Algorithm SHA256).Hash
    $tree=Get-LtPublicationTreeFingerprint -Root $staging -AssertProtection $protect
    $body=[ordered]@{Version=1;Id=[Guid]::NewGuid().ToString('N');Direction=$Direction;Live=$live;Staging=$staging;Previous=$previous;TreeSha256=$tree.Sha256;ExternalSha256=$external;JournalSha256=('A'*64);BackupSha256=('B'*64);Y3Classification='absent';Y3ContentSha256='';Y3IdentitySha256='';Y3ProtectionSha256=('C'*64)}
    $prerequisites={param($ready)return $true}
    $readExternal={param($ready)return (Get-FileHash -LiteralPath (Join-Path $Root 'external.fixture') -Algorithm SHA256).Hash}.GetNewClosure()
    $null=Write-LtPublishReady -Path $ready -Evidence $body -AssertProtection $protect -ConfirmPrerequisites $prerequisites
    Pause-At 'before-rename'
    $null=Invoke-LtPublishReadyRename -ReadyPath $ready -AssertProtection $protect -ConfirmPrerequisites $prerequisites -ReadExternalFingerprint $readExternal
    Pause-At 'after-rename'
    Pause-At 'before-annotation'
    $null=Write-LtCheckpointWriteThrough -Path (Join-Path $Root 'annotation.json') -Content '{"published":true}'
    Pause-At 'after-annotation'
    throw 'Frontera de prueba desconocida.'
}
foreach($directionCase in @('Update','Return49')){
    foreach($boundaryCase in @('before-rename','after-rename','before-annotation','after-annotation')){
        $Root=Join-Path ([IO.Path]::GetTempPath()) ('LegionTweaks-publish-fixture-'+[Guid]::NewGuid().ToString('N'))
        $null=New-Item -ItemType Directory -Path $Root
        [IO.File]::WriteAllText((Join-Path $Root 'fixture-only.marker'),'synthetic only')
        $null=New-Item -ItemType Directory -Path (Join-Path $Root 'previous')
        $null=New-Item -ItemType Directory -Path (Join-Path $Root 'staging\Qam') -Force
        [IO.File]::WriteAllText((Join-Path $Root 'previous\disabled.fixture'),'old fixture, disabled')
        [IO.File]::WriteAllText((Join-Path $Root 'staging\helper.fixture'),'target helper')
        [IO.File]::WriteAllText((Join-Path $Root 'staging\Qam\qam.fixture'),'target qam')
        [IO.File]::WriteAllText((Join-Path $Root 'external.fixture'),'simulated AppX, task, trust and startup')
        $info=[Diagnostics.ProcessStartInfo]::new();$info.FileName=Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe'
        $info.Arguments='-NoProfile -NonInteractive -ExecutionPolicy Bypass -File "'+$PSCommandPath+'" -Mode Child -Root "'+$Root+'" -Boundary '+$boundaryCase+' -Direction '+$directionCase
        $info.UseShellExecute=$false;$info.CreateNoWindow=$true;$info.RedirectStandardError=$true
        $child=[Diagnostics.Process]::Start($info)
        try{
            $until=[DateTime]::UtcNow.AddSeconds(20)
            while(-not [IO.File]::Exists((Join-Path $Root 'child-ready')) -and -not $child.HasExited -and [DateTime]::UtcNow -lt $until){Start-Sleep -Milliseconds 40}
            if(-not [IO.File]::Exists((Join-Path $Root 'child-ready'))){throw ('Fixture no alcanzó '+$boundaryCase+': '+$child.StandardError.ReadToEnd())}
            $child.Kill();if(-not $child.WaitForExit(10000)){throw 'Proceso fixture no terminó.'}
        }finally{if(-not $child.HasExited){$child.Kill();$child.WaitForExit()};$child.Dispose()}
        $external={param($r)return (Get-FileHash -LiteralPath (Join-Path $Root 'external.fixture') -Algorithm SHA256).Hash}.GetNewClosure()
        $recovery={param($r)return [IO.File]::Exists((Join-Path $r.Previous 'disabled.fixture'))}
        $decision=Get-LtPublicationDecision -ReadyPath (Join-Path $Root 'publish-ready.json') -AssertProtection $protect -ReadExternalFingerprint $external -ConfirmRecoverySafe $recovery
        $expected=if($boundaryCase -eq 'before-rename'){'compensation-permitted'}else{'published'}
        Assert ($decision.Decision -eq $expected) ('Clasificación incorrecta '+$directionCase+'/'+$boundaryCase+': '+$decision.Decision+'/'+$decision.Reason)
        if($boundaryCase -eq 'after-rename'){
            # Simulate a later legitimate .49 Y3 session. Neither this journal,
            # its older backup nor the residual marker is touched by the reader.
            $journal=Join-Path $Root 'y3-journal.fixture';$backup=Join-Path $Root 'y3-backup.fixture';$marker=Join-Path $Root 'reservation.guard.fixture'
            [IO.File]::WriteAllText($backup,'Restored:old-original');[IO.File]::WriteAllText($journal,'Preparing:new-original');[IO.File]::WriteAllText($marker,'residual')
            $journalHash=(Get-FileHash -LiteralPath $journal -Algorithm SHA256).Hash;$backupHash=(Get-FileHash -LiteralPath $backup -Algorithm SHA256).Hash;$markerHash=(Get-FileHash -LiteralPath $marker -Algorithm SHA256).Hash
            $again=Get-LtPublicationDecision -ReadyPath (Join-Path $Root 'publish-ready.json') -AssertProtection $protect -ReadExternalFingerprint $external -ConfirmRecoverySafe $recovery
            foreach($reservation in @('residual','busy','abandoned_mutex','unknown')){
                $report=New-LtReconciliationReport -Publication $again -Reservation $reservation -Recovery pending
                Assert ($report.Publication -eq 'published' -and $report.ReservationCleanupPending -and $report.RecoveryReviewPending -and -not $report.MaintenanceAuthorized) 'La publicación, reserva y recuperación no quedaron separadas.'
            }
            Assert ((Get-FileHash -LiteralPath $journal -Algorithm SHA256).Hash -eq $journalHash -and (Get-FileHash -LiteralPath $backup -Algorithm SHA256).Hash -eq $backupHash -and (Get-FileHash -LiteralPath $marker -Algorithm SHA256).Hash -eq $markerHash) 'Se modificó diario, respaldo o marcador del fixture.'
            [IO.File]::WriteAllText((Join-Path $Root 'external.fixture'),'changed after publication')
            $changed=Get-LtPublicationDecision -ReadyPath (Join-Path $Root 'publish-ready.json') -AssertProtection $protect -ReadExternalFingerprint $external -ConfirmRecoverySafe $recovery
            Assert ($changed.Decision -eq 'published-review-required') 'Estado externo cambiado permitió retorno automático.'
            [IO.File]::WriteAllText((Join-Path $Root 'live\helper.fixture'),'unexpected content')
            $changed=Get-LtPublicationDecision -ReadyPath (Join-Path $Root 'publish-ready.json') -AssertProtection $protect -ReadExternalFingerprint $external -ConfirmRecoverySafe $recovery
            Assert ($changed.Decision -eq 'recovery-required') 'Árbol desconocido aceptado.'
        }
        Write-Output ('PASS caída fixture '+$directionCase+' '+$boundaryCase+' -> '+$decision.Decision)
    }
}
