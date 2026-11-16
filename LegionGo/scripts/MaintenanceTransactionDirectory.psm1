# SPDX-License-Identifier: AGPL-3.0-only
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
Import-Module (Join-Path $PSScriptRoot 'MaintenanceJournal.psm1') -ErrorAction Stop
function Assert-LtTransactionProtection([scriptblock]$Protect,[string]$Path){
    $r=@(& $Protect $Path);if($r.Count -ne 1 -or $r[0] -isnot [bool] -or -not $r[0]){throw 'Directorio de transacciones no protegido.'}
}
function Test-LtPriorTransactions {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Directory,[Parameter(Mandatory)][scriptblock]$AssertProtection)
    $parent=[IO.Path]::GetFullPath($Directory).TrimEnd('\');Assert-LtTransactionProtection $AssertProtection $parent
    $item=Get-Item -LiteralPath $parent -Force -ErrorAction Stop
    if(-not $item.PSIsContainer -or ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)){throw 'Directorio de transacciones inseguro.'}
    foreach($child in Get-ChildItem -LiteralPath $parent -Force -ErrorAction Stop){
        if(-not $child.PSIsContainer -or ($child.Attributes -band [IO.FileAttributes]::ReparsePoint) -or $child.Name -cnotmatch '^[a-f0-9]{32}$'){throw 'Entrada de transacción no reconocida.'}
        $journal=Join-Path $child.FullName 'flow-journal.json'
        if(-not [IO.File]::Exists($journal)){throw 'Transacción previa incompleta requiere revisión.'}
        $record=Read-LtTransactionJournal -Path $journal -AssertProtection $AssertProtection
        if($record.Id -cne $child.Name -or $record.Stage -notin @('committed','compensated')){throw 'Transacción previa pendiente requiere revisión.'}
    }
    return $true
}
function New-LtTransactionDirectory {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Directory,[Parameter(Mandatory)][string]$Id,[Parameter(Mandatory)][scriptblock]$AssertProtection,[scriptblock]$Create)
    if($Id -cnotmatch '^[a-f0-9]{32}$'){throw 'Id de transacción inválido.'}
    $parent=[IO.Path]::GetFullPath($Directory).TrimEnd('\');$target=Join-Path $parent $Id
    Assert-LtTransactionProtection $AssertProtection $parent
    $item=Get-Item -LiteralPath $parent -Force -ErrorAction Stop
    if(-not $item.PSIsContainer -or ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -or [IO.Directory]::Exists($target) -or [IO.File]::Exists($target)){throw 'Destino de transacción inseguro o existente.'}
    if(-not $Create){$Create={param($path)
        $security=[Security.AccessControl.DirectorySecurity]::new();$security.SetAccessRuleProtection($true,$false)
        $admins=[Security.Principal.SecurityIdentifier]::new('S-1-5-32-544');$security.SetOwner($admins)
        foreach($sid in @('S-1-5-18','S-1-5-32-544')){
            $security.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new([Security.Principal.SecurityIdentifier]::new($sid),'FullControl','ContainerInherit,ObjectInherit','None','Allow'))
        }
        $null=[IO.Directory]::CreateDirectory($path,$security)
    }}
    & $Create $target
    $created=Get-Item -LiteralPath $target -Force -ErrorAction Stop
    if(-not $created.PSIsContainer -or ($created.Attributes -band [IO.FileAttributes]::ReparsePoint)){throw 'No se creó un directorio de transacción normal.'}
    Assert-LtTransactionProtection $AssertProtection $target
    return $target
}
Export-ModuleMember -Function Test-LtPriorTransactions,New-LtTransactionDirectory
