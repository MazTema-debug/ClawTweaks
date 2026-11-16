# SPDX-License-Identifier: AGPL-3.0-only
$ErrorActionPreference='Stop'
Import-Module (Join-Path $PSScriptRoot '..\scripts\MaintenanceImageBarrier.psm1') -Force
$root=Join-Path ([IO.Path]::GetTempPath()) ('LegionTweaks-image-fixture-'+[Guid]::NewGuid().ToString('N'))
$null=New-Item -ItemType Directory -Path $root
$source=Join-Path $root 'source.exe'
Add-Type -TypeDefinition 'public static class FixtureProgram { public static int Main() { return 0; } }' -OutputAssembly $source -OutputType ConsoleApplication
$copy=Join-Path $root 'fixture.exe';Copy-Item -LiteralPath $source -Destination $copy
$hash=(Get-FileHash -LiteralPath $copy -Algorithm SHA256).Hash
$blocked=$false
try{Disable-LtImageForPublication -Image $copy -ExpectedSha256 ('0'*64) -AssertScope {param($p)return $true}|Out-Null}catch{$blocked=$true}
if(-not $blocked -or (Get-FileHash -LiteralPath $copy -Algorithm SHA256).Hash -ne $hash){throw 'El pin incorrecto modificó la imagen.'}
Write-Output 'PASS pin incorrecto no modifica imagen sintética'
$result=Disable-LtImageForPublication -Image $copy -ExpectedSha256 $hash -AssertScope {param($p)return $true}
$bytes=[IO.File]::ReadAllBytes($copy)
if($result.AuthorizesMaintenance -or $bytes[0] -ne 0 -or $bytes[1] -ne 90){throw 'La barrera de imagen sintética no se confirmó.'}
Write-Output 'PASS imagen sintética invalidada sin autorizar mantenimiento'
