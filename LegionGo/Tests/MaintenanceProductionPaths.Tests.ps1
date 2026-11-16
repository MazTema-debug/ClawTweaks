# SPDX-License-Identifier: AGPL-3.0-only
$ErrorActionPreference='Stop'
Import-Module (Join-Path $PSScriptRoot '..\scripts\MaintenanceProductionPaths.psm1') -Force
$root=Join-Path ([IO.Path]::GetTempPath()) ('LegionTweaks-path-fixture-'+[Guid]::NewGuid().ToString('N'))
$locations=@{Common=(Join-Path $root 'common');Local=(Join-Path $root 'local');ProgramFiles=(Join-Path $root 'program')}
$request=[pscustomobject]@{AuthorizesMaintenance=$false;Id=('a'*32);Direction='Return49'}
$paths=Get-LtProductionPaths -Request $request -ExpectedSid 'S-1-5-18' -Locations $locations
if($paths.AuthorizesMaintenance -or [IO.Path]::GetFileName($paths.Restore) -cne ('LegionTweaks-Z1E-rollback-'+('a'*32)) -or [IO.Path]::GetDirectoryName($paths.Staging) -ne $locations.ProgramFiles -or [IO.Path]::GetFileName($paths.Ready) -ne 'publish-ready.json'){throw 'Rutas derivadas no corresponden.'}
$request.Id='../invalid';$blocked=$false
try{Get-LtProductionPaths -Request $request -ExpectedSid 'S-1-5-18' -Locations $locations|Out-Null}catch{$blocked=$true}
if(-not $blocked){throw 'Id de ruta inválido aceptado.'}
Write-Output 'PASS rutas se derivan de identidad fija y rechazan id inválido'
$good={param($root)return [pscustomobject]@{DriveType=[IO.DriveType]::Fixed;IsReady=$true;DriveFormat='NTFS'}}
if(-not (Assert-LtProductionVolumes -Paths $paths -ReadDrive $good)){throw 'Volumen fixture válido rechazado.'}
$bad={param($root)return [pscustomobject]@{DriveType=[IO.DriveType]::Network;IsReady=$true;DriveFormat='NTFS'}};$blocked=$false
try{Assert-LtProductionVolumes -Paths $paths -ReadDrive $bad|Out-Null}catch{$blocked=$true}
if(-not $blocked){throw 'Volumen remoto aceptado.'}
Write-Output 'PASS volumen remoto bloquea ruta productiva'
