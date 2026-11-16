# SPDX-License-Identifier: AGPL-3.0-only
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
Import-Module (Join-Path $PSScriptRoot 'MaintenanceBackup.psm1') -ErrorAction Stop
$script:leases=[Collections.Generic.Dictionary[object,object]]::new()
function Open-LtCoordinatorLease {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Path,[Parameter(Mandatory)][scriptblock]$AssertProtection)
    $full=[IO.Path]::GetFullPath($Path);$parent=[IO.Path]::GetDirectoryName($full)
    if(-not $parent -or [IO.Path]::GetFileName($full) -ne 'coordinator-v1.lock'){throw 'Ruta de exclusión inválida.'}
    $check=@(& $AssertProtection $parent);if($check.Count -ne 1 -or $check[0] -isnot [bool] -or -not $check[0]){throw 'Directorio de exclusión no protegido.'}
    try{$attributes=[IO.File]::GetAttributes($full);if($attributes -band ([IO.FileAttributes]::ReparsePoint -bor [IO.FileAttributes]::Directory)){throw 'Ruta de exclusión no es un archivo normal.'}}
    catch [IO.FileNotFoundException]{}
    $stream=[IO.FileStream]::new($full,[IO.FileMode]::OpenOrCreate,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None)
    try{
        $id=[LegionTweaks.MaintenanceNative.FileIdentity]::new()
        if(-not [LegionTweaks.MaintenanceNative.BackupApi]::GetFileInformationByHandle($stream.SafeFileHandle,[ref]$id) -or $id.Links -ne 1 -or ($id.Attributes -band [uint32][IO.FileAttributes]::ReparsePoint) -or ($id.IndexHigh -eq 0 -and $id.IndexLow -eq 0)){throw 'Identidad de exclusión incierta.'}
        $check=@(& $AssertProtection $full);if($check.Count -ne 1 -or $check[0] -isnot [bool] -or -not $check[0]){throw 'Archivo de exclusión no protegido.'}
        $token=[pscustomobject]@{Path=$full;AuthorizesMaintenance=$false}
        $script:leases.Add($token,[pscustomobject]@{Stream=$stream;Token=$token;Check=$AssertProtection;Path=$full})
        return $token
    }catch{$stream.Dispose();throw}
}
function Test-LtCoordinatorLease {
    [CmdletBinding()]
    param([Parameter(Mandatory)]$Lease)
    if(-not $script:leases.ContainsKey($Lease)){throw 'Exclusión ajena o ya cerrada.'}
    $record=$script:leases[$Lease]
    if(-not [object]::ReferenceEquals($Lease,$record.Token) -or $Lease.Path -cne $record.Path -or $Lease.AuthorizesMaintenance -ne $false -or -not $record.Stream.CanRead){throw 'Exclusión inválida.'}
    $values=@(& $record.Check $record.Path)
    if($values.Count -ne 1 -or $values[0] -isnot [bool] -or -not $values[0]){throw 'Protección de exclusión cambió.'}
    return $true
}
function Close-LtCoordinatorLease {
    [CmdletBinding()]
    param([Parameter(Mandatory)]$Lease)
    if(-not $script:leases.ContainsKey($Lease)){throw 'Exclusión ajena o ya cerrada.'}
    $record=$script:leases[$Lease]
    try{$record.Stream.Dispose()}finally{$null=$script:leases.Remove($Lease)}
    return $true
}
Export-ModuleMember -Function Open-LtCoordinatorLease,Test-LtCoordinatorLease,Close-LtCoordinatorLease
