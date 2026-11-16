# SPDX-License-Identifier: AGPL-3.0-only
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
Import-Module (Join-Path $PSScriptRoot 'MaintenanceBackup.psm1') -ErrorAction Stop

# This primitive is NOT a complete transaction or an entry point. The caller
# must have independently verified backup, original identity/ACL, quiescence,
# every legitimate image path and a recovery route before invoking it.
function Disable-LtImageForPublication {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Image,
        [Parameter(Mandatory)][ValidatePattern('^[0-9A-Fa-f]{64}$')][string]$ExpectedSha256,
        [Parameter(Mandatory)][scriptblock]$AssertScope)
    $path=[IO.Path]::GetFullPath($Image)
    $decision=@(& $AssertScope $path)
    if($decision.Count -ne 1 -or $decision[0] -isnot [bool] -or -not $decision[0]){throw 'Ámbito de imagen inseguro.'}
    $item=Get-Item -LiteralPath $path -Force -ErrorAction Stop
    if($item.PSIsContainer -or ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)){throw 'Imagen insegura.'}
    $stream=[IO.FileStream]::new($path,[IO.FileMode]::Open,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None,4096,[IO.FileOptions]::WriteThrough)
    try{
        $identity=[LegionTweaks.MaintenanceNative.FileIdentity]::new()
        if(-not [LegionTweaks.MaintenanceNative.BackupApi]::GetFileInformationByHandle($stream.SafeFileHandle,[ref]$identity) -or $identity.Links -ne 1 -or ($identity.IndexHigh -eq 0 -and $identity.IndexLow -eq 0)){throw 'Identidad o enlaces de imagen inciertos.'}
        $sha=[Security.Cryptography.SHA256]::Create();try{$hash=([BitConverter]::ToString($sha.ComputeHash($stream))).Replace('-','')}finally{$sha.Dispose()}
        if($hash -ne $ExpectedSha256){throw 'Imagen no coincide con su pin.'}
        $stream.Position=0;if($stream.ReadByte() -ne 77 -or $stream.ReadByte() -ne 90){throw 'No es una imagen MZ esperada.'}
        $decision=@(& $AssertScope $path)
        if($decision.Count -ne 1 -or $decision[0] -isnot [bool] -or -not $decision[0]){throw 'Cambió la protección de imagen.'}
        $stream.Position=0;$stream.WriteByte(0);$stream.Flush($true)
        $stream.Position=0;if($stream.ReadByte() -ne 0 -or $stream.ReadByte() -ne 90){throw 'No se confirmó la barrera de imagen.'}
        return [pscustomobject]@{Volume=$identity.Volume;FileIndexHigh=$identity.IndexHigh;FileIndexLow=$identity.IndexLow;OriginalSha256=$hash;FirstByte=0;AuthorizesMaintenance=$false}
    }finally{$stream.Dispose()}
}
Export-ModuleMember -Function Disable-LtImageForPublication
