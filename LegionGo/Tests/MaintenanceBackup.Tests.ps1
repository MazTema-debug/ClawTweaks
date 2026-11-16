# SPDX-License-Identifier: AGPL-3.0-only
$ErrorActionPreference='Stop'
Import-Module (Join-Path $PSScriptRoot '..\scripts\MaintenanceBackup.psm1') -Force
$root=Join-Path ([IO.Path]::GetTempPath()) ('LegionTweaks-backup-fixture-'+[Guid]::NewGuid().ToString('N'))
$src=Join-Path $root 'source';$dst=Join-Path $root 'backup'
$null=New-Item -ItemType Directory -Path (Join-Path $src 'nested') -Force
[IO.File]::WriteAllText((Join-Path $src 'nested\original.json'),'fixture secret')
$before=(Get-FileHash -LiteralPath (Join-Path $src 'nested\original.json') -Algorithm SHA256).Hash
$entries=@(Copy-LtProtectedBackup -SourceRoot $src -DestinationRoot $dst -RelativeFiles @('nested\original.json') -AssertProtection {param($a,$b) return $true})
if($entries.Count -ne 1 -or $entries[0].Sha256 -ne $before -or (Get-FileHash -LiteralPath (Join-Path $dst 'nested\original.json') -Algorithm SHA256).Hash -ne $before -or (Get-FileHash -LiteralPath (Join-Path $src 'nested\original.json') -Algorithm SHA256).Hash -ne $before -or (Get-Acl -LiteralPath (Join-Path $dst 'nested\original.json')).Sddl -ne $entries[0].Sddl){throw 'Respaldo no coincidió.'}
Write-Output 'PASS respaldo de fixture preserva hash e incluye ACL'
$rejected=$false
try {Copy-LtProtectedBackup -SourceRoot $src -DestinationRoot (Join-Path $root 'blocked') -RelativeFiles @('nested\original.json') -AssertProtection {param($a,$b) return $false} | Out-Null}catch{$rejected=$true}
if(-not $rejected -or (Test-Path -LiteralPath (Join-Path $root 'blocked'))){throw 'Protección rechazada creó respaldo.'}
Write-Output 'PASS protección incierta bloquea antes de crear respaldo'
$rejected=$false
try {Copy-LtProtectedBackup -SourceRoot $src -DestinationRoot (Join-Path $root 'ambiguous') -RelativeFiles @('nested\original.json') -AssertProtection {param($a,$b) return $true,$false} | Out-Null}catch{$rejected=$true}
if(-not $rejected -or (Test-Path -LiteralPath (Join-Path $root 'ambiguous'))){throw 'Resultado ambiguo de protección fue aceptado.'}
Write-Output 'PASS protección con respuestas ambiguas bloqueada'
$rejected=$false
try {Copy-LtProtectedBackup -SourceRoot $src -DestinationRoot (Join-Path $root 'escape') -RelativeFiles @('..\outside') -AssertProtection {param($a,$b) return $true} | Out-Null}catch{$rejected=$true}
if(-not $rejected){throw 'Se aceptó una ruta fuera de origen.'}
Write-Output 'PASS ruta escapada bloqueada'
