# SPDX-License-Identifier: AGPL-3.0-only
# Tests SID derivation and synthetic ACL decisions; does not access real app data.
$ErrorActionPreference='Stop'
Import-Module (Join-Path $PSScriptRoot '..\scripts\MaintenanceUserData.psm1') -Force
$package=Get-LtWidgetPackageSid
if($package -notmatch '^S-1-15-2-'){throw 'SID de paquete inválido.'}
$module=Get-Module MaintenanceUserData;$user='S-1-5-21-1-2-3-1001'
function Rule($sid,$rights){return [pscustomobject]@{IdentityReference=[pscustomobject]@{Value=$sid};FileSystemRights=$rights;PropagationFlags=[Security.AccessControl.PropagationFlags]::None;AccessControlType=[Security.AccessControl.AccessControlType]::Allow}}
if(-not (& $module {param($u,$p,$r)Assert-LtUserDataRules -Owner $u -CurrentSid $u -PackageSid $p -Rules $r} $user $package @((Rule $user 2),(Rule $package 2)))){throw 'ACL de usuario y paquete rechazada.'}
$blocked=$false
try{& $module {param($u,$p,$r)Assert-LtUserDataRules -Owner $u -CurrentSid $u -PackageSid $p -Rules $r} $user $package @((Rule 'S-1-1-0' 2))|Out-Null}catch{$blocked=$true}
if(-not $blocked){throw 'Escritura por Everyone aceptada.'}
Write-Output 'PASS solo usuario, paquete específico y principales privilegiados pueden escribir'
