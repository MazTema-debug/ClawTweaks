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
if(-not ('LegionTweaks.MaintenanceNative.BackupSecurityApi' -as [type])){
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
namespace LegionTweaks.MaintenanceNative {
    public static class BackupSecurityApi {
        [DllImport("kernel32.dll",EntryPoint="CreateFileW",CharSet=CharSet.Unicode,SetLastError=true)]
        public static extern SafeFileHandle Open(string path,uint access,uint share,IntPtr security,uint creation,uint flags,IntPtr template);
        [DllImport("kernel32.dll",SetLastError=true)] [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool BackupWrite(SafeFileHandle file,byte[] buffer,uint count,out uint written,bool abort,bool security,ref IntPtr context);
    }
}
'@
}
# Restore the recorded owner/group/DACL as backup security data. Set-Acl applies
# destination inheritance and can replace inherited ACEs or add the AI flag.
# BackupWrite preserves that descriptor; no privilege is enabled or ownership
# taken. Missing WRITE_OWNER/WRITE_DAC or any readback difference blocks.
function Set-LtBackupSecurity {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Path,
          [Parameter(Mandatory)][Security.AccessControl.FileSystemSecurity]$Security,
          [Parameter(Mandatory)][scriptblock]$AssertProtection)
    $decision=@(& $AssertProtection $Path)
    if($decision.Count -ne 1 -or $decision[0] -isnot [bool] -or -not $decision[0]){throw 'Protección de descriptor de respaldo incierta.'}
    $item=Get-Item -LiteralPath $Path -Force -ErrorAction Stop
    if($item.Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'Enlace en destino de descriptor de respaldo.'}
    $descriptor=$Security.GetSecurityDescriptorBinaryForm()
    $raw=[Security.AccessControl.RawSecurityDescriptor]::new($descriptor,0)
    if(-not $raw.Owner -or -not $raw.Group -or -not $raw.DiscretionaryAcl -or $descriptor.Length -gt 1048576){throw 'Descriptor de respaldo incompleto.'}
    $memory=[IO.MemoryStream]::new();$writer=[IO.BinaryWriter]::new($memory)
    try{
        # Serialized WIN32_STREAM_ID header: BACKUP_SECURITY_DATA, security
        # attribute, descriptor size and zero stream-name bytes (20 bytes).
        $writer.Write([uint32]3);$writer.Write([uint32]2);$writer.Write([long]$descriptor.Length);$writer.Write([uint32]0);$writer.Write([byte[]]$descriptor);$writer.Flush();$buffer=$memory.ToArray()
    }finally{$writer.Dispose();$memory.Dispose()}
    $handle=[LegionTweaks.MaintenanceNative.BackupSecurityApi]::Open($item.FullName,0x000e0000,1,[IntPtr]::Zero,3,0x02200000,[IntPtr]::Zero)
    if($handle.IsInvalid){$code=[Runtime.InteropServices.Marshal]::GetLastWin32Error();$handle.Dispose();throw ('No se abrió el destino para conservar descriptor; Win32='+$code)}
    $context=[IntPtr]::Zero;$written=[uint32]0;$verified=$false
    try{
        $identity=[LegionTweaks.MaintenanceNative.FileIdentity]::new()
        if(-not [LegionTweaks.MaintenanceNative.BackupApi]::GetFileInformationByHandle($handle,[ref]$identity) -or ($identity.Attributes -band 0x400) -or (-not $item.PSIsContainer -and $identity.Links -ne 1)){throw 'Identidad de destino de descriptor incierta.'}
        if(-not [LegionTweaks.MaintenanceNative.BackupSecurityApi]::BackupWrite($handle,$buffer,$buffer.Length,[ref]$written,$false,$true,[ref]$context) -or $written -ne $buffer.Length){throw ('No se conservó descriptor de respaldo; Win32='+[Runtime.InteropServices.Marshal]::GetLastWin32Error())}
        if((Get-Acl -LiteralPath $item.FullName -ErrorAction Stop).Sddl -cne $Security.Sddl){throw 'El descriptor de respaldo leído no coincide con el original.'}
        $decision=@(& $AssertProtection $Path)
        if($decision.Count -ne 1 -or $decision[0] -isnot [bool] -or -not $decision[0]){throw 'Cambió la protección tras conservar descriptor.'}
        $verified=$true
    }finally{
        $released=[LegionTweaks.MaintenanceNative.BackupSecurityApi]::BackupWrite($handle,$null,0,[ref]$written,$true,$true,[ref]$context)
        $handle.Dispose()
        if($verified -and -not $released){throw 'No se cerró el contexto de descriptor de respaldo.'}
    }
    return $true
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
    if($PreserveSourceAcl){$null=Set-LtBackupSecurity -Path $dst -Security (Get-Acl -LiteralPath $src -ErrorAction Stop) -AssertProtection $AssertProtection}
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
            $in.Position=0
            $sourceParent=$src;$backupParent=$dst
            $relativeParent=[IO.Path]::GetDirectoryName($rel)
            if($relativeParent){foreach($part in $relativeParent.Split([char]'\')){
                $sourceParent=Join-Path $sourceParent $part;$backupParent=Join-Path $backupParent $part
                if(-not(Test-Path -LiteralPath $backupParent)){$null=New-Item -ItemType Directory -Path $backupParent -ErrorAction Stop}
                if($PreserveSourceAcl){$null=Set-LtBackupSecurity -Path $backupParent -Security (Get-Acl -LiteralPath $sourceParent -ErrorAction Stop) -AssertProtection $AssertProtection}
            }}
            $out=[IO.FileStream]::new($output,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None,4096,[IO.FileOptions]::WriteThrough)
            try{$in.CopyTo($out);$out.Flush($true)}finally{$out.Dispose()}
            if($PreserveSourceAcl){$null=Set-LtBackupSecurity -Path $output -Security $security -AssertProtection $AssertProtection}
            $decision=@(& $AssertProtection $src $dst $output)
            if((Get-FileHash -LiteralPath $output -Algorithm SHA256).Hash -ne $hash -or (Get-FileHash -LiteralPath $input -Algorithm SHA256).Hash -ne $hash -or (Get-Acl -LiteralPath $input -ErrorAction Stop).Sddl -ne $acl -or ($PreserveSourceAcl -and (Get-Acl -LiteralPath $output -ErrorAction Stop).Sddl -ne $acl) -or $decision.Count -ne 1 -or $decision[0] -isnot [bool] -or -not $decision[0]){throw 'El respaldo u original cambió durante la copia.'}
            $items.Add([pscustomobject]@{RelativePath=$rel;Sha256=$hash;Sddl=$acl;Volume=$id.Volume;FileIndexHigh=$id.IndexHigh;FileIndexLow=$id.IndexLow})
        } finally {$in.Dispose()}
    }
    return $items.ToArray()
}
Export-ModuleMember -Function Copy-LtProtectedBackup,Set-LtBackupSecurity
