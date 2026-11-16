# SPDX-License-Identifier: AGPL-3.0-only
# Private core runs only against per-test directories; no ProgramData access.
$ErrorActionPreference='Stop'
Import-Module (Join-Path $PSScriptRoot '..\scripts\MaintenanceStartup.psm1') -Force
$module=Get-Module MaintenanceStartup
$dir=Join-Path ([IO.Path]::GetTempPath()) ('LegionTweaks-startup-fixture-'+[Guid]::NewGuid().ToString('N'))
$null=New-Item -ItemType Directory -Path $dir
$check={param($p)return $true}
$create=& $module {param($d,$c) Set-LtStartupSuppressionCore -Directory $d -AssertProtection $c} $dir $check
if($create.AlreadyPresent -or $create.AuthorizesMaintenance -or [IO.File]::ReadAllText((Join-Path $dir 'controller-startup-suppress-once.json')) -cne '{"Skip":true,"Version":1}'){throw 'Supresión nueva incorrecta.'}
Write-Output 'PASS supresión de fixture nueva coincide con contrato .49'
$again=& $module {param($d,$c) Set-LtStartupSuppressionCore -Directory $d -AssertProtection $c} $dir $check
if(-not $again.AlreadyPresent){throw 'Supresión existente no se conservó.'}
Write-Output 'PASS supresión válida existente conservada'
$path=Join-Path $dir 'controller-startup-suppress-once.json';[IO.File]::WriteAllText($path,'corrupt')
$blocked=$false
try{& $module {param($d,$c) Set-LtStartupSuppressionCore -Directory $d -AssertProtection $c} $dir $check|Out-Null}catch{$blocked=$true}
if(-not $blocked -or [IO.File]::ReadAllText($path) -cne 'corrupt'){throw 'Supresión inválida se sobrescribió.'}
Write-Output 'PASS supresión existente inválida bloquea y se conserva'
$dir3=Join-Path ([IO.Path]::GetTempPath()) ('LegionTweaks-startup-fixture-'+[Guid]::NewGuid().ToString('N'))
$null=New-Item -ItemType Directory -Path (Join-Path $dir3 'controller-startup-suppress-once.json') -Force
$blocked=$false
try{& $module {param($d,$c) Set-LtStartupSuppressionCore -Directory $d -AssertProtection $c} $dir3 $check|Out-Null}catch{$blocked=$true}
if(-not $blocked -or -not (Test-Path -LiteralPath (Join-Path $dir3 'controller-startup-suppress-once.json') -PathType Container)){throw 'Ruta de supresión que es directorio no bloqueó.'}
Write-Output 'PASS supresión rechaza una ruta que no es archivo normal'
$dir2=Join-Path ([IO.Path]::GetTempPath()) ('LegionTweaks-startup-fixture-'+[Guid]::NewGuid().ToString('N'))
$null=New-Item -ItemType Directory -Path $dir2
$fixtureOperation={param($sid,$c)& $module {param($d,$check) Set-LtStartupSuppressionCore -Directory $d -AssertProtection $check} $dir2 $c}.GetNewClosure()
$adapter=New-LtStartupAdapter -ExpectedSid 'S-1-5-21-1-2-3-1001' -AssertProtection $check -Operation $fixtureOperation
$ctx=[ordered]@{}
if(-not (& $adapter.ApplyStartupSuppression $ctx) -or $ctx.SuppressionWasPresent){throw 'Adaptador de supresión no verificó el fixture.'}
Write-Output 'PASS adaptador de supresión usa el componente real con fixture'
