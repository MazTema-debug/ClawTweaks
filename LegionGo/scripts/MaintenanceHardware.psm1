# SPDX-License-Identifier: AGPL-3.0-only
Set-StrictMode -Version Latest
function Assert-LtSupportedHardware {
    [CmdletBinding()]
    param([scriptblock]$ReadProduct,[scriptblock]$ReadCpu)
    if(-not $ReadProduct){$ReadProduct={@(Get-CimInstance -ClassName Win32_ComputerSystemProduct -ErrorAction Stop)}}
    if(-not $ReadCpu){$ReadCpu={@(Get-CimInstance -ClassName Win32_Processor -ErrorAction Stop)}}
    $products=@(& $ReadProduct);$cpus=@(& $ReadCpu)
    if($products.Count -ne 1 -or $cpus.Count -ne 1 -or -not $products[0].Vendor -or -not $products[0].Name -or -not $cpus[0].Name){throw 'Identidad de hardware incompleta.'}
    $vendor=([string]$products[0].Vendor).Trim();$model=([string]$products[0].Name).Trim();$version=([string]$products[0].Version).Trim();$cpu=([string]$cpus[0].Name).Trim()
    if($vendor -ine 'LENOVO' -or ($model -inotlike '83E1*' -and $version -ine 'Legion Go 8APU1') -or $cpu -inotmatch 'AMD.*Z1 Extreme'){throw 'Solo se admite Legion Go 8APU1/83E1 con AMD Ryzen Z1 Extreme.'}
    return $true
}
Export-ModuleMember -Function Assert-LtSupportedHardware
