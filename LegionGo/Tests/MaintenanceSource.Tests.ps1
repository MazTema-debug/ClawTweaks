# SPDX-License-Identifier: AGPL-3.0-only
$ErrorActionPreference='Stop'
Import-Module (Join-Path $PSScriptRoot '..\scripts\MaintenanceSource.psm1') -Force
$expected=@{HelperSha256=('A'*64);QamSha256=('B'*64);Signer=('C'*40);PackageFamilyName='MazTema.LegionTweaks_wsckx05r95yk0';Publisher='CN=LegionTweaks Dev';Version='0.1.49.0'}
$observed=[pscustomobject]@{HelperSha256=$expected.HelperSha256;QamSha256=$expected.QamSha256;HelperSigner=$expected.Signer;QamSigner=$expected.Signer;PackageFamilyName=$expected.PackageFamilyName;Publisher=$expected.Publisher;Version=$expected.Version;PackageFullName='fixture';InstallLocation='X:\fixture';AuthorizesMaintenance=$false}
if(-not (Test-LtObservedSource -Observed $observed -Expected $expected)){throw 'Origen fixture rechazado.'}
Write-Output 'PASS origen observado coincide con pins separados'
$observed.QamSha256='0'*64;$blocked=$false
try{Test-LtObservedSource -Observed $observed -Expected $expected|Out-Null}catch{$blocked=$true}
if(-not $blocked){throw 'Origen incorrecto aceptado.'}
Write-Output 'PASS origen observado alterado bloquea'
Add-Type -AssemblyName System.IO.Compression,System.IO.Compression.FileSystem
$root=Join-Path ([IO.Path]::GetTempPath()) ('LegionTweaks-widget-fixture-'+[Guid]::NewGuid().ToString('N'))
$input=Join-Path $root 'input';$installed=Join-Path $root 'installed';$package=Join-Path $root 'widget.msix'
$null=New-Item -ItemType Directory -Path $input -Force
[IO.File]::WriteAllText((Join-Path $input 'AppxManifest.xml'),'fixture')
[IO.Compression.ZipFile]::CreateFromDirectory($input,$package)
[IO.Compression.ZipFile]::ExtractToDirectory($package,$installed)
if(-not (Test-LtInstalledWidgetContents -PackagePath $package -InstallLocation $installed)){throw 'Widget fixture válido rechazado.'}
Write-Output 'PASS widget instalado coincide con paquete fixture'
[IO.File]::WriteAllText((Join-Path $installed 'AppxManifest.xml'),'changed');$blocked=$false
try{Test-LtInstalledWidgetContents -PackagePath $package -InstallLocation $installed|Out-Null}catch{$blocked=$true}
if(-not $blocked){throw 'Widget fixture alterado aceptado.'}
Write-Output 'PASS widget instalado alterado bloquea'
