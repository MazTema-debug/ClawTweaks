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
    $package=[IO.Path]::GetFullPath($PackagePath);$root=[IO.Path]::GetFullPath($InstallLocation).TrimEnd('\')
    $entryPaths=[Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $zip=[IO.Compression.ZipFile]::OpenRead($package)
    try{
        foreach($entry in $zip.Entries){
            if($entry.FullName.EndsWith('/')){continue}
            $relative=$entry.FullName.Replace('/','\')
            if(-not $relative -or $relative.StartsWith('\') -or $relative.Contains(':') -or $relative -match '(^|\\)\.\.?($|\\)' -or -not $entryPaths.Add($relative)){throw 'Entrada widget ambigua.'}
            $path=[IO.Path]::GetFullPath((Join-Path $root $relative))
            if(-not $path.StartsWith($root+'\',[StringComparison]::OrdinalIgnoreCase)){throw 'Entrada widget fuera de ruta.'}
            $item=Get-Item -LiteralPath $path -Force -ErrorAction Stop
            if($item.PSIsContainer -or ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)){throw 'Archivo widget instalado inseguro.'}
            $actual=[IO.FileStream]::new($path,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read)
            $inside=$entry.Open();$sha=[Security.Cryptography.SHA256]::Create()
            try{$actualHash=([BitConverter]::ToString($sha.ComputeHash($actual))).Replace('-','');$entryHash=([BitConverter]::ToString($sha.ComputeHash($inside))).Replace('-','')}
            finally{$sha.Dispose();$inside.Dispose();$actual.Dispose()}
            if($actualHash -ne $entryHash){throw 'Un archivo widget instalado no coincide con el paquete fijado.'}
        }
    }finally{$zip.Dispose()}
    if($entryPaths.Count -eq 0){throw 'Paquete widget vacío.'}
    return $true
}
Export-ModuleMember -Function Get-LtObservedSource,Test-LtObservedSource,Test-LtInstalledWidgetContents
