# SPDX-License-Identifier: AGPL-3.0-only
$ErrorActionPreference='Stop'
Import-Module (Join-Path $PSScriptRoot '..\scripts\MaintenanceTransactionDirectory.psm1') -Force
Import-Module (Join-Path $PSScriptRoot '..\scripts\MaintenanceJournal.psm1') -Force
$root=Join-Path ([IO.Path]::GetTempPath()) ('LegionTweaks-transactions-fixture-'+[Guid]::NewGuid().ToString('N'));$null=New-Item -ItemType Directory -Path $root
$protect={param($p)return $true};$create={param($p)$null=New-Item -ItemType Directory -Path $p}
if(-not (Test-LtPriorTransactions -Directory $root -AssertProtection $protect)){throw 'Directorio vacío rechazado.'}
$id='a'*32;$dir=New-LtTransactionDirectory -Directory $root -Id $id -AssertProtection $protect -Create $create
$blocked=$false;try{Test-LtPriorTransactions -Directory $root -AssertProtection $protect|Out-Null}catch{$blocked=$true}
if(-not $blocked){throw 'Transacción sin diario no bloqueó.'}
$journal=Join-Path $dir 'flow-journal.json';$body=[ordered]@{Version=1;Id=$id;Direction='Update';Stage='prepared';Sequence=0;OldSha256=('A'*64);NewSha256=('B'*64);CoordinatorSha256=('C'*64);BackupManifestSha256=('D'*64)}
$null=Write-LtTransactionJournal -Path $journal -Body $body -AssertProtection $protect
$blocked=$false;try{Test-LtPriorTransactions -Directory $root -AssertProtection $protect|Out-Null}catch{$blocked=$true}
if(-not $blocked){throw 'Transacción pendiente no bloqueó.'}
$body.Stage='recovery-required';$body.Sequence=1;$null=Write-LtTransactionJournal -Path $journal -Body $body -AssertProtection $protect -ExpectedStage 'prepared'
$body.Stage='compensated';$body.Sequence=2;$null=Write-LtTransactionJournal -Path $journal -Body $body -AssertProtection $protect -ExpectedStage 'recovery-required'
if(-not (Test-LtPriorTransactions -Directory $root -AssertProtection $protect)){throw 'Transacción compensada no reconocida.'}
Write-Output 'PASS transacciones sin resolver bloquean; compensada permite continuar'
