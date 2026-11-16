# SPDX-License-Identifier: AGPL-3.0-only
$ErrorActionPreference='Stop'
Import-Module (Join-Path $PSScriptRoot '..\scripts\MaintenanceProductionInputs.psm1') -Force
$root=Join-Path ([IO.Path]::GetTempPath()) ('LegionTweaks-input-fixture-'+[Guid]::NewGuid().ToString('N'));$null=New-Item -ItemType Directory -Path $root
$store=Join-Path $root 'packages';$null=New-Item -ItemType Directory -Path $store
function Package([string]$name){return @{Archive=(Join-Path $store ($name+'.zip'));Extracted=(Join-Path $store $name);ArchiveSha256=('A'*64);HelperSha256=('B'*64);QamSha256=('C'*64);Signer=('D'*40);WidgetRelativePath='Widget\fixture.msix'}}
$coordinator=Package 'coordinator';$sourcePackage=Package 'source';$target=Package 'target'
$target.ArchiveSha256='5225CA985580E24DB634B022E6C83805CDA5B2514846B6AFB523885BE698B39A';$target.HelperSha256='9D485EF0E4354278BB9FC496110A7536585B735EA898ED94A4CE4BD9BF438B00';$target.QamSha256='0874C28F843071381FC54A406FAA28682980480FA4ECB8B603B5088C4FA3B50C';$target.Signer='4AB5A8F9579DFDA51130307D543BEEF06EBF4DA0'
$body=@{Version=1;Direction='Return49';Id=('a'*32);Coordinator=$coordinator;SourcePackage=$sourcePackage;Target=$target;TargetVersion='0.1.49.0';Source=@{HelperSha256=$sourcePackage.HelperSha256;QamSha256=$sourcePackage.QamSha256;Signer=$sourcePackage.Signer;PackageFamilyName='MazTema.LegionTweaks_wsckx05r95yk0';Publisher='CN=LegionTweaks Dev';Version='0.1.50.0'}}
$path=Join-Path $root 'request.json';$check={param($p)return $true}
function Write-Request { [IO.File]::WriteAllText($path,(ConvertTo-Json -InputObject $body -Depth 8 -Compress),[Text.UTF8Encoding]::new($false));return (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash }
$hash=Write-Request;$request=Read-LtMaintenanceRequest -Path $path -ExpectedSha256 $hash -AssertProtection $check -PackageStore $store
if($request.Direction -ne 'Return49' -or $request.AuthorizesMaintenance -or $request.Coordinator.HelperSha256 -ne ('B'*64) -or $request.Target.HelperSha256 -ne $target.HelperSha256){throw 'Solicitud válida no identificó los tres paquetes.'}
Write-Output 'PASS solicitud identifica coordinador, origen y retorno .49'
$target.HelperSha256='0'*64;$hash=Write-Request;$blocked=$false
try{Read-LtMaintenanceRequest -Path $path -ExpectedSha256 $hash -AssertProtection $check -PackageStore $store|Out-Null}catch{$blocked=$true}
if(-not $blocked){throw 'Destino .49 alterado aceptado.'}
$target.HelperSha256='9D485EF0E4354278BB9FC496110A7536585B735EA898ED94A4CE4BD9BF438B00';$sourcePackage.Archive=Join-Path $root 'outside.zip';$hash=Write-Request;$blocked=$false
try{Read-LtMaintenanceRequest -Path $path -ExpectedSha256 $hash -AssertProtection $check -PackageStore $store|Out-Null}catch{$blocked=$true}
if(-not $blocked){throw 'Paquete fuera del almacén aceptado.'}
Write-Output 'PASS solicitud rechaza pins .49 alterados y rutas fuera del almacén'
