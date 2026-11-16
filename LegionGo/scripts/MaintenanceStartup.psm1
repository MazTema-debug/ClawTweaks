# SPDX-License-Identifier: AGPL-3.0-only
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
Import-Module (Join-Path $PSScriptRoot 'MaintenanceBackup.psm1') -ErrorAction Stop
Import-Module (Join-Path $PSScriptRoot 'MaintenanceNative.psm1') -ErrorAction Stop

# Equivalent wire content to ControllerStartupStore.SuppressNextStartup in
# .49; this is not a reader or writer of the FPS Y3 journal.
function Set-LtStartupSuppressionCore {
    param([string]$Directory,[scriptblock]$AssertProtection)
    $dir=[IO.Path]::GetFullPath($Directory).TrimEnd('\');$path=Join-Path $dir 'controller-startup-suppress-once.json'
    $values=@(& $AssertProtection $dir)
    if($values.Count -ne 1 -or $values[0] -isnot [bool] -or -not $values[0]){throw 'Protección de inicio incierta.'}
    $expected=[Text.Encoding]::UTF8.GetBytes('{"Skip":true,"Version":1}')
    # GetFileInformationByHandle describes the target after following a link;
    # reject reparse points before opening and rely on the protected parent to
    # prevent replacement between these checks.
    $existing=$true
    try{$attributes=[IO.File]::GetAttributes($path)}
    catch [IO.FileNotFoundException] {$existing=$false}
    if($existing -and ($attributes -band ([IO.FileAttributes]::ReparsePoint -bor [IO.FileAttributes]::Directory))){throw 'Ruta de supresión no es un archivo normal.'}
    if(-not $existing) {
        $existing=$false;$temp=$path+'.'+[Guid]::NewGuid().ToString('N')+'.tmp'
        $writer=[IO.FileStream]::new($temp,[IO.FileMode]::CreateNew,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None,4096,[IO.FileOptions]::WriteThrough)
        try{
            $writer.Write($expected,0,$expected.Length);$writer.Flush($true)
            $values=@(& $AssertProtection $temp)
            if($values.Count -ne 1 -or $values[0] -isnot [bool] -or -not $values[0]){throw 'ACL temporal de supresión incierta.'}
        }finally{$writer.Dispose()}
        if(-not [LegionTweaks.MaintenanceNative.MoveApi]::Move($temp,$path,8)){throw [ComponentModel.Win32Exception]::new([Runtime.InteropServices.Marshal]::GetLastWin32Error())}
    }
    $attributes=[IO.File]::GetAttributes($path)
    if($attributes -band ([IO.FileAttributes]::ReparsePoint -bor [IO.FileAttributes]::Directory)){throw 'Ruta de supresión no es un archivo normal.'}
    $stream=[IO.FileStream]::new($path,[IO.FileMode]::Open,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None)
    try {
        $info=[LegionTweaks.MaintenanceNative.FileIdentity]::new()
        if(-not [LegionTweaks.MaintenanceNative.BackupApi]::GetFileInformationByHandle($stream.SafeFileHandle,[ref]$info) -or $info.Links -ne 1 -or ($info.IndexHigh -eq 0 -and $info.IndexLow -eq 0) -or ($info.Attributes -band [uint32][IO.FileAttributes]::ReparsePoint)){throw 'Identidad de supresión incierta.'}
        $values=@(& $AssertProtection $path)
        if($values.Count -ne 1 -or $values[0] -isnot [bool] -or -not $values[0]){throw 'ACL de supresión incierta.'}
        if($stream.Length -ne $expected.Length){throw 'Supresión existente no coincide; se conserva.'}
        $stream.Position=0
        foreach($byte in $expected){if($stream.ReadByte() -ne $byte){throw 'Supresión existente no coincide; se conserva.'}}
        $values=@(& $AssertProtection $path)
        if($values.Count -ne 1 -or $values[0] -isnot [bool] -or -not $values[0]){throw 'Protección de supresión cambió.'}
        return [pscustomobject]@{AlreadyPresent=$existing;Version=1;Skip=$true;AuthorizesMaintenance=$false}
    } finally {$stream.Dispose()}
}
function Set-LtStartupSuppression {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$ExpectedSid,[Parameter(Mandatory)][scriptblock]$AssertProtection)
    $identity=[Security.Principal.WindowsIdentity]::GetCurrent()
    try{
        if(-not $identity.User -or $identity.User.Value -ne $ExpectedSid -or -not [Security.Principal.WindowsPrincipal]::new($identity).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)){throw 'Identidad de inicio incorrecta.'}
        $directory=Join-Path (Join-Path $env:ProgramData 'LegionTweaks') $ExpectedSid
        return (Set-LtStartupSuppressionCore -Directory $directory -AssertProtection $AssertProtection)
    }finally{$identity.Dispose()}
}
function New-LtStartupAdapter {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$ExpectedSid,[Parameter(Mandatory)][scriptblock]$AssertProtection,
          [scriptblock]$Operation)
    if(-not $Operation){$entry=${function:Set-LtStartupSuppression};$Operation={param($sid,$check)& $entry -ExpectedSid $sid -AssertProtection $check}.GetNewClosure()}
    $callback={param($ctx)
        $result=@(& $Operation $ExpectedSid $AssertProtection)
        if($result.Count -ne 1 -or $result[0].AuthorizesMaintenance -ne $false -or $result[0].Version -ne 1 -or $result[0].Skip -ne $true){throw 'Supresión de inicio no confirmada.'}
        $ctx['SuppressionWasPresent']=[bool]$result[0].AlreadyPresent
        return $true
    }.GetNewClosure()
    return @{ApplyStartupSuppression=$callback}
}
Export-ModuleMember -Function Set-LtStartupSuppression,New-LtStartupAdapter
