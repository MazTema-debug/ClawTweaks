# SPDX-License-Identifier: AGPL-3.0-only
$ErrorActionPreference='Stop'
Import-Module (Join-Path $PSScriptRoot '..\scripts\MaintenancePayload.psm1') -Force
$root=Join-Path ([IO.Path]::GetTempPath()) ('LegionTweaks-payload-fixture-'+[Guid]::NewGuid().ToString('N'))
$source=Join-Path $root 'package';$tree=Join-Path $root 'destination'
$null=New-Item -ItemType Directory -Path (Join-Path $source 'Helper\Qam') -Force
[IO.File]::WriteAllText((Join-Path $source 'Helper\LegionTweaks.Helper.exe'),'fixture helper')
[IO.File]::WriteAllText((Join-Path $source 'Helper\Qam\LegionTweaks.QamProbe.exe'),'fixture qam')
[IO.File]::WriteAllText((Join-Path $source 'Uninstall-LegionGo.ps1'),'fixture historical script')
$family='MazTema.LegionTweaks_wsckx05r95yk0';$signer='A'*40
$check={param($p)return $true}
function Assert([bool]$ok,[string]$message){if(-not $ok){throw $message}}
function Reject([scriptblock]$a){$failed=$false;try{& $a|Out-Null}catch{$failed=$true};Assert $failed 'Payload alterado aceptado.'}
Assert (New-LtDestinationTree -Extracted $source -Tree $tree -Family $family -Signer $signer -AssertPackage $check -AssertProtection $check -ApplyAndValidateAcl $check) 'No se armó el árbol.'
Assert ((Get-Content -LiteralPath (Join-Path $tree 'allowed-family.txt') -Raw).Trim() -eq $family -and (Get-Content -LiteralPath (Join-Path $tree 'certificate-thumbprint.txt') -Raw).Trim() -eq $signer -and (Test-Path -LiteralPath (Join-Path $tree 'Uninstall-LegionGo.ps1'))) 'Faltan archivos de control.'
Write-Output 'PASS árbol de destino incluye helper, QAM y archivos de control'
[IO.File]::WriteAllText((Join-Path $tree 'Qam\LegionTweaks.QamProbe.exe'),'alterado')
Reject {Test-LtDestinationTree -Extracted $source -Tree $tree -Family $family -Signer $signer -AssertProtection $check}
Write-Output 'PASS payload modificado se rechaza'
