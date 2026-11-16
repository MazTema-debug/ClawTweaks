# SPDX-License-Identifier: AGPL-3.0-only
$ErrorActionPreference='Stop'
Import-Module (Join-Path $PSScriptRoot '..\scripts\MaintenanceBackupSet.psm1') -Force
$root=Join-Path ([IO.Path]::GetTempPath()) ('LegionTweaks-backupset-fixture-'+[Guid]::NewGuid().ToString('N'))
$first=Join-Path $root 'first';$second=Join-Path $root 'second';$destination=Join-Path $root 'backup'
$null=New-Item -ItemType Directory -Path (Join-Path $first 'empty') -Force
$null=New-Item -ItemType Directory -Path $second -Force
[IO.File]::WriteAllText((Join-Path $first 'file.txt'),'first fixture');[IO.File]::WriteAllText((Join-Path $second 'file.txt'),'second fixture')
$groups=@([pscustomobject]@{Name='helper';SourceRoot=$first},[pscustomobject]@{Name='data';SourceRoot=$second})
$meta=@{TaskXml='<fixture/>';TaskWasEnabled=$true;PackageFullName='fixture';TargetCertificateWasPresent=$false;SourceCertificateWasPresent=$true}
$result=New-LtBackupSet -Destination $destination -Groups $groups -Metadata $meta -AssertProtection {param($a,$b,$c)return $true}
if($result.Groups -ne 2 -or $result.AuthorizesMaintenance -or -not (Test-Path -LiteralPath (Join-Path $destination 'helper\empty')) -or (Get-FileHash -LiteralPath $result.ManifestPath -Algorithm SHA256).Hash -ne $result.ManifestSha256){throw 'Conjunto de respaldo incompleto.'}
Write-Output 'PASS conjunto de respaldo conserva archivos, carpetas vacías y manifiesto'
$verified=Test-LtBackupSet -Destination $destination -ExpectedManifestSha256 $result.ManifestSha256 -AssertProtection {param($a)return $true}
if($verified.Groups -ne 2 -or $verified.AuthorizesMaintenance){throw 'Verificación de respaldo incorrecta.'}
Write-Output 'PASS conjunto fijado se verifica sin autorizar mantenimiento'
$restore=Join-Path $root 'restore-tree'
if(-not (New-LtRestoreTree -BackupDestination $destination -ExpectedManifestSha256 $result.ManifestSha256 -RestoreTree $restore -AssertProtection {param($a,$b,$c)return $true}) -or [IO.File]::ReadAllText((Join-Path $restore 'file.txt')) -cne 'first fixture' -or -not (Test-Path -LiteralPath (Join-Path $restore 'empty'))){throw 'Árbol de retorno no corresponde al respaldo.'}
if(-not (Test-LtRestoreTree -BackupDestination $destination -ExpectedManifestSha256 $result.ManifestSha256 -RestoreTree $restore -AssertProtection {param($a)return $true})){throw 'Retorno intacto no verificado.'}
[IO.File]::AppendAllText((Join-Path $restore 'file.txt'),'altered');$blocked=$false
try{Test-LtRestoreTree -BackupDestination $destination -ExpectedManifestSha256 $result.ManifestSha256 -RestoreTree $restore -AssertProtection {param($a)return $true}|Out-Null}catch{$blocked=$true}
if(-not $blocked){throw 'Árbol de retorno alterado aceptado.'}
Write-Output 'PASS árbol de retorno separado conserva archivos, directorios y ACL'
[IO.File]::AppendAllText((Join-Path $destination 'helper\file.txt'),'tamper');$blocked=$false
try{Test-LtBackupSet -Destination $destination -ExpectedManifestSha256 $result.ManifestSha256 -AssertProtection {param($a)return $true}|Out-Null}catch{$blocked=$true}
if(-not $blocked){throw 'Respaldo alterado aceptado.'}
Write-Output 'PASS archivo respaldado alterado se rechaza'
$blocked=$false
try{New-LtBackupSet -Destination (Join-Path $root 'bad') -Groups $groups -Metadata @{} -AssertProtection {param($a)return $true}|Out-Null}catch{$blocked=$true}
if(-not $blocked -or (Test-Path -LiteralPath (Join-Path $root 'bad'))){throw 'Metadatos incompletos crearon respaldo.'}
Write-Output 'PASS metadatos incompletos bloquean antes de crear respaldo'
$y3root=Join-Path $root 'user-data';$null=New-Item -ItemType Directory -Path $y3root
$marker=Join-Path $y3root 'fps-y3-admission-v1.guard';$diary=Join-Path $y3root 'fps-y3-trial.json';$original=Join-Path $y3root 'original.fixture'
[IO.File]::WriteAllText($diary,'Restored fixture');[IO.File]::WriteAllText($original,'original fixture')
$locked=[IO.FileStream]::new($marker,[IO.FileMode]::CreateNew,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None)
try{
    $beforeDiary=(Get-FileHash -LiteralPath $diary -Algorithm SHA256).Hash;$beforeOriginal=(Get-FileHash -LiteralPath $original -Algorithm SHA256).Hash
    $y3groups=@([pscustomobject]@{Name='user-data';SourceRoot=$y3root;ExcludedFiles=@('fps-y3-admission-v1.guard')})
    $destination2=Join-Path $root 'y3-backup'
    $saved=New-LtBackupSet -Destination $destination2 -Groups $y3groups -Metadata $meta -AssertProtection {param($a,$b,$c)return $true}
    $null=Test-LtBackupSet -Destination $destination2 -ExpectedManifestSha256 $saved.ManifestSha256 -AssertProtection {param($a)return $true}
    if((Test-Path -LiteralPath (Join-Path $destination2 'user-data\fps-y3-admission-v1.guard')) -or -not (Test-Path -LiteralPath $marker) -or (Get-FileHash -LiteralPath $diary -Algorithm SHA256).Hash -ne $beforeDiary -or (Get-FileHash -LiteralPath $original -Algorithm SHA256).Hash -ne $beforeOriginal -or (Get-FileHash -LiteralPath (Join-Path $destination2 'user-data\fps-y3-trial.json') -Algorithm SHA256).Hash -ne $beforeDiary -or (Get-FileHash -LiteralPath (Join-Path $destination2 'user-data\original.fixture') -Algorithm SHA256).Hash -ne $beforeOriginal){throw 'Respaldo Y3 no conservó originales y marcador.'}
    Write-Output 'PASS reserva propia bloqueada se excluye y diario y original se respaldan íntegros'
    $badGroups=@([pscustomobject]@{Name='user-data';SourceRoot=$y3root;ExcludedFiles=@('fps-y3-trial.json')});$blocked=$false
    try{New-LtBackupSet -Destination (Join-Path $root 'bad-y3') -Groups $badGroups -Metadata $meta -AssertProtection {param($a,$b,$c)return $true}|Out-Null}catch{$blocked=$true}
    if(-not $blocked -or (Test-Path -LiteralPath (Join-Path $root 'bad-y3'))){throw 'Diario pudo excluirse del respaldo.'}
    Write-Output 'PASS diario Y3 no se puede excluir del respaldo'
}finally{$locked.Dispose()}
$settings=Join-Path $root 'settings';$null=New-Item -ItemType Directory -Path $settings
$settingsFile=Join-Path $settings 'settings.dat';[IO.File]::WriteAllText($settingsFile,'settings fixture')
$settingsSddl=(Get-Acl -LiteralPath $settingsFile).Sddl
$settingsGroups=@([pscustomobject]@{Name='widget-settings';SourceRoot=$settings;ProtectedCopy=$true})
$settingsBackup=Join-Path $root 'settings-backup'
$saved=New-LtBackupSet -Destination $settingsBackup -Groups $settingsGroups -Metadata $meta -AssertProtection {param($a,$b,$c)return $true}
$null=Test-LtBackupSet -Destination $settingsBackup -ExpectedManifestSha256 $saved.ManifestSha256 -AssertProtection {param($a)return $true}
$settingsManifest=Get-Content -LiteralPath $saved.ManifestPath -Raw -Encoding UTF8|ConvertFrom-Json
if(-not $settingsManifest.Groups[0].ProtectedCopy -or $settingsManifest.Groups[0].Files[0].Sddl -cne $settingsSddl -or [IO.File]::ReadAllText((Join-Path $settingsBackup 'widget-settings\settings.dat')) -cne 'settings fixture'){throw 'Respaldo protegido no guardó contenido y ACL original.'}
Write-Output 'PASS copia protegida registra ACL original de settings sin aplicarla al respaldo'
$settingsLock=[IO.FileStream]::new($settingsFile,[IO.FileMode]::Open,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None)
try{
    $blocked=$false
    try{New-LtBackupSet -Destination (Join-Path $root 'settings-locked-backup') -Groups $settingsGroups -Metadata $meta -AssertProtection {param($a,$b,$c)return $true}|Out-Null}catch{$blocked=$true}
    if(-not $blocked){throw 'Settings bloqueado se omitió o se respaldó como completo.'}
}finally{$settingsLock.Dispose()}
Write-Output 'PASS settings.dat bloqueado impide declarar completo el respaldo'
