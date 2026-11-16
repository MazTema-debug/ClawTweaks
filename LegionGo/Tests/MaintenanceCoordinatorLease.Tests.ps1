# SPDX-License-Identifier: AGPL-3.0-only
$ErrorActionPreference='Stop'
Import-Module (Join-Path $PSScriptRoot '..\scripts\MaintenanceCoordinatorLease.psm1') -Force -DisableNameChecking
$root=Join-Path ([IO.Path]::GetTempPath()) ('LegionTweaks-coordinator-lock-'+[Guid]::NewGuid().ToString('N'));$null=New-Item -ItemType Directory -Path $root
$path=Join-Path $root 'coordinator-v1.lock';$check={param($p)return $true}
$one=Open-LtCoordinatorLease -Path $path -AssertProtection $check
if(-not (Test-LtCoordinatorLease -Lease $one) -or $one.AuthorizesMaintenance){throw 'Reserva fixture inválida.'}
$blocked=$false;try{Open-LtCoordinatorLease -Path $path -AssertProtection $check|Out-Null}catch{$blocked=$true}
if(-not $blocked){throw 'Dos coordinadores adquirieron la misma exclusión.'}
Write-Output 'PASS segunda reserva simultánea queda bloqueada'
$one.Path=Join-Path $root 'otro.lock';$blocked=$false
try{Test-LtCoordinatorLease -Lease $one|Out-Null}catch{$blocked=$true}
if(-not $blocked){throw 'Token modificado aceptado.'}
$null=Close-LtCoordinatorLease -Lease $one
$two=Open-LtCoordinatorLease -Path $path -AssertProtection $check
if(-not (Test-LtCoordinatorLease -Lease $two)){throw 'Reserva fixture no se liberó.'}
$null=Close-LtCoordinatorLease -Lease $two
Write-Output 'PASS token cambiado bloquea y el handle propio se libera'
