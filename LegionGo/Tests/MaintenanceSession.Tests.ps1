# SPDX-License-Identifier: AGPL-3.0-only
# Signature decisions use synthetic objects; no certificate store is changed.
$ErrorActionPreference='Stop'
Import-Module (Join-Path $PSScriptRoot '..\scripts\MaintenanceSession.psm1') -Force
$module=Get-Module MaintenanceSession
$thumb='A'*40
$signer=[pscustomobject]@{Thumbprint=$thumb}
$pending=[pscustomobject]@{Status='UnknownError';StatusMessage='Untrusted root.';SignerCertificate=$signer}
$chain=@([pscustomobject]@{Status=[Security.Cryptography.X509Certificates.X509ChainStatusFlags]::UntrustedRoot;StatusInformation='Untrusted root.'})
function Assert([bool]$value,[string]$message){if(-not $value){throw $message}}
function Decide($signature,$statuses,[bool]$allow){& $module {param($s,$t,$c,$a) Test-LtSignatureDecision -Signature $s -ExpectedSigner $t -ChainStatus $c -AllowUntrustedRoot $a} $signature $thumb $statuses $allow}
function Reject([scriptblock]$action){$blocked=$false;try{& $action|Out-Null}catch{$blocked=$true};Assert $blocked 'Aceptó firma inválida.'}
Assert ((Decide $pending $chain $true) -eq 'verified-untrusted-root') 'No distinguió confianza pendiente.'
Write-Output 'PASS raíz no confiada reconocida sin importar certificado'
Reject {Decide $pending $chain $false}
Write-Output 'PASS raíz no confiada requiere política explícita'
$bad=[pscustomobject]@{Status='HashMismatch';StatusMessage='Untrusted root.';SignerCertificate=$signer}
Reject {Decide $bad $chain $true}
Write-Output 'PASS hash de firma incorrecto bloquea'
$unknown=[pscustomobject]@{Status='UnknownError';StatusMessage='Otro error';SignerCertificate=$signer}
Reject {Decide $unknown $chain $true}
Write-Output 'PASS error genérico no se convierte en raíz no confiada'
$badSigner=[pscustomobject]@{Status='Valid';StatusMessage='Valid';SignerCertificate=[pscustomobject]@{Thumbprint=('B'*40)}}
Reject {Decide $badSigner @() $true}
Write-Output 'PASS firmante diferente bloquea'
$forged=[pscustomobject]@{Assembly=[string].Assembly;TrustStatus='trusted';Sha256=('A'*64);Signer=$thumb;AuthorizesMaintenance=$false}
Reject {Open-LtMandoSession -VerifiedHelper $forged -ExpectedSid 'S-1-5-21-1-2-3-1001'}
Write-Output 'PASS asamblea no cargada mediante verificación se rechaza antes de reservar'
Add-Type -TypeDefinition @'
using System;
public sealed class LegionTweaksReasonFixture : Exception {
    public string Reason { get { return "abandoned_mutex"; } }
}
public static class LegionTweaksReflectionFailureFixture {
    public static void Begin() { throw new LegionTweaksReasonFixture(); }
}
'@
$caught=$null
try{[LegionTweaksReflectionFailureFixture].GetMethod('Begin').Invoke($null,[object[]]@())}catch{$caught=$_.Exception}
$mapped=& $module {param($e) New-LtSessionFailure -Failure $e -ReservationMayRemain $true -Fallback 'unknown'} $caught
Assert ($mapped.Data['LegionTweaksReason'] -eq 'abandoned_mutex' -and $mapped.Data['ReservationMayRemain']) 'No se propagó el motivo interior.'
Write-Output 'PASS excepción reflejada conserva motivo y posible marcador residual'
