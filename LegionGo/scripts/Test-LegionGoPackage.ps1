# SPDX-License-Identifier: AGPL-3.0-only
[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$PackageRoot,[string]$SourceRoot,[switch]$Unsigned)
$ErrorActionPreference='Stop'
if($PSVersionTable.PSEdition -ne 'Desktop'){throw 'Package verification requires Windows PowerShell 5.1.'}
Add-Type -AssemblyName System.IO.Compression.FileSystem
$package=[IO.Path]::GetFullPath($PackageRoot).TrimEnd('\')+'\'
$inventory=Get-Content -LiteralPath (Join-Path $package 'Build-Inventory.json') -Raw | ConvertFrom-Json
if($inventory.SchemaVersion -ne 1 -or $inventory.Revision -cne '0.1.50-maintenance-v1' -or $inventory.Version -cne '0.1.50.0'){throw 'Unexpected package inventory identity.'}
foreach($relative in $inventory.RequiredPackageFiles){
    $file=[IO.Path]::GetFullPath((Join-Path $package $relative))
    if(-not $file.StartsWith($package,[StringComparison]::OrdinalIgnoreCase) -or -not(Test-Path -LiteralPath $file -PathType Leaf)){throw ('Required package file missing: '+$relative)}
}
$modules=@(Get-ChildItem -LiteralPath $package -File -Filter 'Maintenance*.psm1' | Select-Object -ExpandProperty Name)
if(@(Compare-Object @($inventory.MaintenanceModules) $modules).Count){throw 'Packaged maintenance module inventory differs.'}
foreach($name in @('LegionTweaks-Maintenance-v1.ps1')+$modules){
    $text=Get-Content -LiteralPath (Join-Path $package $name) -Raw
    foreach($reference in [regex]::Matches($text,'[''"](Maintenance[A-Za-z0-9]+)(?:\.psm1)?[''"]')){
        if(($reference.Groups[1].Value+'.psm1') -cnotin $modules){throw ('Missing maintenance dependency: '+$reference.Groups[1].Value)}
    }
    if($SourceRoot -and (Get-FileHash -LiteralPath (Join-Path $package $name)).Hash -cne (Get-FileHash -LiteralPath (Join-Path $SourceRoot ('scripts\'+$name))).Hash){throw ('Packaged source differs: '+$name)}
}
if($SourceRoot){
    foreach($name in @('Install-LegionGo.ps1','Uninstall-LegionGo.ps1','Maintenance-v1.md','Build-Inventory.json','Test-LegionGoPackage.ps1')){
        if((Get-FileHash -LiteralPath (Join-Path $package $name)).Hash -cne (Get-FileHash -LiteralPath (Join-Path $SourceRoot ('scripts\'+$name))).Hash){throw ('Packaged source differs: '+$name)}
    }
}
$seen=@{}
foreach($line in Get-Content -LiteralPath (Join-Path $package 'SHA256SUMS.txt')){
    if($line -cnotmatch '^([A-F0-9]{64})  (.+)$'){throw 'Invalid package checksum record.'}
    $hash=$Matches[1];$relative=$Matches[2];$file=[IO.Path]::GetFullPath((Join-Path $package $relative))
    if(-not $file.StartsWith($package,[StringComparison]::OrdinalIgnoreCase) -or $seen.ContainsKey($file)){throw 'Invalid checksum path.'}
    $seen[$file]=$true
    if((Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash -cne $hash){throw ('Package checksum mismatch: '+$relative)}
}
$all=@(Get-ChildItem -LiteralPath $package -Recurse -File | Where-Object Name -ne 'SHA256SUMS.txt')
if($seen.Count -ne $all.Count){throw 'Package checksum inventory has missing or extra files.'}
$widgets=@(Get-ChildItem -LiteralPath (Join-Path $package 'Widget') -Recurse -File | Where-Object {$_.Extension -in '.appx','.msix' -and $_.FullName -notmatch '\\Dependencies\\'})
if($widgets.Count -ne 1){throw 'Expected exactly one widget package.'}
function Read-PackageManifest([string]$Path){
    $zip=[IO.Compression.ZipFile]::OpenRead($Path)
    try{$entry=$zip.GetEntry('AppxManifest.xml');if(-not $entry){throw 'Package manifest absent.'};$reader=[IO.StreamReader]::new($entry.Open());try{return [xml]$reader.ReadToEnd()}finally{$reader.Dispose()}}finally{$zip.Dispose()}
}
$manifest=Read-PackageManifest $widgets[0].FullName
if($manifest.Package.Identity.Version -cne $inventory.Version -or $manifest.Package.Identity.ProcessorArchitecture -cne 'x64' -or $manifest.Package.Identity.Name -cne 'MazTema.LegionTweaks' -or $manifest.Package.Identity.Publisher -cne 'CN=LegionTweaks Dev'){throw 'Widget identity differs.'}
$dependencies=@(Get-ChildItem -LiteralPath (Split-Path -Parent $widgets[0].FullName) -Recurse -File -Filter '*.appx' | Where-Object {$_.FullName -match '\\Dependencies\\x64\\'})
$identities=@(foreach($dep in $dependencies){(Read-PackageManifest $dep.FullName).Package.Identity})
foreach($required in $manifest.Package.Dependencies.PackageDependency){
    $satisfied=@($identities | Where-Object {$_.Name -ceq $required.Name -and $_.Publisher -ceq $required.Publisher -and [version]$_.Version -ge [version]$required.MinVersion -and $_.ProcessorArchitecture -in 'x64','neutral'})
    if(-not $satisfied.Count){throw ('Missing x64 widget dependency: '+$required.Name)}
}
foreach($relative in @('Helper\LegionTweaks.Helper.exe','Helper\Qam\LegionTweaks.QamProbe.exe')){
    $assembly=[Reflection.Assembly]::ReflectionOnlyLoadFrom((Join-Path $package $relative))
    if($assembly.GetName().Version.ToString() -cne $inventory.Version -or $assembly.GetName().ProcessorArchitecture -ne 'Amd64'){throw 'Host version or architecture differs.'}
    $rules=$assembly.GetType('LegionTweaks.FpsY3TrialRules',$true);$engine=$assembly.GetType('LegionTweaks.ControllerRemapEngine',$true)
    if($rules.GetField('MaxPulseAgeMilliseconds').GetRawConstantValue() -ne 1000 -or $engine.GetField('FreshMilliseconds').GetRawConstantValue() -ne 250 -or $rules.GetField('DurationMilliseconds').GetRawConstantValue() -ne 120000){throw 'Y3 window constants differ.'}
    $launcher=$assembly.GetType('LegionTweaks.QamUserSession',$true)
    if($launcher.GetField('PrimaryTokenAccess',[Reflection.BindingFlags]'NonPublic,Static').GetRawConstantValue() -ne 0x018b){throw 'QAM token correction absent.'}
    if($relative -eq 'Helper\LegionTweaks.Helper.exe'){
        foreach($type in @('FpsY3JournalInspector','FpsY3Admission','FpsY3MaintenanceSession','FpsY3LegacyInstanceFence')){if(-not $assembly.GetType('LegionTweaks.'+$type,$false)){throw ('Compiled maintenance type absent: '+$type)}}
    }
}
$signatures=@()
if(-not $Unsigned){
    $cert=[Security.Cryptography.X509Certificates.X509Certificate2]::new((Join-Path $package 'LegionTweaks.cer'))
    if($cert.HasPrivateKey -or $cert.Subject -cne 'CN=LegionTweaks Dev'){throw 'Unexpected public signing certificate.'}
    foreach($file in @((Join-Path $package 'Helper\LegionTweaks.Helper.exe'),(Join-Path $package 'Helper\Qam\LegionTweaks.QamProbe.exe'),$widgets[0].FullName)){
        $signature=Get-AuthenticodeSignature -LiteralPath $file
        if(-not $signature.SignerCertificate -or $signature.SignerCertificate.Thumbprint -cne $cert.Thumbprint -or $signature.Status -notin 'Valid','UnknownError','NotTrusted'){throw ('Invalid package signature: '+$file)}
        $chain=[Security.Cryptography.X509Certificates.X509Chain]::new();$chain.ChainPolicy.RevocationMode='NoCheck'
        try{$null=$chain.Build($signature.SignerCertificate);$bad=@($chain.ChainStatus | Where-Object {$_.Status -notin 'NoError','UntrustedRoot'});if($bad.Count){throw 'Signature chain has errors other than an untrusted development root.'}}finally{$chain.Dispose()}
        $signatures+=@{File=$file.Substring($package.Length);Status=$signature.Status.ToString();Signer=$cert.Thumbprint}
    }
}
[pscustomobject]@{Revision=$inventory.Revision;Version=$inventory.Version;RequiredFiles=$inventory.RequiredPackageFiles.Count;Modules=$modules.Count;InternalHashes=$seen.Count;Files=$all.Count+1;WidgetDependencies=$dependencies.Count;Signatures=$signatures;Y3PulseMs=1000;Y3FreshnessMs=250;QamToken='0x018b';ProductEntryPointExecuted=$false;Status='PASS'} | ConvertTo-Json -Depth 5
