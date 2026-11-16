# SPDX-License-Identifier: AGPL-3.0-only
. (Join-Path $PSScriptRoot 'MaintenancePayload.Tests.ps1')
Import-Module (Join-Path $PSScriptRoot '..\scripts\MaintenanceProductionStage.psm1') -Force
$target=@{Archive=(Join-Path $root 'archive.zip');Extracted=$source;ArchiveSha256=('A'*64);HelperPath=(Join-Path $source 'Helper\LegionTweaks.Helper.exe');HelperSha256=('B'*64);QamPath=(Join-Path $source 'Helper\Qam\LegionTweaks.QamProbe.exe');QamSha256=('C'*64);WidgetPath=(Join-Path $source 'Widget\fixture.msix');Signer=$signer}
$state=[pscustomobject]@{PackageChecks=0;Allow=$true}
$operations=@{Package={param($t)$state.PackageChecks++;return $state.Allow}.GetNewClosure();ApplyAcl={param($p)return $true}}
$staging=Join-Path $root 'staging'
if(-not (New-LtPreparedTarget -Target $target -Staging $staging -AssertProtection $check -Operations $operations) -or $state.PackageChecks -ne 2 -or -not (Test-Path -LiteralPath (Join-Path $staging 'Uninstall-LegionGo.ps1'))){throw 'Staging de fixture incompleto.'}
Write-Output 'PASS staging verifica el paquete antes y después de crear árbol completo'
$state.Allow=$false;$bad=Join-Path $root 'bad-staging';$blocked=$false
try{New-LtPreparedTarget -Target $target -Staging $bad -AssertProtection $check -Operations $operations|Out-Null}catch{$blocked=$true}
if(-not $blocked -or (Test-Path -LiteralPath $bad)){throw 'Staging inseguro llegó a crearse.'}
Write-Output 'PASS staging bloquea paquete no verificado antes de crear árbol'
