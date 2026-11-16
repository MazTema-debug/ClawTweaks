# SPDX-License-Identifier: AGPL-3.0-only
$ErrorActionPreference='Stop'
Import-Module (Join-Path $PSScriptRoot '..\scripts\MaintenanceInventory.psm1') -Force
$helper='X:\Fixture\LegionTweaks.Helper.exe';$qam='X:\Fixture\Qam\LegionTweaks.QamProbe.exe'
$script:items=@();$script:identity=[pscustomobject]@{Sid='S-1-5-21-1-2-3-1001';StartUtcTicks=100}
$list={return $script:items};$identify={param($p)return $script:identity}
function Assert([bool]$condition,[string]$message){if(-not $condition){throw $message}}
function Reject([scriptblock]$action){$failed=$false;try{& $action|Out-Null}catch{$failed=$true};Assert $failed 'Se aceptó un inventario incierto.'}
function P($path,$arguments){[pscustomobject]@{Name=[IO.Path]::GetFileName($path);ExecutablePath=$path;CommandLine=('"'+$path+'" '+$arguments);ProcessId=123;SessionId=2;CreationDate=[DateTime]::new(100,[DateTimeKind]::Utc)}}
$script:items=@((P $helper ''),(P $helper '--fps-y3-io verify-ready synthetic'),(P $qam ''))
$inventory=@(Get-LtProcessInventory -ExpectedPaths @($helper,$qam) -ListProcesses $list -ReadIdentity $identify)
Assert ($inventory.Count -eq 3 -and $inventory[0].Role -eq 'main' -and $inventory[1].Role -eq 'fps-y3-io' -and $inventory[2].Role -eq 'interface') 'Roles no reconocidos.'
Reject {Assert-LtInventoryQuiescent -Inventory $inventory}
Write-Output 'PASS principal, worker y QAM inventariados y no quiescentes'
$widget='X:\WindowsApps\LegionTweaks.Widget.exe'
$script:items=@((P $widget ''))
$inventory=@(Get-LtProcessInventory -ExpectedPaths @($helper,$qam,$widget) -ListProcesses $list -ReadIdentity $identify)
Assert ($inventory.Count -eq 1 -and $inventory[0].Role -eq 'interface') 'Vista activa no fue identificada.'
Reject {Assert-LtInventoryQuiescent -Inventory $inventory}
Write-Output 'PASS vista widget activa detiene quiescencia'
$script:items=@();Assert (Assert-LtInventoryQuiescent -Inventory @(Get-LtProcessInventory -ExpectedPaths @($helper,$qam) -ListProcesses $list -ReadIdentity $identify)) 'Inventario vacío rechazado.'
Write-Output 'PASS inventario vacío en fixture'
$script:items=@((P $helper '--unknown-mode'))
Reject {Get-LtProcessInventory -ExpectedPaths @($helper,$qam) -ListProcesses $list -ReadIdentity $identify}
Write-Output 'PASS rol desconocido bloquea'
$script:items=@([pscustomobject]@{Name='LegionTweaks.Helper.exe';ExecutablePath=$null;CommandLine=$null;ProcessId=123;SessionId=2;CreationDate=[DateTime]::new(100,[DateTimeKind]::Utc)})
Reject {Get-LtProcessInventory -ExpectedPaths @($helper,$qam) -ListProcesses $list -ReadIdentity $identify}
Write-Output 'PASS ruta inaccesible bloquea'
$script:items=@((P 'X:\Other\LegionTweaks.Helper.exe' ''))
Reject {Get-LtProcessInventory -ExpectedPaths @($helper,$qam) -ListProcesses $list -ReadIdentity $identify}
Write-Output 'PASS copia no identificada bloquea'
$script:items=@((P $helper ''));$script:identity=[pscustomobject]@{Sid='S-1-5-21-1-2-3-1001';StartUtcTicks=0}
Reject {Get-LtProcessInventory -ExpectedPaths @($helper,$qam) -ListProcesses $list -ReadIdentity $identify}
Write-Output 'PASS hora de creación incierta bloquea'
$script:identity=[pscustomobject]@{Sid='S-1-5-21-1-2-3-1001';StartUtcTicks=1000}
Reject {Get-LtProcessInventory -ExpectedPaths @($helper,$qam) -ListProcesses $list -ReadIdentity $identify}
Write-Output 'PASS PID con fecha de creación diferente bloquea'
