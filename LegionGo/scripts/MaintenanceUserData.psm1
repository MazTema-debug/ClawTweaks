# SPDX-License-Identifier: AGPL-3.0-only
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
Import-Module (Join-Path $PSScriptRoot 'MaintenanceProtection.psm1') -ErrorAction Stop
if(-not ('LegionTweaks.MaintenanceNative.PackageIdentity' -as [type])){
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Security.Principal;
namespace LegionTweaks.MaintenanceNative {
    public static class PackageIdentity {
        [DllImport("userenv.dll",CharSet=CharSet.Unicode)] static extern int DeriveAppContainerSidFromAppContainerName(string name,out IntPtr sid);
        [DllImport("advapi32.dll")] static extern IntPtr FreeSid(IntPtr sid);
        public static string Derive(string family) {
            IntPtr sid=IntPtr.Zero; int hr=DeriveAppContainerSidFromAppContainerName(family,out sid);
            if(hr!=0) Marshal.ThrowExceptionForHR(hr);
            if(sid==IntPtr.Zero) throw new InvalidOperationException("Missing package SID.");
            try { return new SecurityIdentifier(sid).Value; } finally { if(sid!=IntPtr.Zero) FreeSid(sid); }
        }
    }
}
'@
}
function Assert-LtUserDataRules {
    param([string]$Owner,[object[]]$Rules,[string]$CurrentSid,[string]$PackageSid)
    $owners=@($CurrentSid,'S-1-5-18','S-1-5-32-544','S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464')
    $writers=@($owners)+@($PackageSid)
    if($Owner -notin $owners){throw 'Propietario de datos del widget incierto.'}
    $writeMask=2 -bor 4 -bor 16 -bor 64 -bor 256 -bor 65536 -bor 262144 -bor 524288 -bor 0x40000000 -bor 0x10000000
    foreach($rule in $Rules){
        if($rule.AccessControlType -eq [Security.AccessControl.AccessControlType]::Allow -and -not ($rule.PropagationFlags -band [Security.AccessControl.PropagationFlags]::InheritOnly) -and $rule.IdentityReference.Value -notin $writers -and (([long]$rule.FileSystemRights -band $writeMask) -ne 0)){throw 'Datos del widget escribibles por otra identidad.'}
    }
    return $true
}
function Get-LtWidgetPackageSid {
    return [LegionTweaks.MaintenanceNative.PackageIdentity]::Derive('MazTema.LegionTweaks_wsckx05r95yk0')
}
function Test-LtWidgetDataProtection {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Path,[Parameter(Mandatory)][string]$ExpectedSid)
    $identity=[Security.Principal.WindowsIdentity]::GetCurrent();try{if(-not $identity.User -or $identity.User.Value -ne $ExpectedSid){throw 'Identidad de datos del widget no coincide.'}}finally{$identity.Dispose()}
    $local=[Environment]::GetFolderPath([Environment+SpecialFolder]::LocalApplicationData)
    $root=Join-Path $local 'Packages\MazTema.LegionTweaks_wsckx05r95yk0'
    $full=[IO.Path]::GetFullPath($Path).TrimEnd('\');$root=[IO.Path]::GetFullPath($root).TrimEnd('\')
    $allowed=$false
    foreach($name in @('Settings','LocalState')){$part=Join-Path $root $name;if([string]::Equals($full,$part,[StringComparison]::OrdinalIgnoreCase) -or $full.StartsWith($part+'\',[StringComparison]::OrdinalIgnoreCase)){$allowed=$true}}
    if(-not $allowed){throw 'Datos del widget fuera del ámbito permitido.'}
    $packageSid=Get-LtWidgetPackageSid;$paths=[Collections.Generic.SortedSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $local=[IO.Path]::GetFullPath($local).TrimEnd('\');$cursor=$full
    while($cursor -and ($cursor -eq $local -or $cursor.StartsWith($local+'\',[StringComparison]::OrdinalIgnoreCase))){$null=$paths.Add($cursor);if($cursor -eq $local){break};$cursor=[IO.Path]::GetDirectoryName($cursor)}
    $rootItem=Get-Item -LiteralPath $full -Force -ErrorAction Stop
    if($rootItem.PSIsContainer){foreach($item in Get-ChildItem -LiteralPath $full -Force -Recurse -ErrorAction Stop){$null=$paths.Add($item.FullName)}}
    foreach($path in $paths){
        $item=Get-Item -LiteralPath $path -Force -ErrorAction Stop
        if($item.Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'Enlace en datos del widget.'}
        $acl=Get-Acl -LiteralPath $path -ErrorAction Stop
        $null=Assert-LtUserDataRules -Owner ($acl.GetOwner([Security.Principal.SecurityIdentifier]).Value) -Rules @($acl.GetAccessRules($true,$true,[Security.Principal.SecurityIdentifier])) -CurrentSid $ExpectedSid -PackageSid $packageSid
        $handle=[LegionTweaks.MaintenanceNative.ProtectionApi]::Open($path,0,7,[IntPtr]::Zero,3,0x02200000,[IntPtr]::Zero)
        try{
            $info=[LegionTweaks.MaintenanceNative.FileIdentity]::new()
            if($handle.IsInvalid -or -not [LegionTweaks.MaintenanceNative.BackupApi]::GetFileInformationByHandle($handle,[ref]$info) -or ($info.Attributes -band [uint32][IO.FileAttributes]::ReparsePoint) -or ($info.IndexHigh -eq 0 -and $info.IndexLow -eq 0) -or (-not $item.PSIsContainer -and $info.Links -ne 1)){throw 'Identidad de datos del widget incierta.'}
        }finally{$handle.Dispose()}
    }
    return $true
}
Export-ModuleMember -Function Get-LtWidgetPackageSid,Test-LtWidgetDataProtection
