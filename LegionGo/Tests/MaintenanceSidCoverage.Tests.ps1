# SPDX-License-Identifier: AGPL-3.0-only
$ErrorActionPreference='Stop'
Import-Module (Join-Path $PSScriptRoot '..\scripts\MaintenanceSidCoverage.psm1') -Force
$current='S-1-5-21-1-2-3-1001';$other='S-1-5-21-1-2-3-1002'
$state=[pscustomobject]@{Computer=[pscustomobject]@{PartOfDomain=$false};Accounts=@([pscustomobject]@{SID=$current},[pscustomobject]@{SID=$other});Dirs=@([pscustomobject]@{Name=$current;Attributes=[IO.FileAttributes]::Directory})}
$computer={return $state.Computer}.GetNewClosure();$accounts={return $state.Accounts}.GetNewClosure();$dirs={return $state.Dirs}.GetNewClosure()
function Assert([bool]$ok,[string]$message){if(-not $ok){throw $message}}
function Reject([scriptblock]$a){$failed=$false;try{& $a|Out-Null}catch{$failed=$true};Assert $failed 'Cobertura incierta aceptada.'}
$coverage=Get-LtSidCoverage -CurrentSid $current -ReadComputer $computer -ReadLocalAccounts $accounts -ReadDataDirectories $dirs
Assert ($coverage.FenceSids.Count -eq 2 -and $coverage.InspectionSids.Count -eq 1 -and -not $coverage.AuthorizesMaintenance) 'Cobertura local incorrecta.'
Write-Output 'PASS cuentas locales incluidas en barrera sin autorizar mantenimiento'
$state.Dirs+= [pscustomobject]@{Name='maintenance-v1';FullName='X:\fixture\maintenance-v1';Attributes=[IO.FileAttributes]::Directory}
$seen=[pscustomobject]@{Count=0;Valid=$true}
$validate={param($directory)$seen.Count++;return ($seen.Valid -and $directory.FullName -eq 'X:\fixture\maintenance-v1')}.GetNewClosure()
$coverage=Get-LtSidCoverage -CurrentSid $current -ReadComputer $computer -ReadLocalAccounts $accounts -ReadDataDirectories $dirs -ValidateMaintenanceDirectory $validate
Assert ($coverage.FenceSids.Count -eq 2 -and $coverage.InspectionSids.Count -eq 1 -and $seen.Count -eq 1) 'Carpeta administrativa no fue verificada.'
$seen.Valid=$false
Reject {Get-LtSidCoverage -CurrentSid $current -ReadComputer $computer -ReadLocalAccounts $accounts -ReadDataDirectories $dirs -ValidateMaintenanceDirectory $validate}
$seen.Valid=$true;$state.Dirs+= [pscustomobject]@{Name='unknown-folder';Attributes=[IO.FileAttributes]::Directory}
Reject {Get-LtSidCoverage -CurrentSid $current -ReadComputer $computer -ReadLocalAccounts $accounts -ReadDataDirectories $dirs -ValidateMaintenanceDirectory $validate}
$state.Dirs=@([pscustomobject]@{Name=$current;Attributes=[IO.FileAttributes]::Directory},[pscustomobject]@{Name='maintenance-v1';FullName='X:\fixture\maintenance-v1';Attributes=[IO.FileAttributes]::ReparsePoint})
Reject {Get-LtSidCoverage -CurrentSid $current -ReadComputer $computer -ReadLocalAccounts $accounts -ReadDataDirectories $dirs -ValidateMaintenanceDirectory $validate}
Write-Output 'PASS carpeta administrativa verificada; ruta incierta, desconocida y enlace bloquean'
$state.Dirs=@([pscustomobject]@{Name=$current;Attributes=[IO.FileAttributes]::Directory})
$state.Dirs+= [pscustomobject]@{Name=$other;Attributes=[IO.FileAttributes]::Directory}
Reject {Get-LtSidCoverage -CurrentSid $current -ReadComputer $computer -ReadLocalAccounts $accounts -ReadDataDirectories $dirs}
Write-Output 'PASS datos de otro SID bloquean sin broker'
$state.Dirs=@();$state.Computer=[pscustomobject]@{PartOfDomain=$true}
Reject {Get-LtSidCoverage -CurrentSid $current -ReadComputer $computer -ReadLocalAccounts $accounts -ReadDataDirectories $dirs}
Write-Output 'PASS dominio fuera de alcance bloqueado'
$state.Computer=[pscustomobject]@{PartOfDomain=$false};$state.Accounts=@()
Reject {Get-LtSidCoverage -CurrentSid $current -ReadComputer $computer -ReadLocalAccounts $accounts -ReadDataDirectories $dirs}
Write-Output 'PASS inventario de cuentas vacío bloqueado'
