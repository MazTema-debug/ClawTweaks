# SPDX-License-Identifier: AGPL-3.0-only
$ErrorActionPreference='Stop'
Import-Module (Join-Path $PSScriptRoot '..\scripts\MaintenanceHardware.psm1') -Force
$state=[pscustomobject]@{Product=[pscustomobject]@{Vendor='LENOVO';Name='83E1';Version='Legion Go 8APU1'};Cpu=[pscustomobject]@{Name='AMD Ryzen Z1 Extreme'}}
$product={return $state.Product}.GetNewClosure();$cpu={return $state.Cpu}.GetNewClosure()
if(-not (Assert-LtSupportedHardware -ReadProduct $product -ReadCpu $cpu)){throw 'Hardware de fixture válido rechazado.'}
$state.Cpu=[pscustomobject]@{Name='AMD Ryzen Z1'};$blocked=$false
try{Assert-LtSupportedHardware -ReadProduct $product -ReadCpu $cpu|Out-Null}catch{$blocked=$true}
if(-not $blocked){throw 'CPU no Extreme aceptada.'}
$state.Cpu=[pscustomobject]@{Name='AMD Ryzen Z1 Extreme'};$state.Product=[pscustomobject]@{Vendor='OTHER';Name='83E1';Version='Legion Go 8APU1'};$blocked=$false
try{Assert-LtSupportedHardware -ReadProduct $product -ReadCpu $cpu|Out-Null}catch{$blocked=$true}
if(-not $blocked){throw 'Fabricante incorrecto aceptado.'}
Write-Output 'PASS modelo y CPU se validan por separado sin identificadores USB'
