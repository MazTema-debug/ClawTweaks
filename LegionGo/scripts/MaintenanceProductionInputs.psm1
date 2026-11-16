# SPDX-License-Identifier: AGPL-3.0-only
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
function Convert-LtPackageInput($Package,[string]$Store){
    foreach($key in @('Archive','Extracted','ArchiveSha256','HelperSha256','QamSha256','Signer','WidgetRelativePath')){if(-not $Package.PSObject.Properties[$key] -or -not $Package.$key){throw "Falta pin de paquete $key."}}
    foreach($key in @('ArchiveSha256','HelperSha256','QamSha256')){if([string]$Package.$key -cnotmatch '^[A-F0-9]{64}$'){throw 'Hash de paquete inválido.'}}
    if([string]$Package.Signer -cnotmatch '^[A-F0-9]{40}$'){throw 'Firmante de paquete inválido.'}
    $archive=[IO.Path]::GetFullPath([string]$Package.Archive);$root=[IO.Path]::GetFullPath([string]$Package.Extracted).TrimEnd('\')
    if(-not [IO.Path]::IsPathRooted([string]$Package.Archive) -or -not [IO.Path]::IsPathRooted([string]$Package.Extracted) -or -not $archive.StartsWith($Store+'\',[StringComparison]::OrdinalIgnoreCase) -or -not $root.StartsWith($Store+'\',[StringComparison]::OrdinalIgnoreCase)){throw 'Paquete fuera del almacén previsto.'}
    $relative=[string]$Package.WidgetRelativePath
    if(-not $relative -or [IO.Path]::IsPathRooted($relative) -or $relative.Contains(':') -or $relative -match '(^|[\\/])\.\.?($|[\\/])' -or $relative -notmatch '(?i)^Widget[\\/].+\.msix$'){throw 'Ruta de widget inválida.'}
    $widget=[IO.Path]::GetFullPath((Join-Path $root $relative))
    if(-not $widget.StartsWith((Join-Path $root 'Widget')+'\',[StringComparison]::OrdinalIgnoreCase)){throw 'Widget fuera del paquete.'}
    return @{Archive=$archive;Extracted=$root;ArchiveSha256=[string]$Package.ArchiveSha256;HelperPath=(Join-Path $root 'Helper\LegionTweaks.Helper.exe');QamPath=(Join-Path $root 'Helper\Qam\LegionTweaks.QamProbe.exe');WidgetPath=$widget;
        HelperSha256=[string]$Package.HelperSha256;QamSha256=[string]$Package.QamSha256;Signer=[string]$Package.Signer}
}
function Read-LtMaintenanceRequest {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Path,[Parameter(Mandatory)][ValidatePattern('^[A-F0-9]{64}$')][string]$ExpectedSha256,
          [Parameter(Mandatory)][scriptblock]$AssertProtection,[string]$PackageStore)
    if(-not $PackageStore){$PackageStore=Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::CommonApplicationData)) 'LegionTweaks\maintenance-v1\packages'}
    if(-not [IO.Path]::IsPathRooted($PackageStore)){throw 'Almacén no absoluto.'}
    $store=[IO.Path]::GetFullPath($PackageStore).TrimEnd('\');$full=[IO.Path]::GetFullPath($Path)
    $check=@(& $AssertProtection $full);if($check.Count -ne 1 -or $check[0] -isnot [bool] -or -not $check[0]){throw 'Solicitud no protegida.'}
    $item=Get-Item -LiteralPath $full -Force -ErrorAction Stop
    if($item.PSIsContainer -or ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)){throw 'Solicitud no es archivo normal.'}
    $stream=[IO.FileStream]::new($full,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read)
    try{
        if($stream.Length -le 0 -or $stream.Length -gt 65536){throw 'Solicitud fuera de tamaño.'}
        $sha=[Security.Cryptography.SHA256]::Create();try{$hash=([BitConverter]::ToString($sha.ComputeHash($stream))).Replace('-','')}finally{$sha.Dispose()}
        if($hash -ne $ExpectedSha256){throw 'Hash de solicitud no coincide.'}
        $stream.Position=0;$reader=[IO.StreamReader]::new($stream,[Text.UTF8Encoding]::new($false,$true),$false)
        try{$request=ConvertFrom-Json -InputObject $reader.ReadToEnd() -ErrorAction Stop}finally{$reader.Dispose()}
    }finally{$stream.Dispose()}
    foreach($name in @('Version','Direction','Id','Coordinator','SourcePackage','Target','Source','TargetVersion')){if(-not $request.PSObject.Properties[$name] -or $null -eq $request.$name){throw "Falta dato de solicitud $name."}}
    if($request.Version -ne 1 -or $request.Direction -cnotin @('Update','Return49') -or [string]$request.Id -cnotmatch '^[a-f0-9]{32}$'){throw 'Identidad de solicitud inválida.'}
    $coordinator=Convert-LtPackageInput $request.Coordinator $store;$target=Convert-LtPackageInput $request.Target $store;$sourcePackage=Convert-LtPackageInput $request.SourcePackage $store
    $source=$request.Source
    foreach($key in @('HelperSha256','QamSha256','Signer','PackageFamilyName','Publisher','Version')){if(-not $source.PSObject.Properties[$key] -or -not $source.$key){throw "Falta pin de origen $key."}}
    if($source.HelperSha256 -cne $sourcePackage.HelperSha256 -or $source.QamSha256 -cne $sourcePackage.QamSha256 -or $source.Signer -cne $sourcePackage.Signer -or $source.PackageFamilyName -cne 'MazTema.LegionTweaks_wsckx05r95yk0' -or $source.Publisher -cne 'CN=LegionTweaks Dev'){throw 'Origen y paquete de origen no coinciden.'}
    try{$null=[version]$source.Version;$null=[version]$request.TargetVersion}catch{throw 'Versión de paquete inválida.'}
    if($request.Direction -ceq 'Return49' -and ($target.ArchiveSha256 -cne '5225CA985580E24DB634B022E6C83805CDA5B2514846B6AFB523885BE698B39A' -or $target.HelperSha256 -cne '9D485EF0E4354278BB9FC496110A7536585B735EA898ED94A4CE4BD9BF438B00' -or $target.QamSha256 -cne '0874C28F843071381FC54A406FAA28682980480FA4ECB8B603B5088C4FA3B50C' -or $target.Signer -cne '4AB5A8F9579DFDA51130307D543BEEF06EBF4DA0' -or [version]$request.TargetVersion -ne [version]'0.1.49.0')){throw 'El destino no es .49 exacta.'}
    $check=@(& $AssertProtection $full);if($check.Count -ne 1 -or $check[0] -isnot [bool] -or -not $check[0]){throw 'Protección de solicitud cambió.'}
    return [pscustomobject]@{Version=1;Direction=[string]$request.Direction;Id=[string]$request.Id;Coordinator=$coordinator;SourcePackage=$sourcePackage;Target=$target;
        Source=@{HelperSha256=[string]$source.HelperSha256;QamSha256=[string]$source.QamSha256;Signer=[string]$source.Signer;PackageFamilyName=[string]$source.PackageFamilyName;Publisher=[string]$source.Publisher;Version=[string]$source.Version};TargetVersion=[string]$request.TargetVersion;RequestSha256=$hash;AuthorizesMaintenance=$false}
}
Export-ModuleMember -Function Read-LtMaintenanceRequest
