# SPDX-License-Identifier: AGPL-3.0-only
$ErrorActionPreference='Stop'
Import-Module (Join-Path $PSScriptRoot '..\scripts\MaintenancePackage.psm1') -Force
Add-Type -AssemblyName System.IO.Compression,System.IO.Compression.FileSystem
$root=Join-Path ([IO.Path]::GetTempPath()) ('LegionTweaks-widget-id-'+[Guid]::NewGuid().ToString('N'));$null=New-Item -ItemType Directory -Path $root
function Make-Widget([string]$name,[string]$manifest){
    $path=Join-Path $root ($name+'.msix');$zip=[IO.Compression.ZipFile]::Open($path,[IO.Compression.ZipArchiveMode]::Create)
    try{$entry=$zip.CreateEntry('AppxManifest.xml');$writer=[IO.StreamWriter]::new($entry.Open());try{$writer.Write($manifest)}finally{$writer.Dispose()}}finally{$zip.Dispose()};return $path
}
$valid='<Package><Identity Name="MazTema.LegionTweaks" Publisher="CN=LegionTweaks Dev" ProcessorArchitecture="x64" Version="0.1.49.0"/></Package>'
$result=Get-LtWidgetPackageIdentity -Path (Make-Widget 'good' $valid)
if($result.Version -cne '0.1.49.0' -or $result.AuthorizesMaintenance){throw 'Identidad de widget válida rechazada.'}
$bad=$valid.Replace('CN=LegionTweaks Dev','CN=Other');$blocked=$false
try{Get-LtWidgetPackageIdentity -Path (Make-Widget 'wrong' $bad)|Out-Null}catch{$blocked=$true}
if(-not $blocked){throw 'Publisher de widget incorrecto aceptado.'}
$bad='<!DOCTYPE Package [<!ENTITY x "unsafe">]><Package><Identity Name="&x;"/></Package>';$blocked=$false
try{Get-LtWidgetPackageIdentity -Path (Make-Widget 'dtd' $bad)|Out-Null}catch{$blocked=$true}
if(-not $blocked){throw 'DTD en manifiesto fue aceptado.'}
Write-Output 'PASS identidad del widget y versión se verifican y se rechazan publisher ajeno y DTD'
