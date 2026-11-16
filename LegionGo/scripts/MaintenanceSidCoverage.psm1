# SPDX-License-Identifier: AGPL-3.0-only
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
Import-Module (Join-Path $PSScriptRoot 'MaintenanceProtection.psm1') -ErrorAction Stop

# Restrictive initial scope: standalone Windows with local accounts. This is
# only an inventory component; it is not proof that no other launch path exists.
function Get-LtSidCoverage {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$CurrentSid,
          [AllowEmptyCollection()][object[]]$Processes=@(),
          [AllowEmptyCollection()][string[]]$TaskSids=@(),
          [scriptblock]$ReadComputer,[scriptblock]$ReadLocalAccounts,[scriptblock]$ReadDataDirectories,[scriptblock]$ValidateMaintenanceDirectory)
    if(-not $ReadComputer){$ReadComputer={Get-CimInstance -ClassName Win32_ComputerSystem -ErrorAction Stop}}
    if(-not $ReadLocalAccounts){$ReadLocalAccounts={Get-CimInstance -ClassName Win32_UserAccount -Filter 'LocalAccount=True' -ErrorAction Stop}}
    if(-not $ReadDataDirectories){$ReadDataDirectories={
        $root=Join-Path $env:ProgramData 'LegionTweaks'
        $item=Get-Item -LiteralPath $root -Force -ErrorAction Stop
        if(-not $item.PSIsContainer -or ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)){throw 'Raíz de datos incierta.'}
        Get-ChildItem -LiteralPath $root -Force -Directory -ErrorAction Stop
    }}
    if(-not $ValidateMaintenanceDirectory){
        $snapshot=Get-Command Get-LtProtectedSnapshot -CommandType Function -ErrorAction Stop
        $ValidateMaintenanceDirectory={param($directory)
            $expected=Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::CommonApplicationData)) 'LegionTweaks\maintenance-v1'
            if(-not $directory.FullName -or -not [string]::Equals([IO.Path]::GetFullPath([string]$directory.FullName).TrimEnd('\'),[IO.Path]::GetFullPath($expected).TrimEnd('\'),[StringComparison]::OrdinalIgnoreCase)){throw 'Ruta administrativa no coincide.'}
            $result=& $snapshot -Roots @($expected) -WithoutDescendants
            return ($result.AuthorizesMaintenance -eq $false -and $result.Items -gt 0)
        }.GetNewClosure()
    }
    $computer=& $ReadComputer
    if(-not $computer -or $computer.PartOfDomain -ne $false){throw 'Solo se admite cobertura local sin dominio.'}
    $all=[Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    $inspect=[Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    function Add-Sid([Collections.Generic.HashSet[string]]$set,[string]$value){
        try{$sid=[Security.Principal.SecurityIdentifier]::new($value).Value}catch{throw 'SID de inventario inválido.'}
        if($sid -ne $value){throw 'SID de inventario no canónico.'};$null=$set.Add($sid)
    }
    Add-Sid $all $CurrentSid;Add-Sid $inspect $CurrentSid
    $accounts=@(& $ReadLocalAccounts)
    if($accounts.Count -eq 0){throw 'Inventario local de cuentas vacío o incompleto.'}
    foreach($account in $accounts){if(-not $account.SID){throw 'Cuenta local sin SID.'};Add-Sid $all ([string]$account.SID)}
    foreach($directory in @(& $ReadDataDirectories)){
        if(-not $directory.Name -or ($directory.Attributes -band [IO.FileAttributes]::ReparsePoint)){throw 'Directorio de datos incierto.'}
        if([string]::Equals([string]$directory.Name,'maintenance-v1',[StringComparison]::OrdinalIgnoreCase)){
            $checked=@(& $ValidateMaintenanceDirectory $directory)
            if($checked.Count -ne 1 -or $checked[0] -isnot [bool] -or -not $checked[0]){throw 'Directorio administrativo no protegido.'}
            continue
        }
        Add-Sid $all ([string]$directory.Name);Add-Sid $inspect ([string]$directory.Name)
    }
    foreach($p in $Processes){if(-not $p.Sid){throw 'Proceso sin SID.'};Add-Sid $all ([string]$p.Sid);Add-Sid $inspect ([string]$p.Sid)}
    foreach($sid in $TaskSids){Add-Sid $all $sid;Add-Sid $inspect $sid}
    if($all.Count -gt 64){throw 'Demasiadas identidades para la barrera legada.'}
    $others=@($inspect | Where-Object {$_ -ne $CurrentSid})
    if($others.Count){throw 'Hay otra identidad con datos o procesos: falta un broker de inspección por identidad.'}
    return [pscustomobject]@{FenceSids=@($all | Sort-Object);InspectionSids=@($inspect | Sort-Object);AuthorizesMaintenance=$false}
}
Export-ModuleMember -Function Get-LtSidCoverage
