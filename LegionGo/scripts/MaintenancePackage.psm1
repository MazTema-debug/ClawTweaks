# SPDX-License-Identifier: AGPL-3.0-only
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'

# Read-only validation against an externally pinned archive. A successful
# result is not permission to install and must be rechecked before mutation.
function Test-LtPinnedPackage {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Archive,[Parameter(Mandatory)][string]$Extracted,
          [Parameter(Mandatory)][ValidatePattern('^[0-9A-Fa-f]{64}$')][string]$ExpectedSha256)
    Add-Type -AssemblyName System.IO.Compression,System.IO.Compression.FileSystem
    $archivePath=[IO.Path]::GetFullPath($Archive)
    $root=[IO.Path]::GetFullPath($Extracted).TrimEnd('\')
    if(-not [IO.File]::Exists($archivePath) -or -not [IO.Directory]::Exists($root)){throw 'Falta el paquete o la extracción.'}
    if((Get-Item -LiteralPath $archivePath -Force).Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'El archivo de paquete es un enlace.'}
    if((Get-Item -LiteralPath $root -Force).Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'La extracción es un enlace.'}
    $archiveStream=[IO.FileStream]::new($archivePath,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read)
    $sha=[Security.Cryptography.SHA256]::Create()
    try{$archiveHash=([BitConverter]::ToString($sha.ComputeHash($archiveStream))).Replace('-','')}catch{$archiveStream.Dispose();throw}finally{$sha.Dispose()}
    if($archiveHash -ne $ExpectedSha256){$archiveStream.Dispose();throw 'Hash del ZIP incorrecto.'}
    $archiveStream.Position=0
    try{$zip=[IO.Compression.ZipArchive]::new($archiveStream,[IO.Compression.ZipArchiveMode]::Read,$true)}catch{$archiveStream.Dispose();throw}
    try {
        $entries=[Collections.Generic.Dictionary[string,object]]::new([StringComparer]::OrdinalIgnoreCase)
        foreach($e in $zip.Entries){
            if($e.FullName.EndsWith('/')){continue}
            $name=$e.FullName.Replace('/','\')
            if($name -match '(^|\\)\.\.?($|\\)' -or $name.StartsWith('\') -or $name.Contains(':') -or $name -match '[\x00-\x1f]' -or $entries.ContainsKey($name)){throw 'Ruta de ZIP inválida o duplicada.'}
            $entries.Add($name,$e)
        }
        if(-not $entries.ContainsKey('SHA256SUMS.txt')){throw 'Falta manifiesto interno.'}
        $manifestStream=$entries['SHA256SUMS.txt'].Open()
        try {$reader=[IO.StreamReader]::new($manifestStream);try{$lines=@();while(-not $reader.EndOfStream){$lines+= $reader.ReadLine()}}finally{$reader.Dispose()}}finally{$manifestStream.Dispose()}
        $seen=[Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
        foreach($line in $lines){
            if($line -notmatch '^([0-9A-Fa-f]{64})  (.+)$'){throw 'Manifiesto malformado.'}
            $hash=$Matches[1];$rel=$Matches[2].Replace('/','\')
            if(-not $seen.Add($rel) -or -not $entries.ContainsKey($rel) -or $rel -eq 'SHA256SUMS.txt'){throw 'Entrada de manifiesto duplicada o ausente.'}
            $file=[IO.Path]::GetFullPath((Join-Path $root $rel))
            if(-not $file.StartsWith($root+'\',[StringComparison]::OrdinalIgnoreCase)){throw 'La ruta sale de la extracción.'}
            $cursor=$file
            while($cursor -and $cursor.StartsWith($root+'\',[StringComparison]::OrdinalIgnoreCase)){
                $item=Get-Item -LiteralPath $cursor -Force
                if($item.Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'Enlace en la extracción.'}
                $cursor=Split-Path -Parent $cursor
            }
            if((Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash -ne $hash){throw 'Hash de archivo extraído incorrecto.'}
            $stream=$entries[$rel].Open();$sha=[Security.Cryptography.SHA256]::Create()
            try{$zipHash=([BitConverter]::ToString($sha.ComputeHash($stream))).Replace('-','')}finally{$sha.Dispose();$stream.Dispose()}
            if($zipHash -ne $hash){throw 'Hash de entrada ZIP incorrecto.'}
        }
        if($seen.Count -ne ($entries.Count-1)){throw 'El ZIP contiene archivos no declarados.'}
        $manifestFile=Join-Path $root 'SHA256SUMS.txt';$zipManifest=$entries['SHA256SUMS.txt'].Open();$sha=[Security.Cryptography.SHA256]::Create()
        try{$expectedManifest=([BitConverter]::ToString($sha.ComputeHash($zipManifest))).Replace('-','')}finally{$sha.Dispose();$zipManifest.Dispose()}
        if((Get-FileHash -LiteralPath $manifestFile -Algorithm SHA256).Hash -ne $expectedManifest){throw 'El manifiesto extraído cambió.'}
        foreach($f in Get-ChildItem -LiteralPath $root -Force -Recurse){
            if($f.Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'Enlace adicional en extracción.'}
            if(-not $f.PSIsContainer){$rel=$f.FullName.Substring($root.Length+1);if($rel -ne 'SHA256SUMS.txt' -and -not $seen.Contains($rel)){throw 'Archivo extraído no declarado.'}}
        }
        return [pscustomobject]@{ArchiveSha256=$ExpectedSha256.ToUpperInvariant(); InternalFiles=$seen.Count; TotalFiles=$entries.Count; AuthorizesMaintenance=$false}
    } finally {$zip.Dispose();$archiveStream.Dispose()}
}
function Test-LtPinnedComponentSignatures {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string[]]$Paths,
          [Parameter(Mandatory)][ValidatePattern('^[0-9A-Fa-f]{40}$')][string]$ExpectedSigner,
          [switch]$AllowOnlyUntrustedRoot)
    if($Paths.Count -ne 3){throw 'Se requieren exactamente helper, QAM y paquete widget.'}
    $unique=[Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $results=[Collections.Generic.List[object]]::new()
    foreach($path in $Paths){
        $full=[IO.Path]::GetFullPath($path)
        if(-not $unique.Add($full)){throw 'Componente firmado duplicado.'}
        $item=Get-Item -LiteralPath $full -Force
        if($item.PSIsContainer -or ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)){throw 'Componente firmado inseguro.'}
        $signature=Get-AuthenticodeSignature -LiteralPath $full
        if(-not $signature.SignerCertificate -or $signature.SignerCertificate.Thumbprint -ne $ExpectedSigner){throw 'Firmante incorrecto.'}
        if($signature.Status -ne 'Valid'){
            if(-not $AllowOnlyUntrustedRoot -or [string]$signature.Status -notin @('UnknownError','NotTrusted')){throw 'Firma no válida.'}
            $chain=[Security.Cryptography.X509Certificates.X509Chain]::new()
            try{
                $chain.ChainPolicy.RevocationMode=[Security.Cryptography.X509Certificates.X509RevocationMode]::NoCheck
                $null=$chain.Build($signature.SignerCertificate)
                $statuses=@($chain.ChainStatus)
                if($statuses.Count -ne 1 -or $statuses[0].Status -ne [Security.Cryptography.X509Certificates.X509ChainStatusFlags]::UntrustedRoot -or $signature.StatusMessage.Trim().TrimEnd('.') -ne $statuses[0].StatusInformation.Trim().TrimEnd('.')){throw 'La cadena no falla exclusivamente por raíz no confiable.'}
            }finally{$chain.Dispose()}
        }
        $results.Add([pscustomobject]@{Path=$full;Status=[string]$signature.Status;Signer=$signature.SignerCertificate.Thumbprint})
    }
    return $results.ToArray()
}
function Get-LtWidgetPackageIdentity {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Path)
    Add-Type -AssemblyName System.IO.Compression,System.IO.Compression.FileSystem
    $full=[IO.Path]::GetFullPath($Path);$item=Get-Item -LiteralPath $full -Force -ErrorAction Stop
    if($item.PSIsContainer -or ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)){throw 'Paquete widget inseguro.'}
    $file=[IO.FileStream]::new($full,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read)
    try{
        $zip=[IO.Compression.ZipArchive]::new($file,[IO.Compression.ZipArchiveMode]::Read,$true)
        try{
            $entries=@($zip.Entries|Where-Object {$_.FullName -ceq 'AppxManifest.xml'})
            if($entries.Count -ne 1 -or $entries[0].Length -le 0 -or $entries[0].Length -gt 2097152){throw 'Manifiesto widget ambiguo o fuera de tamaño.'}
            $stream=$entries[0].Open();$settings=[Xml.XmlReaderSettings]::new();$settings.DtdProcessing=[Xml.DtdProcessing]::Prohibit;$settings.XmlResolver=$null;$settings.MaxCharactersInDocument=2097152
            try{$reader=[Xml.XmlReader]::Create($stream,$settings);try{$document=[Xml.XmlDocument]::new();$document.XmlResolver=$null;$document.Load($reader)}finally{$reader.Dispose()}}finally{$stream.Dispose()}
            $nodes=@($document.SelectNodes('/*[local-name()="Package"]/*[local-name()="Identity"]'))
            if($nodes.Count -ne 1){throw 'Identidad widget ausente o ambigua.'}
            $identity=$nodes[0];$name=$identity.GetAttribute('Name');$publisher=$identity.GetAttribute('Publisher');$architecture=$identity.GetAttribute('ProcessorArchitecture');$version=$identity.GetAttribute('Version')
            if($name -cne 'MazTema.LegionTweaks' -or $publisher -cne 'CN=LegionTweaks Dev' -or $architecture -ine 'x64'){throw 'Identidad widget no corresponde al producto.'}
            try{$parsed=[version]$version}catch{throw 'Versión de widget inválida.'}
            if($parsed.ToString(4) -cne $version){throw 'Versión widget no canónica.'}
            return [pscustomobject]@{Name=$name;Publisher=$publisher;Architecture='x64';Version=$version;AuthorizesMaintenance=$false}
        }finally{$zip.Dispose()}
    }finally{$file.Dispose()}
}
Export-ModuleMember -Function Test-LtPinnedPackage,Test-LtPinnedComponentSignatures,Get-LtWidgetPackageIdentity
