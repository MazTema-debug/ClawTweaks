# SPDX-License-Identifier: AGPL-3.0-only
$ErrorActionPreference='Stop'
Import-Module (Join-Path $PSScriptRoot '..\scripts\MaintenanceProtectionRouter.psm1') -Force
$root=Join-Path ([IO.Path]::GetTempPath()) ('LegionTweaks-router-fixture-'+[Guid]::NewGuid().ToString('N'))
$state=[pscustomobject]@{Calls=[Collections.Generic.List[string]]::new();Allow=$true}
$ops=@{WidgetRoot=(Join-Path $root 'package');TestScope={param($g,$p)$state.Calls.Add('scope:'+ $p);return $state.Allow}.GetNewClosure();TestUser={param($p,$s)$state.Calls.Add('user:'+ $p);return $state.Allow}.GetNewClosure()}
$router=New-LtProtectionRouter -Guard ([pscustomobject]@{}) -ExpectedSid 'S-1-5-18' -Operations $ops
if(-not (& $router (Join-Path $root 'package\Settings\settings.dat') (Join-Path $root 'backup')) -or $state.Calls.Count -ne 2 -or $state.Calls[0] -notlike 'user:*' -or $state.Calls[1] -notlike 'scope:*'){throw 'Rutas no se comprobaron por separado.'}
$state.Allow=$false;$blocked=$false
try{& $router (Join-Path $root 'package\LocalState\profiles.json')|Out-Null}catch{$blocked=$true}
if(-not $blocked){throw 'Protección denegada no bloqueó.'}
Write-Output 'PASS enrutador separa datos del usuario y respaldo protegido y bloquea rechazo'
