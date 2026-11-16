# SPDX-License-Identifier: AGPL-3.0-only
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
if(-not ('LegionTweaks.MaintenanceNative.BackupApi' -as [type])){
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
namespace LegionTweaks.MaintenanceNative {
    [StructLayout(LayoutKind.Sequential)] public struct FileIdentity {
        public uint Attributes; public System.Runtime.InteropServices.ComTypes.FILETIME Creation,Access,Write;
        public uint Volume,SizeHigh,SizeLow,Links,IndexHigh,IndexLow;
    }
    public static class BackupApi {
        [DllImport("kernel32.dll",SetLastError=true)] [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetFileInformationByHandle(SafeFileHandle handle, out FileIdentity info);
    }
}
'@
}
function Copy-LtProtectedBackup {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$SourceRoot,[Parameter(Mandatory)][string]$DestinationRoot,
          [Parameter(Mandatory)][string[]]$RelativeFiles,[Parameter(Mandatory)][scriptblock]$AssertProtection,
          [bool]$PreserveSourceAcl=$true)
    $src=[IO.Path]::GetFullPath($SourceRoot).TrimEnd('\');$dst=[IO.Path]::GetFullPath($DestinationRoot).TrimEnd('\')
    if($src -eq $dst -or $dst.StartsWith($src+'\',[StringComparison]::OrdinalIgnoreCase) -or $src.StartsWith($dst+'\',[StringComparison]::OrdinalIgnoreCase) -or $RelativeFiles.Count -eq 0){throw 'Rutas de respaldo inválidas.'}
    if(Test-Path -LiteralPath $dst){throw 'El respaldo de destino ya existe.'}
    $decision=@(& $AssertProtection $src $dst)
    if($decision.Count -ne 1 -or $decision[0] -isnot [bool] -or -not $decision[0]){throw 'No se comprobó protección del respaldo.'}
    $seen=[Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $items=[Collections.Generic.List[object]]::new()
    $null=New-Item -ItemType Directory -Path $dst -ErrorAction Stop
    foreach($rel in $RelativeFiles){
        if(-not $rel -or [IO.Path]::IsPathRooted($rel) -or $rel.Contains(':') -or $rel -match '(^|[\\/])\.\.?($|[\\/])' -or -not $seen.Add($rel.Replace('/','\'))){throw 'Ruta de respaldo duplicada o inválida.'}
        $input=[IO.Path]::GetFullPath((Join-Path $src $rel));$output=[IO.Path]::GetFullPath((Join-Path $dst $rel))
        if(-not $input.StartsWith($src+'\',[StringComparison]::OrdinalIgnoreCase) -or -not $output.StartsWith($dst+'\',[StringComparison]::OrdinalIgnoreCase)){throw 'Ruta de respaldo fuera de ámbito.'}
        $cursor=$input
        while($cursor -and $cursor.StartsWith($src+'\',[StringComparison]::OrdinalIgnoreCase)){
            $entry=Get-Item -LiteralPath $cursor -Force -ErrorAction Stop
            if($entry.Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'Enlace en ruta de respaldo.'}
            $cursor=Split-Path -Parent $cursor
        }
        $decision=@(& $AssertProtection $src $dst)
        if($decision.Count -ne 1 -or $decision[0] -isnot [bool] -or -not $decision[0]){throw 'Cambió la protección del respaldo.'}
        $in=[IO.FileStream]::new($input,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read)
        try {
            $id=[LegionTweaks.MaintenanceNative.FileIdentity]::new()
            if(-not [LegionTweaks.MaintenanceNative.BackupApi]::GetFileInformationByHandle($in.SafeFileHandle,[ref]$id) -or $id.Links -ne 1 -or ($id.IndexHigh -eq 0 -and $id.IndexLow -eq 0)){throw 'Identidad o enlaces del original inciertos.'}
            $security=Get-Acl -LiteralPath $input -ErrorAction Stop;$acl=$security.Sddl
            $sha=[Security.Cryptography.SHA256]::Create();try{$hash=([BitConverter]::ToString($sha.ComputeHash($in))).Replace('-','')}finally{$sha.Dispose()}
            $in.Position=0;$null=New-Item -ItemType Directory -Path (Split-Path -Parent $output) -Force
            $out=[IO.FileStream]::new($output,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None,4096,[IO.FileOptions]::WriteThrough)
            try{$in.CopyTo($out);$out.Flush($true)}finally{$out.Dispose()}
            if($PreserveSourceAcl){Set-Acl -LiteralPath $output -AclObject $security -ErrorAction Stop}
            $decision=@(& $AssertProtection $src $dst $output)
            if((Get-FileHash -LiteralPath $output -Algorithm SHA256).Hash -ne $hash -or (Get-FileHash -LiteralPath $input -Algorithm SHA256).Hash -ne $hash -or (Get-Acl -LiteralPath $input -ErrorAction Stop).Sddl -ne $acl -or ($PreserveSourceAcl -and (Get-Acl -LiteralPath $output -ErrorAction Stop).Sddl -ne $acl) -or $decision.Count -ne 1 -or $decision[0] -isnot [bool] -or -not $decision[0]){throw 'El respaldo u original cambió durante la copia.'}
            $items.Add([pscustomobject]@{RelativePath=$rel;Sha256=$hash;Sddl=$acl;Volume=$id.Volume;FileIndexHigh=$id.IndexHigh;FileIndexLow=$id.IndexLow})
        } finally {$in.Dispose()}
    }
    return $items.ToArray()
}
Export-ModuleMember -Function Copy-LtProtectedBackup
