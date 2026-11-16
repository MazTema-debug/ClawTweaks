# SPDX-License-Identifier: AGPL-3.0-only
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
Import-Module (Join-Path $PSScriptRoot 'MaintenanceBackup.psm1') -ErrorAction Stop
Import-Module (Join-Path $PSScriptRoot 'MaintenanceNative.psm1') -ErrorAction Stop

function Get-LtBackupDirectoryState([string]$Root,[string[]]$ExcludedFiles=@()){
    $rootFull=[IO.Path]::GetFullPath($Root).TrimEnd('\')
    $rootItem=Get-Item -LiteralPath $rootFull -Force -ErrorAction Stop
    if(-not $rootItem.PSIsContainer -or ($rootItem.Attributes -band [IO.FileAttributes]::ReparsePoint)){throw 'Raíz de respaldo insegura.'}
    $items=@($rootItem)+@(Get-ChildItem -LiteralPath $rootFull -Force -Recurse -ErrorAction Stop)
    $dirs=[Collections.Generic.List[object]]::new();$files=[Collections.Generic.List[string]]::new()
    foreach($item in $items){
        if($item.Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'Enlace dentro del origen de respaldo.'}
        $rel=if($item.FullName -eq $rootFull){''}else{$item.FullName.Substring($rootFull.Length+1)}
        if($ExcludedFiles -contains $rel){if($item.PSIsContainer){throw 'La reserva excluida no es un archivo.'};continue}
        if($item.PSIsContainer){$dirs.Add([pscustomobject]@{RelativePath=$rel;Sddl=(Get-Acl -LiteralPath $item.FullName -ErrorAction Stop).Sddl})}
        else{$files.Add($rel)}
    }
    return [pscustomobject]@{Directories=@($dirs | Sort-Object RelativePath);Files=@($files | Sort-Object)}
}
function New-LtBackupSet {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Destination,[Parameter(Mandatory)][object[]]$Groups,
          [Parameter(Mandatory)][System.Collections.IDictionary]$Metadata,[Parameter(Mandatory)][scriptblock]$AssertProtection)
    $dest=[IO.Path]::GetFullPath($Destination).TrimEnd('\')
    if($Groups.Count -eq 0 -or (Test-Path -LiteralPath $dest)){throw 'Conjunto de respaldo vacío o existente.'}
    foreach($key in @('TaskXml','TaskWasEnabled','PackageFullName','TargetCertificateWasPresent','SourceCertificateWasPresent')){
        if(-not $Metadata.Contains($key) -or $null -eq $Metadata[$key]){throw "Falta metadato de respaldo $key."}
    }
    if($Metadata.TaskWasEnabled -isnot [bool] -or $Metadata.TargetCertificateWasPresent -isnot [bool] -or $Metadata.SourceCertificateWasPresent -isnot [bool] -or -not $Metadata.TaskXml -or -not $Metadata.PackageFullName){throw 'Metadatos de respaldo inválidos.'}
    $decision=@(& $AssertProtection $dest)
    if($decision.Count -ne 1 -or $decision[0] -isnot [bool] -or -not $decision[0]){throw 'Destino de respaldo no protegido.'}
    $names=[Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach($group in $Groups){
        if(-not $group.Name -or $group.Name -notmatch '^[a-z0-9-]{1,40}$' -or -not $group.SourceRoot -or -not $names.Add([string]$group.Name)){throw 'Grupo de respaldo inválido o duplicado.'}
        $excluded=@();if($group.PSObject.Properties['ExcludedFiles']){$excluded=@($group.ExcludedFiles)}
        if($excluded.Count -and ($group.Name -cne 'user-data' -or $excluded.Count -ne 1 -or $excluded[0] -cne 'fps-y3-admission-v1.guard')){throw 'Exclusión de respaldo no permitida.'}
        $protectedCopy=$false;if($group.PSObject.Properties['ProtectedCopy']){$protectedCopy=$group.ProtectedCopy;if($protectedCopy -isnot [bool]){throw 'Modo de respaldo inválido.'}}
        if($protectedCopy -and $group.Name -cnotin @('widget-settings','widget-local-state')){throw 'Modo de respaldo protegido no permitido para este grupo.'}
    }
    $null=New-Item -ItemType Directory -Path $dest -ErrorAction Stop
    $all=[Collections.Generic.List[object]]::new()
    foreach($group in $Groups){
        $src=[IO.Path]::GetFullPath([string]$group.SourceRoot).TrimEnd('\');$groupDest=Join-Path $dest $group.Name
        $excluded=@();if($group.PSObject.Properties['ExcludedFiles']){$excluded=@($group.ExcludedFiles)}
        $protectedCopy=$false;if($group.PSObject.Properties['ProtectedCopy']){$protectedCopy=$group.ProtectedCopy}
        if($excluded.Count -and -not [IO.File]::Exists((Join-Path $src $excluded[0]))){throw 'Marcador de reserva ausente durante respaldo.'}
        $before=Get-LtBackupDirectoryState $src $excluded
        if($before.Files.Count){$records=@(Copy-LtProtectedBackup -SourceRoot $src -DestinationRoot $groupDest -RelativeFiles $before.Files -AssertProtection $AssertProtection -PreserveSourceAcl:(-not $protectedCopy))}
        else{$null=New-Item -ItemType Directory -Path $groupDest -ErrorAction Stop;$records=@()}
        foreach($dir in @($before.Directories | Sort-Object {$_.RelativePath.Length})){
            $sourceDir=if($dir.RelativePath){Join-Path $src $dir.RelativePath}else{$src}
            $backupDir=if($dir.RelativePath){Join-Path $groupDest $dir.RelativePath}else{$groupDest}
            if(-not (Test-Path -LiteralPath $backupDir)){$null=New-Item -ItemType Directory -Path $backupDir -ErrorAction Stop}
            if(-not $protectedCopy){$null=Set-LtBackupSecurity -Path $backupDir -Security (Get-Acl -LiteralPath $sourceDir -ErrorAction Stop) -AssertProtection $AssertProtection}
            $decision=@(& $AssertProtection $backupDir)
            if($decision.Count -ne 1 -or $decision[0] -isnot [bool] -or -not $decision[0]){throw 'Directorio de respaldo no protegido.'}
        }
        foreach($record in $records){
            $sourceFile=Join-Path $src $record.RelativePath;$backupFile=Join-Path $groupDest $record.RelativePath
            if(-not $protectedCopy){
                $null=Set-LtBackupSecurity -Path $backupFile -Security (Get-Acl -LiteralPath $sourceFile -ErrorAction Stop) -AssertProtection $AssertProtection
                if((Get-Acl -LiteralPath $backupFile -ErrorAction Stop).Sddl -ne $record.Sddl){throw 'ACL de respaldo no coincide.'}
            }
        }
        $after=Get-LtBackupDirectoryState $src $excluded
        if((ConvertTo-Json -InputObject $before -Depth 6 -Compress) -cne (ConvertTo-Json -InputObject $after -Depth 6 -Compress)){throw 'Cambió el inventario o ACL de directorios durante respaldo.'}
        $backupState=Get-LtBackupDirectoryState $groupDest
        if($protectedCopy){
            if((ConvertTo-Json -InputObject @($before.Directories|ForEach-Object {$_.RelativePath}) -Compress) -cne (ConvertTo-Json -InputObject @($backupState.Directories|ForEach-Object {$_.RelativePath}) -Compress) -or (ConvertTo-Json -InputObject $before.Files -Compress) -cne (ConvertTo-Json -InputObject $backupState.Files -Compress)){throw 'Inventario protegido de respaldo no coincide.'}
        }elseif((ConvertTo-Json -InputObject $before -Depth 6 -Compress) -cne (ConvertTo-Json -InputObject $backupState -Depth 6 -Compress)){throw 'Los directorios respaldados no coinciden.'}
        foreach($record in $records){
            $original=Join-Path $src $record.RelativePath
            $stream=[IO.FileStream]::new($original,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read)
            try{
                $identity=[LegionTweaks.MaintenanceNative.FileIdentity]::new()
                if(-not [LegionTweaks.MaintenanceNative.BackupApi]::GetFileInformationByHandle($stream.SafeFileHandle,[ref]$identity) -or $identity.Links -ne 1 -or $identity.Volume -ne $record.Volume -or $identity.IndexHigh -ne $record.FileIndexHigh -or $identity.IndexLow -ne $record.FileIndexLow){throw 'Un original fue reemplazado tras respaldarse.'}
                $sha=[Security.Cryptography.SHA256]::Create();try{$hash=([BitConverter]::ToString($sha.ComputeHash($stream))).Replace('-','')}finally{$sha.Dispose()}
                if($hash -ne $record.Sha256){throw 'Un original cambió tras respaldarse.'}
            }finally{$stream.Dispose()}
        }
        $all.Add([pscustomobject]@{Name=[string]$group.Name;SourceRoot=$src;ProtectedCopy=[bool]$protectedCopy;ExcludedFiles=$excluded;Directories=$before.Directories;Files=$records})
    }
    $manifest=[ordered]@{Version=1;Groups=$all.ToArray();Metadata=$Metadata}
    $json=ConvertTo-Json -InputObject $manifest -Depth 12 -Compress
    $manifestPath=Join-Path $dest 'manifest.json'
    $null=Write-LtCheckpointWriteThrough -Path $manifestPath -Content $json
    $decision=@(& $AssertProtection $manifestPath)
    if($decision.Count -ne 1 -or $decision[0] -isnot [bool] -or -not $decision[0]){throw 'Manifiesto de respaldo no protegido.'}
    return [pscustomobject]@{ManifestPath=$manifestPath;ManifestSha256=(Get-FileHash -LiteralPath $manifestPath -Algorithm SHA256).Hash;Groups=$all.Count;AuthorizesMaintenance=$false}
}
function Test-LtBackupSet {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Destination,
          [Parameter(Mandatory)][ValidatePattern('^[0-9A-Fa-f]{64}$')][string]$ExpectedManifestSha256,
          [Parameter(Mandatory)][scriptblock]$AssertProtection)
    $dest=[IO.Path]::GetFullPath($Destination).TrimEnd('\');$manifestPath=Join-Path $dest 'manifest.json'
    $decision=@(& $AssertProtection $dest)
    if($decision.Count -ne 1 -or $decision[0] -isnot [bool] -or -not $decision[0]){throw 'Respaldo no protegido.'}
    $stream=[IO.FileStream]::new($manifestPath,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read)
    try {
        if($stream.Length -le 0 -or $stream.Length -gt 10485760){throw 'Manifiesto fuera de tamaño.'}
        $sha=[Security.Cryptography.SHA256]::Create();try{$hash=([BitConverter]::ToString($sha.ComputeHash($stream))).Replace('-','')}finally{$sha.Dispose()}
        if($hash -ne $ExpectedManifestSha256){throw 'El manifiesto de respaldo cambió.'}
        $stream.Position=0;$reader=[IO.StreamReader]::new($stream,[Text.UTF8Encoding]::new($false,$true),$false)
        try{$manifest=ConvertFrom-Json -InputObject $reader.ReadToEnd() -ErrorAction Stop}finally{$reader.Dispose()}
    }finally{$stream.Dispose()}
    if($manifest.Version -ne 1 -or @($manifest.Groups).Count -eq 0 -or -not $manifest.Metadata){throw 'Manifiesto de respaldo inválido.'}
    $names=[Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach($group in $manifest.Groups){
        if($group.Name -notmatch '^[a-z0-9-]{1,40}$' -or -not $names.Add([string]$group.Name)){throw 'Grupo de respaldo ambiguo.'}
        $excluded=@();if($group.PSObject.Properties['ExcludedFiles']){$excluded=@($group.ExcludedFiles)}
        if($excluded.Count -and ($group.Name -cne 'user-data' -or $excluded.Count -ne 1 -or $excluded[0] -cne 'fps-y3-admission-v1.guard')){throw 'Exclusión de respaldo inválida.'}
        $protectedCopy=$false;if($group.PSObject.Properties['ProtectedCopy']){$protectedCopy=$group.ProtectedCopy;if($protectedCopy -isnot [bool]){throw 'Modo de respaldo inválido.'}}
        if($protectedCopy -and $group.Name -cnotin @('widget-settings','widget-local-state')){throw 'Modo protegido no corresponde al grupo.'}
        $root=Join-Path $dest $group.Name;$actual=Get-LtBackupDirectoryState $root
        $expectedDirs=@($group.Directories | Sort-Object RelativePath);$expectedFiles=@($group.Files | ForEach-Object {[string]$_.RelativePath} | Sort-Object)
        $dirActual=if($protectedCopy){@($actual.Directories|ForEach-Object {$_.RelativePath})}else{$actual.Directories}
        $dirExpected=if($protectedCopy){@($expectedDirs|ForEach-Object {$_.RelativePath})}else{$expectedDirs}
        if((ConvertTo-Json -InputObject $dirActual -Depth 5 -Compress) -cne (ConvertTo-Json -InputObject $dirExpected -Depth 5 -Compress) -or (ConvertTo-Json -InputObject $actual.Files -Compress) -cne (ConvertTo-Json -InputObject $expectedFiles -Compress)){throw 'Contenido o ACL de directorios respaldados cambiaron.'}
        foreach($record in @($group.Files)){
            $relative=[string]$record.RelativePath
            if(-not $relative -or [IO.Path]::IsPathRooted($relative) -or $relative.Contains(':') -or $relative -match '(^|[\\/])\.\.?($|[\\/])'){throw 'Ruta de respaldo inválida.'}
            $file=[IO.Path]::GetFullPath((Join-Path $root $relative))
            if(-not $file.StartsWith($root+'\',[StringComparison]::OrdinalIgnoreCase) -or (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash -ne $record.Sha256 -or (-not $protectedCopy -and (Get-Acl -LiteralPath $file -ErrorAction Stop).Sddl -ne $record.Sddl)){throw 'Archivo o ACL respaldado cambió.'}
        }
    }
    foreach($item in Get-ChildItem -LiteralPath $dest -Force -ErrorAction Stop){
        if(($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -or ($item.PSIsContainer -and -not $names.Contains($item.Name)) -or (-not $item.PSIsContainer -and $item.Name -ne 'manifest.json')){throw 'Entrada adicional en respaldo.'}
    }
    $decision=@(& $AssertProtection $dest)
    if($decision.Count -ne 1 -or $decision[0] -isnot [bool] -or -not $decision[0]){throw 'Protección de respaldo cambió.'}
    return [pscustomobject]@{ManifestSha256=$hash;Groups=$names.Count;Metadata=$manifest.Metadata;AuthorizesMaintenance=$false}
}

# Creates a separate restore tree from the verified helper group. It never
# writes the original user-data group, Y3 journal, marker or original bytes.
function New-LtRestoreTree {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$BackupDestination,[Parameter(Mandatory)][ValidatePattern('^[0-9A-Fa-f]{64}$')][string]$ExpectedManifestSha256,
          [Parameter(Mandatory)][string]$RestoreTree,[Parameter(Mandatory)][scriptblock]$AssertProtection)
    $backup=[IO.Path]::GetFullPath($BackupDestination).TrimEnd('\');$restore=[IO.Path]::GetFullPath($RestoreTree).TrimEnd('\')
    if($backup -eq $restore -or $backup.StartsWith($restore+'\',[StringComparison]::OrdinalIgnoreCase) -or $restore.StartsWith($backup+'\',[StringComparison]::OrdinalIgnoreCase) -or (Test-Path -LiteralPath $restore)){throw 'Ruta de retorno insegura o existente.'}
    $null=Test-LtBackupSet -Destination $backup -ExpectedManifestSha256 $ExpectedManifestSha256 -AssertProtection $AssertProtection
    $manifestPath=Join-Path $backup 'manifest.json';$manifest=Get-Content -LiteralPath $manifestPath -Raw -Encoding UTF8|ConvertFrom-Json -ErrorAction Stop
    if((Get-FileHash -LiteralPath $manifestPath -Algorithm SHA256).Hash -ne $ExpectedManifestSha256){throw 'Manifiesto cambió antes de preparar retorno.'}
    $selected=@($manifest.Groups|Where-Object {$_.Name -ceq 'helper'})
    if($selected.Count -ne 1 -or @($selected[0].Files).Count -eq 0 -or @($selected[0].ExcludedFiles).Count -ne 0){throw 'Grupo helper incompleto o ambiguo.'}
    $group=$selected[0];$source=Join-Path $backup 'helper'
    $files=@($group.Files|ForEach-Object {[string]$_.RelativePath})
    $null=Copy-LtProtectedBackup -SourceRoot $source -DestinationRoot $restore -RelativeFiles $files -AssertProtection $AssertProtection
    foreach($dir in @($group.Directories|Sort-Object {$_.RelativePath.Length})){
        $src=if($dir.RelativePath){Join-Path $source $dir.RelativePath}else{$source}
        $dst=if($dir.RelativePath){Join-Path $restore $dir.RelativePath}else{$restore}
        if(-not (Test-Path -LiteralPath $dst)){$null=New-Item -ItemType Directory -Path $dst -ErrorAction Stop}
        $null=Set-LtBackupSecurity -Path $dst -Security (Get-Acl -LiteralPath $src -ErrorAction Stop) -AssertProtection $AssertProtection
    }
    foreach($file in $group.Files){
        $dst=Join-Path $restore $file.RelativePath
        if((Get-FileHash -LiteralPath $dst -Algorithm SHA256).Hash -ne $file.Sha256 -or (Get-Acl -LiteralPath $dst -ErrorAction Stop).Sddl -ne $file.Sddl){throw 'Árbol de retorno difiere del respaldo.'}
    }
    $actual=Get-LtBackupDirectoryState $restore
    $expectedDirs=@($group.Directories|Sort-Object RelativePath);$expectedFiles=@($group.Files|ForEach-Object {[string]$_.RelativePath}|Sort-Object)
    if((ConvertTo-Json -InputObject $actual.Directories -Depth 5 -Compress) -cne (ConvertTo-Json -InputObject $expectedDirs -Depth 5 -Compress) -or (ConvertTo-Json -InputObject $actual.Files -Compress) -cne (ConvertTo-Json -InputObject $expectedFiles -Compress)){throw 'Directorio de retorno difiere del respaldo.'}
    $null=Test-LtBackupSet -Destination $backup -ExpectedManifestSha256 $ExpectedManifestSha256 -AssertProtection $AssertProtection
    $decision=@(& $AssertProtection $restore)
    if($decision.Count -ne 1 -or $decision[0] -isnot [bool] -or -not $decision[0]){throw 'Protección del retorno incierta.'}
    return $true
}
function Test-LtRestoreTree {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$BackupDestination,[Parameter(Mandatory)][ValidatePattern('^[0-9A-Fa-f]{64}$')][string]$ExpectedManifestSha256,
          [Parameter(Mandatory)][string]$RestoreTree,[Parameter(Mandatory)][scriptblock]$AssertProtection)
    $backup=[IO.Path]::GetFullPath($BackupDestination).TrimEnd('\');$restore=[IO.Path]::GetFullPath($RestoreTree).TrimEnd('\')
    $null=Test-LtBackupSet -Destination $backup -ExpectedManifestSha256 $ExpectedManifestSha256 -AssertProtection $AssertProtection
    $manifestPath=Join-Path $backup 'manifest.json';$manifest=Get-Content -LiteralPath $manifestPath -Raw -Encoding UTF8|ConvertFrom-Json -ErrorAction Stop
    if((Get-FileHash -LiteralPath $manifestPath -Algorithm SHA256).Hash -ne $ExpectedManifestSha256){throw 'Manifiesto cambió antes de verificar retorno.'}
    $groups=@($manifest.Groups|Where-Object {$_.Name -ceq 'helper'})
    if($groups.Count -ne 1 -or @($groups[0].Files).Count -eq 0 -or @($groups[0].ExcludedFiles).Count -ne 0){throw 'Grupo helper incompleto o ambiguo.'}
    $group=$groups[0];$state=Get-LtBackupDirectoryState $restore
    $expectedDirs=@($group.Directories|Sort-Object RelativePath);$expectedFiles=@($group.Files|ForEach-Object {[string]$_.RelativePath}|Sort-Object)
    if((ConvertTo-Json -InputObject $state.Directories -Depth 5 -Compress) -cne (ConvertTo-Json -InputObject $expectedDirs -Depth 5 -Compress) -or (ConvertTo-Json -InputObject $state.Files -Compress) -cne (ConvertTo-Json -InputObject $expectedFiles -Compress)){throw 'Inventario de retorno cambió.'}
    foreach($file in $group.Files){
        $path=Join-Path $restore $file.RelativePath
        if((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ne $file.Sha256 -or (Get-Acl -LiteralPath $path -ErrorAction Stop).Sddl -ne $file.Sddl){throw 'Archivo o ACL de retorno cambió.'}
    }
    $check=@(& $AssertProtection $restore)
    if($check.Count -ne 1 -or $check[0] -isnot [bool] -or -not $check[0]){throw 'Protección de retorno incierta.'}
    return $true
}
function Get-LtBackedY3Snapshot {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$BackupDestination,[Parameter(Mandatory)][ValidatePattern('^[A-F0-9]{64}$')][string]$ExpectedManifestSha256,[Parameter(Mandatory)][scriptblock]$AssertProtection)
    $null=Test-LtBackupSet -Destination $BackupDestination -ExpectedManifestSha256 $ExpectedManifestSha256 -AssertProtection $AssertProtection
    $manifestPath=Join-Path $BackupDestination 'manifest.json';$manifest=Get-Content -LiteralPath $manifestPath -Raw -Encoding UTF8|ConvertFrom-Json -ErrorAction Stop
    if((Get-FileHash -LiteralPath $manifestPath -Algorithm SHA256).Hash -cne $ExpectedManifestSha256){throw 'Manifiesto Y3 cambió.'}
    $groups=@($manifest.Groups|Where-Object {$_.Name -ceq 'user-data'})
    if($groups.Count -ne 1){throw 'Falta grupo Y3 respaldado.'}
    $journal=@($groups[0].Files|Where-Object {$_.RelativePath -ceq 'fps-y3-trial.json'})
    if($journal.Count -gt 1){throw 'Diario Y3 respaldado ambiguo.'}
    if($journal.Count -eq 0){return [pscustomobject]@{Classification='absent';ContentSha256='';FileIdentitySha256='';AuthorizesMaintenance=$false}}
    $entry=$journal[0];$identity=('{0:X8}{1:X8}{2:X8}' -f [uint32]$entry.Volume,[uint32]$entry.FileIndexHigh,[uint32]$entry.FileIndexLow)
    $sha=[Security.Cryptography.SHA256]::Create();try{$idHash=([BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::ASCII.GetBytes($identity)))).Replace('-','')}finally{$sha.Dispose()}
    return [pscustomobject]@{Classification='restored_with_original';ContentSha256=[string]$entry.Sha256;FileIdentitySha256=$idHash;AuthorizesMaintenance=$false}
}
Export-ModuleMember -Function New-LtBackupSet,Test-LtBackupSet,New-LtRestoreTree,Test-LtRestoreTree,Get-LtBackedY3Snapshot
