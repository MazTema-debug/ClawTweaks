# SPDX-License-Identifier: AGPL-3.0-only
$ErrorActionPreference='Stop'
Import-Module (Join-Path $PSScriptRoot '..\scripts\MaintenancePackage.psm1') -Force
Add-Type -AssemblyName System.IO.Compression.FileSystem
$base=Join-Path ([IO.Path]::GetTempPath()) ('LegionTweaks-package-fixture-'+[Guid]::NewGuid().ToString('N'))
$source=Join-Path $base 'src';$extracted=Join-Path $base 'extracted';$archive=Join-Path $base 'fixture.zip'
$null=New-Item -ItemType Directory -Path (Join-Path $source 'Helper') -Force
[IO.File]::WriteAllText((Join-Path $source 'Helper\example.txt'),'fixture-original')
$hash=(Get-FileHash -Algorithm SHA256 -LiteralPath (Join-Path $source 'Helper\example.txt')).Hash
[IO.File]::WriteAllText((Join-Path $source 'SHA256SUMS.txt'),($hash+'  Helper\example.txt'+[Environment]::NewLine))
[IO.Compression.ZipFile]::CreateFromDirectory($source,$archive)
[IO.Compression.ZipFile]::ExtractToDirectory($archive,$extracted)
$zipHash=(Get-FileHash -Algorithm SHA256 -LiteralPath $archive).Hash
function Assert([bool]$condition,[string]$message){if(-not $condition){throw $message}}
function Reject([scriptblock]$action){$failed=$false;try{& $action | Out-Null}catch{$failed=$true};Assert $failed 'El verificador aceptó un paquete alterado.'}
$result=Test-LtPinnedPackage -Archive $archive -Extracted $extracted -ExpectedSha256 $zipHash
Assert ($result.InternalFiles -eq 1 -and -not $result.AuthorizesMaintenance) 'Resultado válido incorrecto.'
Write-Output 'PASS paquete íntegro no autoriza mantenimiento'
Reject {Test-LtPinnedPackage -Archive $archive -Extracted $extracted -ExpectedSha256 ('0'*64)}
Write-Output 'PASS hash externo incorrecto rechazado'
[IO.File]::WriteAllText((Join-Path $extracted 'Helper\example.txt'),'altered')
Reject {Test-LtPinnedPackage -Archive $archive -Extracted $extracted -ExpectedSha256 $zipHash}
Write-Output 'PASS contenido extraído alterado rechazado'
Copy-Item -LiteralPath (Join-Path $source 'Helper\example.txt') -Destination (Join-Path $extracted 'Helper\example.txt') -Force
[IO.File]::WriteAllText((Join-Path $extracted 'extra.txt'),'unexpected')
Reject {Test-LtPinnedPackage -Archive $archive -Extracted $extracted -ExpectedSha256 $zipHash}
Write-Output 'PASS archivo extra rechazado'
Remove-Item -LiteralPath (Join-Path $extracted 'extra.txt')
[IO.File]::AppendAllText((Join-Path $extracted 'SHA256SUMS.txt'),'tamper')
Reject {Test-LtPinnedPackage -Archive $archive -Extracted $extracted -ExpectedSha256 $zipHash}
Write-Output 'PASS manifiesto extraído alterado rechazado'
