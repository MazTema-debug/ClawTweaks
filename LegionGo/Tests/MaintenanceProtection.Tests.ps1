# SPDX-License-Identifier: AGPL-3.0-only
$ErrorActionPreference='Stop'
Import-Module (Join-Path $PSScriptRoot '..\scripts\MaintenanceProtection.psm1') -Force
$module=Get-Module MaintenanceProtection
$user='S-1-5-21-1-2-3-1001';$admin='S-1-5-32-544'
function Rule($sid,$rights,$propagation){return [pscustomobject]@{IdentityReference=[pscustomobject]@{Value=$sid};FileSystemRights=$rights;PropagationFlags=$propagation;AccessControlType=[Security.AccessControl.AccessControlType]::Allow}}
function Check($owner,$rules){& $module {param($o,$r) Assert-LtPrivilegedRules -Owner $o -Rules $r} $owner $rules}
function Reject([scriptblock]$a){$failed=$false;try{& $a|Out-Null}catch{$failed=$true};if(-not $failed){throw 'ACL insegura aceptada.'}}
if(-not (Check $admin @((Rule $user 1 ([Security.AccessControl.PropagationFlags]::None))))){throw 'Lectura segura rechazada.'}
Write-Output 'PASS ACL de lectura no privilegiada aceptada'
Reject {Check $admin @((Rule $user 2 ([Security.AccessControl.PropagationFlags]::None)))}
Write-Output 'PASS escritura no privilegiada bloqueada'
Reject {Check $admin @((Rule $user 0x40000000 ([Security.AccessControl.PropagationFlags]::None)))}
Write-Output 'PASS GenericWrite no privilegiado bloqueado'
if(-not (& $module {param($o,$r) Assert-LtPrivilegedRules -Owner $o -Rules $r -Ancestor} $admin @((Rule $user 2 ([Security.AccessControl.PropagationFlags]::None))))){throw 'Creación de hermanos en ancestro rechazada.'}
Reject {& $module {param($o,$r) Assert-LtPrivilegedRules -Owner $o -Rules $r -Ancestor} $admin @((Rule $user 64 ([Security.AccessControl.PropagationFlags]::None)))}
Write-Output 'PASS ancestro permite creación de hermanos pero no eliminación de hijos'
Reject {Check $user @()}
Write-Output 'PASS propietario no privilegiado bloqueado'
if(-not (Check $admin @((Rule $user 2 ([Security.AccessControl.PropagationFlags]::InheritOnly))))){throw 'ACE de solo herencia rechazada.'}
Write-Output 'PASS ACE solo de herencia no se aplica al objeto actual'
Reject {Set-LtStagingAcl -Tree (Join-Path ([IO.Path]::GetTempPath()) 'fixture-outside-program-files')}
Write-Output 'PASS staging fuera de la ubicación prevista bloqueado antes de cambiar ACL'
Reject {Get-LtProtectedSnapshot -Roots @([IO.Path]::GetTempPath()) -ExcludeY3AdmissionGuard}
Write-Output 'PASS exclusión del marcador Y3 rechaza rutas ajenas'
