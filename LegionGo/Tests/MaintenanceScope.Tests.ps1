# SPDX-License-Identifier: AGPL-3.0-only
$ErrorActionPreference='Stop'
Import-Module (Join-Path $PSScriptRoot '..\scripts\MaintenanceScope.psm1') -Force
$root=Join-Path ([IO.Path]::GetTempPath()) ('LegionTweaks-scope-fixture-'+[Guid]::NewGuid().ToString('N'));$null=New-Item -ItemType Directory -Path $root
$allowed=Join-Path $root 'allowed';$null=New-Item -ItemType Directory -Path $allowed
$state=[pscustomobject]@{Anchor='A'*64;Paths='B'*64;Valid=$true}
$snapshot={param($paths,$shallow)return [pscustomobject]@{ProtectionSha256=$(if($shallow){$state.Anchor}else{$state.Paths});AuthorizesMaintenance=(-not $state.Valid)}}.GetNewClosure()
$guard=New-LtScopeGuard -Anchors @($root) -AllowedRoots @($allowed) -Snapshot $snapshot
if(-not (Test-LtScopeGuard -Guard $guard -Paths @((Join-Path $allowed 'new\file')))){throw 'Ámbito fixture válido rechazado.'}
$blocked=$false;try{Test-LtScopeGuard -Guard $guard -Paths @((Join-Path $root 'outside'))|Out-Null}catch{$blocked=$true}
if(-not $blocked){throw 'Ruta fuera del ámbito aceptada.'}
Write-Output 'PASS ámbito admite descendientes y rechaza rutas ajenas'
$state.Anchor='C'*64;$blocked=$false
try{Test-LtScopeGuard -Guard $guard -Paths @($allowed)|Out-Null}catch{$blocked=$true}
if(-not $blocked){throw 'Ancla modificada aceptada.'}
Write-Output 'PASS cambio de identidad o ACL de ancla detiene la operación'
