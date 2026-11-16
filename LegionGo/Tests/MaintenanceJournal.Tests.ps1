# SPDX-License-Identifier: AGPL-3.0-only
$ErrorActionPreference='Stop'
Import-Module (Join-Path $PSScriptRoot '..\scripts\MaintenanceJournal.psm1') -Force
$root=Join-Path ([IO.Path]::GetTempPath()) ('LegionTweaks-journal-fixture-'+[Guid]::NewGuid().ToString('N'))
$null=New-Item -ItemType Directory -Path $root
$path=Join-Path $root 'transaction.json';$protect={param($p)return $true}
$body=[ordered]@{Version=1;Id=[Guid]::NewGuid().ToString('N');Direction='Return49';Stage='prepared';Sequence=0;OldSha256=('A'*64);NewSha256=('B'*64);CoordinatorSha256=('C'*64);BackupManifestSha256=('D'*64)}
function Assert([bool]$ok,[string]$message){if(-not $ok){throw $message}}
function Reject([scriptblock]$a){$failed=$false;try{& $a|Out-Null}catch{$failed=$true};Assert $failed 'El registro aceptó una transición inválida.'}
$null=Write-LtTransactionJournal -Path $path -Body $body -AssertProtection $protect
$body.Stage='backup-verified';$body.Sequence=1
$read=Write-LtTransactionJournal -Path $path -Body $body -AssertProtection $protect -ExpectedStage 'prepared'
Assert ($read.Stage -eq 'backup-verified' -and $read.Sequence -eq 1) 'No se confirmó la transición.'
Write-Output 'PASS creación y transición de registro fixture'
$body.Stage='arm-barrier-intent';$body.Sequence=2;$body.CoordinatorSha256='E'*64
Reject {Write-LtTransactionJournal -Path $path -Body $body -AssertProtection $protect -ExpectedStage 'backup-verified'}
$body.CoordinatorSha256='C'*64
Write-Output 'PASS cambio de pin del coordinador bloqueado'
$body.Stage='committed';$body.Sequence=2
Reject {Write-LtTransactionJournal -Path $path -Body $body -AssertProtection $protect -ExpectedStage 'backup-verified'}
Write-Output 'PASS no se puede saltar a commit'
[IO.File]::AppendAllText($path,' ')
Reject {Read-LtTransactionJournal -Path $path -AssertProtection $protect}
Write-Output 'PASS registro alterado o no canónico bloqueado'
$other=Join-Path $root 'blocked.json';$body.Stage='prepared';$body.Sequence=0
Reject {Write-LtTransactionJournal -Path $other -Body $body -AssertProtection {param($p)return $true,$false}}
Assert (-not (Test-Path -LiteralPath $other)) 'Se creó registro con protección ambigua.'
Write-Output 'PASS protección ambigua bloquea creación'
Import-Module (Join-Path $PSScriptRoot '..\scripts\MaintenanceReconcile.psm1') -Force
$fresh=Join-Path $root 'missing-ready-journal.json';$body.Stage='prepared';$body.Sequence=0
$null=Write-LtTransactionJournal -Path $fresh -Body $body -AssertProtection $protect
$decision=Invoke-LtPublicationReconciliation -ReadyPath (Join-Path $root 'missing-ready.json') -JournalPath $fresh -AssertProtection $protect -ReadExternalFingerprint {param($r)throw 'No debe consultar efectos'} -ConfirmRecoverySafe {param($r)throw 'No debe compensar'} -VerifyBackup {param($r)throw 'No debe verificar respaldo'} -Reservation residual -Recovery pending -Finalize
Assert ($decision.Publication -eq 'recovery-required' -and $decision.PublicationReason -eq 'publish-ready-absent' -and -not $decision.Annotated -and $decision.ReservationCleanupPending) 'Ready ausente no conservó bloqueo.'
Write-Output 'PASS registro sin publish-ready informa recuperación sin efectos ni anotación'
