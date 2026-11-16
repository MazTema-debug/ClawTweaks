# SPDX-License-Identifier: AGPL-3.0-only
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
Import-Module (Join-Path $PSScriptRoot 'MaintenanceBackup.psm1') -ErrorAction Stop
if(-not ('LegionTweaks.MaintenanceNative.ProtectionApi' -as [type])){
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
namespace LegionTweaks.MaintenanceNative {
    public static class ProtectionApi {
        [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true,EntryPoint="CreateFileW")]
        public static extern SafeFileHandle Open(string path,uint access,uint share,IntPtr sec,uint creation,uint flags,IntPtr template);
    }
}
'@
}
function Assert-LtPrivilegedRules {
    param([string]$Owner,[object[]]$Rules,[switch]$Ancestor)
    $privileged=@('S-1-5-18','S-1-5-32-544','S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464')
    if($Owner -notin $privileged){throw 'Propietario de ruta no privilegiado.'}
    # Ancestors must prevent deleting/replacing children or changing ACL/owner;
    # ordinary creation of unrelated siblings is not a write to the root.
    $writeMask=if($Ancestor){64 -bor 65536 -bor 262144 -bor 524288 -bor 0x10000000}else{2 -bor 4 -bor 16 -bor 64 -bor 256 -bor 65536 -bor 262144 -bor 524288 -bor 0x40000000 -bor 0x10000000}
    foreach($r in $Rules){
        if($r.PropagationFlags -band [Security.AccessControl.PropagationFlags]::InheritOnly){continue}
        if($r.AccessControlType -eq [Security.AccessControl.AccessControlType]::Allow -and $r.IdentityReference.Value -notin $privileged -and (([long]$r.FileSystemRights -band $writeMask) -ne 0)){throw 'Permiso de escritura no privilegiado.'}
    }
    return $true
}
function Get-LtProtectedSnapshot {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string[]]$Roots,[switch]$WithoutDescendants,[switch]$ExcludeY3AdmissionGuard)
    if($Roots.Count -eq 0){throw 'Faltan rutas protegidas.'}
    $excludedPath=$null
    if($ExcludeY3AdmissionGuard){
        $identity=[Security.Principal.WindowsIdentity]::GetCurrent()
        try{if(-not $identity.User){throw 'Identidad Y3 ausente.'};$y3=Join-Path (Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::CommonApplicationData)) 'LegionTweaks') $identity.User.Value}finally{$identity.Dispose()}
        if($Roots.Count -ne 1 -or -not [string]::Equals([IO.Path]::GetFullPath($Roots[0]).TrimEnd('\'),[IO.Path]::GetFullPath($y3).TrimEnd('\'),[StringComparison]::OrdinalIgnoreCase)){throw 'Exclusión de reserva fuera del directorio Y3 actual.'}
        $excludedPath=Join-Path $y3 'fps-y3-admission-v1.guard'
    }
    $paths=[Collections.Generic.SortedSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $inside=[Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach($root in $Roots){
        if(-not [IO.Path]::IsPathRooted($root)){throw 'Ruta protegida no absoluta.'}
        $full=[IO.Path]::GetFullPath($root).TrimEnd('\');$item=Get-Item -LiteralPath $full -Force -ErrorAction Stop
        if($item.Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'Raíz protegida es un enlace.'}
        $null=$inside.Add($full)
        $cursor=$full
        while($cursor){$null=$paths.Add($cursor);$next=[IO.Path]::GetDirectoryName($cursor);if(-not $next -or $next -eq $cursor){break};$cursor=$next}
        if($item.PSIsContainer -and -not $WithoutDescendants){foreach($child in Get-ChildItem -LiteralPath $full -Force -Recurse -ErrorAction Stop){if($child.Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'Enlace bajo ruta protegida.'};if($excludedPath -and [string]::Equals($child.FullName,$excludedPath,[StringComparison]::OrdinalIgnoreCase)){if($child.PSIsContainer){throw 'Marcador Y3 no es archivo.'};continue};$null=$paths.Add($child.FullName);$null=$inside.Add($child.FullName)}}
    }
    $lines=[Collections.Generic.List[string]]::new()
    foreach($path in $paths){
        $item=Get-Item -LiteralPath $path -Force -ErrorAction Stop
        if($item.Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'Enlace en ruta protegida.'}
        $acl=Get-Acl -LiteralPath $path -ErrorAction Stop
        $owner=$acl.GetOwner([Security.Principal.SecurityIdentifier]).Value
        $rules=@($acl.GetAccessRules($true,$true,[Security.Principal.SecurityIdentifier]))
        $null=Assert-LtPrivilegedRules -Owner $owner -Rules $rules -Ancestor:(-not $inside.Contains($path))
        # Read attributes by handle, without following reparse points or opening
        # with data access. Parent protection and repeat snapshots remain needed.
        $handle=[LegionTweaks.MaintenanceNative.ProtectionApi]::Open($path,0,7,[IntPtr]::Zero,3,0x02200000,[IntPtr]::Zero)
        try{
            if($handle.IsInvalid){throw [ComponentModel.Win32Exception]::new([Runtime.InteropServices.Marshal]::GetLastWin32Error())}
            $info=[LegionTweaks.MaintenanceNative.FileIdentity]::new()
            if(-not [LegionTweaks.MaintenanceNative.BackupApi]::GetFileInformationByHandle($handle,[ref]$info) -or ($info.Attributes -band [uint32][IO.FileAttributes]::ReparsePoint) -or ($info.IndexHigh -eq 0 -and $info.IndexLow -eq 0)){throw 'Identidad de ruta protegida incierta.'}
            if(-not $item.PSIsContainer -and $info.Links -ne 1){throw 'Archivo protegido con enlaces adicionales.'}
            $lines.Add(($path.ToUpperInvariant()+'|'+$info.Volume+'|'+$info.IndexHigh+'|'+$info.IndexLow+'|'+$acl.Sddl))
        }finally{$handle.Dispose()}
    }
    $sha=[Security.Cryptography.SHA256]::Create()
    try{$digest=([BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes(($lines -join "`n"))))).Replace('-','')}finally{$sha.Dispose()}
    return [pscustomobject]@{ProtectionSha256=$digest;Items=$lines.Count;AuthorizesMaintenance=$false}
}
function Set-LtStagingAcl {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Tree)
    $full=[IO.Path]::GetFullPath($Tree).TrimEnd('\')
    $parent=[IO.Path]::GetDirectoryName($full)
    if(-not [string]::Equals($parent,[IO.Path]::GetFullPath($env:ProgramFiles).TrimEnd('\'),[StringComparison]::OrdinalIgnoreCase) -or [IO.Path]::GetFileName($full) -notmatch '^LegionTweaks-Z1E-(?:stage|rollback)-[a-f0-9]{32}$'){throw 'Árbol temporal fuera de la ruta prevista.'}
    $item=Get-Item -LiteralPath $full -Force -ErrorAction Stop
    if(-not $item.PSIsContainer -or ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)){throw 'Árbol temporal inseguro.'}
    # Same protected DACL as .49's historical installer. Any owner/ancestor or
    # inherited child that fails the strict snapshot leaves staging unusable.
    $acl=[Security.AccessControl.DirectorySecurity]::new();$acl.SetAccessRuleProtection($true,$false)
    $acl.SetOwner([Security.Principal.SecurityIdentifier]::new('S-1-5-32-544'))
    foreach($entry in @(@('S-1-5-18','FullControl'),@('S-1-5-32-544','FullControl'),@('S-1-5-32-545','ReadAndExecute'))){
        $sid=[Security.Principal.SecurityIdentifier]::new($entry[0])
        $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new($sid,$entry[1],'ContainerInherit,ObjectInherit','None','Allow'))
    }
    Set-Acl -LiteralPath $full -AclObject $acl -ErrorAction Stop
    $snapshot=Get-LtProtectedSnapshot -Roots @($full)
    if($snapshot.AuthorizesMaintenance -ne $false -or $snapshot.Items -le 0){throw 'No se confirmó protección de staging.'}
    return $true
}
Export-ModuleMember -Function Get-LtProtectedSnapshot,Set-LtStagingAcl
