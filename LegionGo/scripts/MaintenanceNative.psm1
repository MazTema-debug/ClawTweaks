# SPDX-License-Identifier: AGPL-3.0-only
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
if(-not ('LegionTweaks.MaintenanceNative.MoveApi' -as [type])){
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
namespace LegionTweaks.MaintenanceNative {
    public static class MoveApi {
        [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true, EntryPoint="MoveFileExW")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool Move(string source, string target, uint flags);
    }
}
'@
}

# Low-level primitives only. Callers must first verify ACLs, identity, sources,
# recovery path, sessions, all producers, and same-volume target. These do not
# authorize deployment or establish atomicity of AppX/task/certificate.
function Move-LtDirectorySameParent {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Source,[Parameter(Mandatory)][string]$Destination)
    $from=[IO.Path]::GetFullPath($Source).TrimEnd('\');$to=[IO.Path]::GetFullPath($Destination).TrimEnd('\')
    $parent=[IO.Path]::GetDirectoryName($from);$toParent=[IO.Path]::GetDirectoryName($to)
    if(-not $parent -or -not [string]::Equals($parent,$toParent,[StringComparison]::OrdinalIgnoreCase) -or [string]::Equals($from,$to,[StringComparison]::OrdinalIgnoreCase)){throw 'El rename requiere nombres distintos en un mismo directorio padre.'}
    $item=Get-Item -LiteralPath $from -Force
    $parentItem=Get-Item -LiteralPath $parent -Force
    if(-not $item.PSIsContainer -or ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -or ($parentItem.Attributes -band [IO.FileAttributes]::ReparsePoint)){throw 'Directorio de rename inseguro.'}
    if(Test-Path -LiteralPath $to){throw 'El destino de rename ya existe.'}
    if(-not [LegionTweaks.MaintenanceNative.MoveApi]::Move($from,$to,8)){throw [ComponentModel.Win32Exception]::new([Runtime.InteropServices.Marshal]::GetLastWin32Error())}
    if((Test-Path -LiteralPath $from) -or -not (Test-Path -LiteralPath $to -PathType Container)){throw 'El estado posterior al rename es incierto.'}
    return $true
}
function Write-LtCheckpointWriteThrough {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Path,[Parameter(Mandatory)][string]$Content)
    $target=[IO.Path]::GetFullPath($Path);$parent=[IO.Path]::GetDirectoryName($target)
    $directory=Get-Item -LiteralPath $parent -Force
    if(-not $directory.PSIsContainer -or ($directory.Attributes -band [IO.FileAttributes]::ReparsePoint)){throw 'Directorio de checkpoint inseguro.'}
    if((Test-Path -LiteralPath $target) -and ((Get-Item -LiteralPath $target -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)){throw 'Checkpoint es un enlace.'}
    $temp=Join-Path $parent ('.lt-checkpoint-'+[Guid]::NewGuid().ToString('N')+'.tmp')
    $bytes=[Text.Encoding]::UTF8.GetBytes($Content)
    $stream=[IO.FileStream]::new($temp,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None,4096,[IO.FileOptions]::WriteThrough)
    try{$stream.Write($bytes,0,$bytes.Length);$stream.Flush($true)}finally{$stream.Dispose()}
    if(-not [LegionTweaks.MaintenanceNative.MoveApi]::Move($temp,$target,9)){throw [ComponentModel.Win32Exception]::new([Runtime.InteropServices.Marshal]::GetLastWin32Error())}
    return $true
}
Export-ModuleMember -Function Move-LtDirectorySameParent,Write-LtCheckpointWriteThrough
