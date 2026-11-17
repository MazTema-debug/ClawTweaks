# SPDX-License-Identifier: AGPL-3.0-only
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
Import-Module (Join-Path $PSScriptRoot 'MaintenanceBackup.psm1') -ErrorAction Stop

# Read-only identification of the currently installed source. The caller must
# verify scope/ACL/quiescence and must not treat this snapshot as a reservation.
function Get-LtObservedSource {
    [CmdletBinding()]
    param()
    $root=Join-Path $env:ProgramFiles 'LegionTweaks-Z1E'
    $paths=@((Join-Path $root 'LegionTweaks.Helper.exe'),(Join-Path $root 'Qam\LegionTweaks.QamProbe.exe'))
    $components=@(foreach($path in $paths){
        $item=Get-Item -LiteralPath $path -Force -ErrorAction Stop
        if($item.PSIsContainer -or ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)){throw 'Componente instalado inseguro.'}
        $stream=[IO.FileStream]::new($path,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read)
        try{
            $identity=[LegionTweaks.MaintenanceNative.FileIdentity]::new()
            if(-not [LegionTweaks.MaintenanceNative.BackupApi]::GetFileInformationByHandle($stream.SafeFileHandle,[ref]$identity) -or $identity.Links -ne 1 -or ($identity.IndexHigh -eq 0 -and $identity.IndexLow -eq 0)){throw 'Identidad del componente instalado incierta.'}
            $sha=[Security.Cryptography.SHA256]::Create();try{$hash=([BitConverter]::ToString($sha.ComputeHash($stream))).Replace('-','')}finally{$sha.Dispose()}
            $signature=Get-AuthenticodeSignature -LiteralPath $path
            if($signature.Status -ne 'Valid' -or -not $signature.SignerCertificate){throw 'Firma instalada no válida.'}
            [pscustomobject]@{Sha256=$hash;Signer=$signature.SignerCertificate.Thumbprint}
        }finally{$stream.Dispose()}
    })
    $packages=@(Get-AppxPackage -Name 'MazTema.LegionTweaks' -ErrorAction Stop)
    if($packages.Count -ne 1 -or [string]$packages[0].Status -ne 'Ok' -or [string]$packages[0].Architecture -ne 'X64'){throw 'Paquete instalado ausente, ambiguo o incorrecto.'}
    return [pscustomobject]@{HelperSha256=$components[0].Sha256;QamSha256=$components[1].Sha256;
        HelperSigner=$components[0].Signer;QamSigner=$components[1].Signer;
        PackageFamilyName=[string]$packages[0].PackageFamilyName;Publisher=[string]$packages[0].Publisher;
        Version=[string]$packages[0].Version;PackageFullName=[string]$packages[0].PackageFullName;InstallLocation=[string]$packages[0].InstallLocation;AuthorizesMaintenance=$false}
}
function Test-LtObservedSource {
    [CmdletBinding()]
    param([Parameter(Mandatory)]$Observed,[Parameter(Mandatory)][System.Collections.IDictionary]$Expected)
    foreach($key in @('HelperSha256','QamSha256','Signer','PackageFamilyName','Publisher','Version')){if(-not $Expected.Contains($key) -or -not $Expected[$key]){throw "Falta pin del origen $key."}}
    if($Expected.HelperSha256 -notmatch '^[0-9A-Fa-f]{64}$' -or $Expected.QamSha256 -notmatch '^[0-9A-Fa-f]{64}$' -or $Expected.Signer -notmatch '^[0-9A-Fa-f]{40}$' -or $Expected.PackageFamilyName -ne 'MazTema.LegionTweaks_wsckx05r95yk0' -or $Expected.Publisher -ne 'CN=LegionTweaks Dev'){throw 'Pins de origen inválidos.'}
    if($Observed.AuthorizesMaintenance -ne $false -or $Observed.HelperSha256 -ne $Expected.HelperSha256 -or $Observed.QamSha256 -ne $Expected.QamSha256 -or $Observed.HelperSigner -ne $Expected.Signer -or $Observed.QamSigner -ne $Expected.Signer -or $Observed.PackageFamilyName -ne $Expected.PackageFamilyName -or $Observed.Publisher -ne $Expected.Publisher -or [version]$Observed.Version -ne [version]$Expected.Version -or -not $Observed.PackageFullName -or -not $Observed.InstallLocation){throw 'Origen instalado no coincide con los pins.'}
    return $true
}
function Test-LtInstalledWidgetContents {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$PackagePath,[Parameter(Mandatory)][string]$InstallLocation)
    Add-Type -AssemblyName System.IO.Compression,System.IO.Compression.FileSystem
    if(-not [IO.Path]::IsPathRooted($PackagePath) -or -not [IO.Path]::IsPathRooted($InstallLocation)){throw 'Rutas de widget no absolutas.'}
    $package=[IO.Path]::GetFullPath($PackagePath);$root=[IO.Path]::GetFullPath($InstallLocation).TrimEnd('\')
    $packageItem=@(Get-Item -LiteralPath $package -Force -ErrorAction Stop);$rootItem=@(Get-Item -LiteralPath $root -Force -ErrorAction Stop)
    if($packageItem.Count -ne 1 -or $null -eq $packageItem[0] -or $packageItem[0].PSIsContainer -or ($packageItem[0].Attributes -band [IO.FileAttributes]::ReparsePoint) -or $rootItem.Count -ne 1 -or $null -eq $rootItem[0] -or -not $rootItem[0].PSIsContainer -or ($rootItem[0].Attributes -band [IO.FileAttributes]::ReparsePoint)){throw 'Paquete o raíz widget insegura.'}
    # The caller must independently pin and authenticate the MSIX, its
    # identity/version and the protected installed root. This comparison does
    # not validate a signature or grant permission to perform maintenance.
    $entryPaths=[Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $allPaths=[Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $contentTypes=$null;$payloadCount=0
    $zip=[IO.Compression.ZipFile]::OpenRead($package)
    try{
        foreach($entry in $zip.Entries){
            $name=[string]$entry.FullName;$directory=$name.EndsWith('/')
            if($directory){$name=$name.Substring(0,$name.Length-1)}
            if(-not $name -or $name.StartsWith('/') -or $name.Contains('\') -or $name.Contains(':') -or $name -match '[\x00-\x1F]' -or $name -match '(^|/)\.{1,2}($|/)' -or $name -match '(^|/)([^/]*[ .])($|/)' -or $name.Contains('//') -or -not $allPaths.Add($name)){throw 'Entrada widget ambigua.'}
            if($directory){continue}
            $relative=$name.Replace('/','\')
            if(-not $entryPaths.Add($relative)){throw 'Entrada widget duplicada.'}
            # In the supported packages the only AppxMetadata footprint is
            # CodeIntegrity.cat. Do not silently classify unknown reserved
            # metadata as payload or exempt it from installed checks.
            if(($name.StartsWith('AppxMetadata/',[StringComparison]::OrdinalIgnoreCase) -and $name -ine 'AppxMetadata/CodeIntegrity.cat') -or $name.StartsWith('Microsoft.System.Package.Metadata/',[StringComparison]::OrdinalIgnoreCase)){throw 'Footprint widget no admitido.'}
            # OPC container content types are not materialized by the observed
            # Windows deployment. The exception is this one canonical root
            # entry only; all payload and all footprint files are compared.
            if($name -ceq '[Content_Types].xml'){
                if($entry.Length -le 0 -or $entry.Length -gt 1048576){throw 'Metadatos OPC fuera de tamaño.'}
                $settings=[Xml.XmlReaderSettings]::new();$settings.DtdProcessing=[Xml.DtdProcessing]::Prohibit;$settings.XmlResolver=$null;$settings.MaxCharactersInDocument=1048576
                $stream=$entry.Open();$reader=$null
                try{$reader=[Xml.XmlReader]::Create($stream,$settings);$contentTypes=[Xml.XmlDocument]::new();$contentTypes.XmlResolver=$null;$contentTypes.Load($reader)}finally{if($reader){$reader.Dispose()};$stream.Dispose()}
                continue
            }
            $path=[IO.Path]::GetFullPath((Join-Path $root $relative))
            if(-not $path.StartsWith($root+'\',[StringComparison]::OrdinalIgnoreCase)){throw 'Entrada widget fuera de ruta.'}
            $cursor=$path
            while($cursor -and $cursor.StartsWith($root+'\',[StringComparison]::OrdinalIgnoreCase)){
                $items=@(Get-Item -LiteralPath $cursor -Force -ErrorAction Stop)
                if($items.Count -ne 1 -or $null -eq $items[0]){throw ('Archivo widget instalado ausente: '+$relative)}
                if($items[0].Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'Enlace en widget instalado.'}
                if($cursor -eq $path -and $items[0].PSIsContainer){throw 'Archivo widget instalado es directorio.'}
                $cursor=[IO.Path]::GetDirectoryName($cursor)
            }
            $actual=[IO.FileStream]::new($path,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read)
            $inside=$entry.Open();$sha=[Security.Cryptography.SHA256]::Create()
            try{$actualHash=([BitConverter]::ToString($sha.ComputeHash($actual))).Replace('-','');$entryHash=([BitConverter]::ToString($sha.ComputeHash($inside))).Replace('-','')}
            finally{$sha.Dispose();$inside.Dispose();$actual.Dispose()}
            if($actualHash -ne $entryHash){throw 'Un archivo widget instalado no coincide con el paquete fijado.'}
            if($name -notin @('AppxManifest.xml','AppxBlockMap.xml','AppxSignature.p7x','AppxStreamMap.xml','AppxContentGroupMap.xml','AppxMetadata/CodeIntegrity.cat')){$payloadCount++}
        }
    }finally{$zip.Dispose()}
    foreach($required in @('[Content_Types].xml','AppxManifest.xml','AppxBlockMap.xml','AppxSignature.p7x')){if(-not $entryPaths.Contains($required)){throw ('Falta entrada requerida del widget: '+$required)}}
    if(-not $contentTypes -or -not $contentTypes.DocumentElement -or $contentTypes.DocumentElement.LocalName -cne 'Types' -or $contentTypes.DocumentElement.NamespaceURI -cne 'http://schemas.openxmlformats.org/package/2006/content-types' -or $payloadCount -eq 0){throw 'Metadatos OPC o payload inválidos.'}
    $extensions=[Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase);$overrides=[Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach($node in $contentTypes.DocumentElement.ChildNodes){
        if($node.NodeType -ne [Xml.XmlNodeType]::Element){continue}
        if($node.NamespaceURI -cne $contentTypes.DocumentElement.NamespaceURI -or -not $node.GetAttribute('ContentType')){throw 'Tipo de contenido OPC inválido.'}
        if($node.LocalName -ceq 'Default'){
            $ext=$node.GetAttribute('Extension');if(-not $ext -or $ext -notmatch '^[A-Za-z0-9]+$' -or -not $extensions.Add($ext)){throw 'Extensión OPC inválida.'}
        }elseif($node.LocalName -ceq 'Override'){
            $part=$node.GetAttribute('PartName')
            if(-not $part.StartsWith('/') -or $part.Length -lt 2 -or $part.Contains('%') -or $part.Contains('\') -or $part.Contains(':') -or $part -match '(^|/)\.{1,2}($|/)' -or $part.Contains('//')){throw 'Referencia OPC inválida.'}
            $part=$part.Substring(1).Replace('/','\')
            if(-not $overrides.Add($part) -or -not $entryPaths.Contains($part) -or $part -ieq '[Content_Types].xml'){throw 'Referencia OPC no coincide con el paquete.'}
        }else{throw 'Metadato OPC inesperado.'}
    }
    foreach($relative in $entryPaths){
        if($relative -ceq '[Content_Types].xml' -or $overrides.Contains($relative)){continue}
        $extension=[IO.Path]::GetExtension($relative).TrimStart('.')
        if(-not $extension -or -not $extensions.Contains($extension)){throw 'Falta tipo OPC para una entrada.'}
    }
    return $true
}
Export-ModuleMember -Function Get-LtObservedSource,Test-LtObservedSource,Test-LtInstalledWidgetContents
