# SPDX-License-Identifier: AGPL-3.0-only
$ErrorActionPreference='Stop'
Import-Module (Join-Path $PSScriptRoot '..\scripts\MaintenanceNative.psm1') -Force
$root=Join-Path ([IO.Path]::GetTempPath()) ('LegionTweaks-native-fixture-'+[Guid]::NewGuid().ToString('N'))
$null=New-Item -ItemType Directory -Path $root
function Assert([bool]$condition,[string]$message){if(-not $condition){throw $message}}
function Reject([scriptblock]$action){$failed=$false;try{& $action|Out-Null}catch{$failed=$true};Assert $failed 'Se aceptó una operación inválida.'}
$checkpoint=Join-Path $root 'journal.json'
Assert (Write-LtCheckpointWriteThrough -Path $checkpoint -Content '{"stage":1}') 'No se creó checkpoint.'
Assert (Write-LtCheckpointWriteThrough -Path $checkpoint -Content '{"stage":2}') 'No se reemplazó checkpoint.'
Assert ([IO.File]::ReadAllText($checkpoint) -eq '{"stage":2}') 'Contenido de checkpoint incorrecto.'
Write-Output 'PASS checkpoint write-through y reemplazo de fixture'
$a=Join-Path $root 'a';$b=Join-Path $root 'b';$c=Join-Path $root 'c'
$null=New-Item -ItemType Directory -Path $a;$null=New-Item -ItemType Directory -Path $c
[IO.File]::WriteAllText((Join-Path $a 'content.txt'),'fixture')
Reject {Move-LtDirectorySameParent -Source $a -Destination $c}
Assert ([IO.Directory]::Exists($a) -and [IO.Directory]::Exists($c)) 'El rechazo modificó árboles.'
Assert (Move-LtDirectorySameParent -Source $a -Destination $b) 'No se realizó el rename.'
Assert (-not [IO.Directory]::Exists($a) -and [IO.File]::ReadAllText((Join-Path $b 'content.txt')) -eq 'fixture') 'Árbol publicado incorrecto.'
Write-Output 'PASS rename nativo en mismo padre y rechazo de destino existente'
Reject {Move-LtDirectorySameParent -Source $b -Destination (Join-Path $c 'otro')}
Write-Output 'PASS rechazo de destinos en otro directorio'
