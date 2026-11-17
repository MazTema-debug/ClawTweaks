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
$null=New-Item -ItemType Directory -Path $root -Force
$types='<Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Default Extension="xml" ContentType="text/xml"/><Default Extension="bin" ContentType="application/octet-stream"/><Default Extension="p7x" ContentType="application/octet-stream"/><Default Extension="cat" ContentType="application/octet-stream"/><Override PartName="/AppxManifest.xml" ContentType="application/vnd.ms-appx.manifest+xml"/></Types>'
function New-WidgetFixture([string]$label,[string]$metadata=$types){
    $dir=Join-Path $root $label;$installed=Join-Path $dir 'installed';$package=Join-Path $dir 'widget.msix'
    $null=New-Item -ItemType Directory -Path (Join-Path $installed 'AppxMetadata') -Force
    $files=[ordered]@{'AppxManifest.xml'='manifest';'AppxBlockMap.xml'='blockmap';'AppxSignature.p7x'='signature';'AppxMetadata/CodeIntegrity.cat'='integrity';'payload.bin'='payload'}
    $zip=[IO.Compression.ZipFile]::Open($package,[IO.Compression.ZipArchiveMode]::Create)
    try{
        $entries=[ordered]@{'[Content_Types].xml'=$metadata};foreach($name in $files.Keys){$entries[$name]=$files[$name]}
        foreach($name in $entries.Keys){$entry=$zip.CreateEntry($name);$writer=[IO.StreamWriter]::new($entry.Open(),[Text.UTF8Encoding]::new($false));try{$writer.Write([string]$entries[$name])}finally{$writer.Dispose()}}
    }finally{$zip.Dispose()}
    foreach($name in $files.Keys){[IO.File]::WriteAllText((Join-Path $installed $name),[string]$files[$name],[Text.UTF8Encoding]::new($false))}
    return [pscustomobject]@{Installed=$installed;Package=$package}
}
function Assert-WidgetRejected($fixture,[string]$label){$blocked=$false;try{Test-LtInstalledWidgetContents -PackagePath $fixture.Package -InstallLocation $fixture.Installed|Out-Null}catch{$blocked=$true};if(-not $blocked){throw ('Widget aceptó '+$label)};Write-Output ('PASS widget rechaza '+$label)}
$f=New-WidgetFixture 'ok'
if(-not (Test-LtInstalledWidgetContents -PackagePath $f.Package -InstallLocation $f.Installed)){throw 'Widget fixture válido rechazado.'}
Write-Output 'PASS widget compara payload y footprint con OPC solo en el contenedor'
foreach($name in @('payload.bin','AppxManifest.xml','AppxBlockMap.xml','AppxSignature.p7x','AppxMetadata/CodeIntegrity.cat')){
    $f=New-WidgetFixture ('altered-'+[guid]::NewGuid().ToString('N'));[IO.File]::WriteAllText((Join-Path $f.Installed $name),'changed');Assert-WidgetRejected $f ('contenido alterado '+$name)
    $f=New-WidgetFixture ('missing-'+[guid]::NewGuid().ToString('N'));Remove-Item -LiteralPath (Join-Path $f.Installed $name);Assert-WidgetRejected $f ('contenido ausente '+$name)
}
$f=New-WidgetFixture 'bad-xml' '<!DOCTYPE Types [<!ENTITY foo "evil">]><Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">&foo;</Types>';Assert-WidgetRejected $f 'DTD en metadatos OPC'
$f=New-WidgetFixture 'bad-ref' '<Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Default Extension="xml" ContentType="text/xml"/><Override PartName="/missing.bin" ContentType="application/octet-stream"/></Types>';Assert-WidgetRejected $f 'referencia OPC ausente'
$f=New-WidgetFixture 'bad-ref-escape' '<Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Override PartName="/../escape.bin" ContentType="application/octet-stream"/></Types>';Assert-WidgetRejected $f 'referencia OPC fuera de raíz'
$f=New-WidgetFixture 'missing-type' '<Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Default Extension="xml" ContentType="text/xml"/></Types>';Assert-WidgetRejected $f 'tipo OPC de payload ausente'
$f=New-WidgetFixture 'bad-kind' '<Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Unexpected ContentType="application/octet-stream"/></Types>';Assert-WidgetRejected $f 'metadato OPC inesperado'
$f=New-WidgetFixture 'duplicate-zip';$zip=[IO.Compression.ZipFile]::Open($f.Package,[IO.Compression.ZipArchiveMode]::Update);try{$e=$zip.CreateEntry('[content_types].xml');$w=[IO.StreamWriter]::new($e.Open());try{$w.Write('duplicate')}finally{$w.Dispose()}}finally{$zip.Dispose()};Assert-WidgetRejected $f 'entrada OPC duplicada'
$f=New-WidgetFixture 'escape';$zip=[IO.Compression.ZipFile]::Open($f.Package,[IO.Compression.ZipArchiveMode]::Update);try{$e=$zip.CreateEntry('../escape.bin');$w=[IO.StreamWriter]::new($e.Open());try{$w.Write('escape')}finally{$w.Dispose()}}finally{$zip.Dispose()};Assert-WidgetRejected $f 'ruta fuera de raíz'
$f=New-WidgetFixture 'unexpected-file';$zip=[IO.Compression.ZipFile]::Open($f.Package,[IO.Compression.ZipArchiveMode]::Update);try{$e=$zip.CreateEntry('AppxMetadata/unexpected.cat');$w=[IO.StreamWriter]::new($e.Open());try{$w.Write('unexpected')}finally{$w.Dispose()}}finally{$zip.Dispose()};Assert-WidgetRejected $f 'footprint adicional ausente'
$f=New-WidgetFixture 'unexpected-file-present';$zip=[IO.Compression.ZipFile]::Open($f.Package,[IO.Compression.ZipArchiveMode]::Update);try{$e=$zip.CreateEntry('AppxMetadata/unexpected.cat');$w=[IO.StreamWriter]::new($e.Open());try{$w.Write('unexpected')}finally{$w.Dispose()}}finally{$zip.Dispose()};[IO.File]::WriteAllText((Join-Path $f.Installed 'AppxMetadata/unexpected.cat'),'unexpected');Assert-WidgetRejected $f 'footprint desconocido incluso presente'
$f=New-WidgetFixture 'no-opc';$zip=[IO.Compression.ZipFile]::Open($f.Package,[IO.Compression.ZipArchiveMode]::Update);try{$zip.GetEntry('[Content_Types].xml').Delete()}finally{$zip.Dispose()};Assert-WidgetRejected $f 'tipos OPC ausentes del paquete'
$f=New-WidgetFixture 'no-blockmap';$zip=[IO.Compression.ZipFile]::Open($f.Package,[IO.Compression.ZipArchiveMode]::Update);try{$zip.GetEntry('AppxBlockMap.xml').Delete()}finally{$zip.Dispose()};Assert-WidgetRejected $f 'blockmap ausente del paquete'
foreach($required in @('AppxManifest.xml','AppxSignature.p7x')){$f=New-WidgetFixture ('no-package-'+[guid]::NewGuid().ToString('N'));$zip=[IO.Compression.ZipFile]::Open($f.Package,[IO.Compression.ZipArchiveMode]::Update);try{$zip.GetEntry($required).Delete()}finally{$zip.Dispose()};Assert-WidgetRejected $f ('entrada requerida ausente del paquete '+$required)}
$f=New-WidgetFixture 'junction';$source=Join-Path (Split-Path -Parent $f.Installed) 'real-metadata';$original=Join-Path $f.Installed 'AppxMetadata';[IO.File]::Delete((Join-Path $original 'CodeIntegrity.cat'));[IO.Directory]::Delete($original,$false);$null=New-Item -ItemType Directory -Path $source;[IO.File]::WriteAllText((Join-Path $source 'CodeIntegrity.cat'),'integrity');$null=New-Item -ItemType Junction -Path $original -Target $source;Assert-WidgetRejected $f 'directorio intermedio reparse'
$f=New-WidgetFixture 'root-junction';$link=Join-Path (Split-Path -Parent $f.Installed) 'linked-installed';$null=New-Item -ItemType Junction -Path $link -Target $f.Installed;Assert-WidgetRejected ([pscustomobject]@{Installed=$link;Package=$f.Package}) 'raíz reparse'
