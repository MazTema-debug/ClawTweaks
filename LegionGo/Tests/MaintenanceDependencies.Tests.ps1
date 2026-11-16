# SPDX-License-Identifier: AGPL-3.0-only
$ErrorActionPreference='Stop'
Import-Module (Join-Path $PSScriptRoot '..\scripts\MaintenanceWindowsEffects.psm1') -Force
Add-Type -AssemblyName System.IO.Compression,System.IO.Compression.FileSystem
$root=Join-Path ([IO.Path]::GetTempPath()) ('LegionTweaks-dependency-fixture-'+[Guid]::NewGuid().ToString('N'))
$null=New-Item -ItemType Directory -Path $root
$file=Join-Path $root 'Framework.appx';$zip=[IO.Compression.ZipFile]::Open($file,[IO.Compression.ZipArchiveMode]::Create)
try{$entry=$zip.CreateEntry('AppxManifest.xml');$writer=[IO.StreamWriter]::new($entry.Open());try{$writer.Write('<Package><Identity Name="Microsoft.Test.Framework" Publisher="CN=Microsoft Test" Version="2.2.3.0" ProcessorArchitecture="x64"/><Properties><Framework>true</Framework></Properties></Package>')}finally{$writer.Dispose()}}finally{$zip.Dispose()}
$state=[pscustomobject]@{Installed=@()};$get={param($name)if($name -ne 'Microsoft.Test.Framework'){throw 'Nombre inesperado.'};return $state.Installed}.GetNewClosure()
function Assert([bool]$ok,[string]$message){if(-not $ok){throw $message}}
Assert (@(Select-LtMissingDependencies -Paths @($file) -GetFrameworks $get).Count -eq 1) 'Dependencia faltante no seleccionada.'
Write-Output 'PASS dependencia ausente se selecciona'
$state.Installed=@([pscustomobject]@{IsFramework=$true;Status='Ok';Publisher='CN=Microsoft Test';Architecture='X64';Version='2.2.3.0'})
Assert (@(Select-LtMissingDependencies -Paths @($file) -GetFrameworks $get).Count -eq 0) 'Framework válido se reinstalaría.'
Write-Output 'PASS framework compatible no se reinstala'
$state.Installed=@([pscustomobject]@{IsFramework=$true;Status='Ok';Publisher='CN=Other';Architecture='X64';Version='2.2.3.0'})
Assert (@(Select-LtMissingDependencies -Paths @($file) -GetFrameworks $get).Count -eq 1) 'Publisher incorrecto aceptado.'
Write-Output 'PASS framework de otro publisher se rechaza'
