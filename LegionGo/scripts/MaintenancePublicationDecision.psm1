# SPDX-License-Identifier: AGPL-3.0-only
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
Import-Module (Join-Path $PSScriptRoot 'MaintenanceProtection.psm1') -ErrorAction Stop
Import-Module (Join-Path $PSScriptRoot 'MaintenanceNative.psm1') -ErrorAction Stop

function Assert-LtDecision([scriptblock]$Check,$Value,[string]$Message){
    $r=@(& $Check $Value)
    if($r.Count -ne 1 -or $r[0] -isnot [bool] -or -not $r[0]){throw $Message}
}
function Get-LtDecisionHash([string]$Value){
    $sha=[Security.Cryptography.SHA256]::Create()
    try{return ([BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($Value)))).Replace('-','')}finally{$sha.Dispose()}
}

# Includes stable volume/file IDs and ACLs, using relative paths so that the
# fingerprint remains the same after a same-volume directory rename.
function Get-LtPublicationTreeFingerprint {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Root,[Parameter(Mandatory)][scriptblock]$AssertProtection)
    $rootFull=[IO.Path]::GetFullPath($Root).TrimEnd('\')
    Assert-LtDecision $AssertProtection $rootFull 'Protección de árbol incierta.'
    $rootItem=Get-Item -LiteralPath $rootFull -Force -ErrorAction Stop
    if(-not $rootItem.PSIsContainer -or ($rootItem.Attributes -band [IO.FileAttributes]::ReparsePoint)){throw 'Árbol de publicación inseguro.'}
    $pending=[Collections.Generic.Stack[string]]::new();$pending.Push($rootFull)
    $entries=[Collections.Generic.SortedDictionary[string,object]]::new([StringComparer]::OrdinalIgnoreCase)
    while($pending.Count){
        $path=$pending.Pop();$item=Get-Item -LiteralPath $path -Force -ErrorAction Stop
        if($item.Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'Enlace en árbol de publicación.'}
        $relative=if($path -eq $rootFull){'.'}else{$path.Substring($rootFull.Length+1)}
        $handle=[LegionTweaks.MaintenanceNative.ProtectionApi]::Open($path,0,7,[IntPtr]::Zero,3,0x02200000,[IntPtr]::Zero)
        try{
            $id=[LegionTweaks.MaintenanceNative.FileIdentity]::new()
            if($handle.IsInvalid -or -not [LegionTweaks.MaintenanceNative.BackupApi]::GetFileInformationByHandle($handle,[ref]$id) -or ($id.Attributes -band [uint32][IO.FileAttributes]::ReparsePoint) -or ($id.IndexHigh -eq 0 -and $id.IndexLow -eq 0)){throw 'Identidad de publicación incierta.'}
            if(-not $item.PSIsContainer -and $id.Links -ne 1){throw 'Archivo de publicación con enlaces adicionales.'}
            $content=if($item.PSIsContainer){''}else{(Get-FileHash -LiteralPath $path -Algorithm SHA256 -ErrorAction Stop).Hash}
            $entry=[ordered]@{Path=$relative.ToUpperInvariant();Directory=[bool]$item.PSIsContainer;Volume=[uint32]$id.Volume;IndexHigh=[uint32]$id.IndexHigh;IndexLow=[uint32]$id.IndexLow;Sddl=(Get-Acl -LiteralPath $path -ErrorAction Stop).Sddl;Sha256=$content}
            if($entries.ContainsKey($relative)){throw 'Ruta duplicada en publicación.'};$entries.Add($relative,$entry)
        }finally{$handle.Dispose()}
        if($item.PSIsContainer){foreach($child in Get-ChildItem -LiteralPath $path -Force -ErrorAction Stop){$pending.Push($child.FullName)}}
    }
    Assert-LtDecision $AssertProtection $rootFull 'Protección de árbol cambió.'
    $array=@($entries.Values);$serialized=ConvertTo-Json -InputObject $array -Depth 5 -Compress
    return [pscustomobject]@{Sha256=(Get-LtDecisionHash $serialized);Items=$array.Count;AuthorizesMaintenance=$false}
}

function Convert-LtReadyBody($Body){
    $b=[ordered]@{}
    foreach($key in @('Version','Id','Direction','Live','Staging','Previous','TreeSha256','ExternalSha256','JournalSha256','BackupSha256','Y3Classification','Y3ContentSha256','Y3IdentitySha256','Y3ProtectionSha256')){
        if(-not $Body.Contains($key) -or $null -eq $Body[$key]){throw "Falta evidencia de publicación $key."}
        $b[$key]=$Body[$key]
    }
    if([int]$b.Version -ne 1 -or [string]$b.Id -cnotmatch '^[a-f0-9]{32}$' -or $b.Direction -notin @('Update','Return49')){throw 'Identidad de publicación inválida.'}
    foreach($key in @('TreeSha256','ExternalSha256','JournalSha256','BackupSha256')){if([string]$b[$key] -cnotmatch '^[A-F0-9]{64}$'){throw 'Pin de publicación inválido.'}}
    if($b.Y3Classification -notin @('absent','restored_with_original') -or [string]$b.Y3ProtectionSha256 -cnotmatch '^[A-F0-9]{64}$' -or
       ($b.Y3Classification -eq 'absent' -and ($b.Y3ContentSha256 -cne '' -or $b.Y3IdentitySha256 -cne '')) -or
       ($b.Y3Classification -eq 'restored_with_original' -and ([string]$b.Y3ContentSha256 -cnotmatch '^[A-F0-9]{64}$' -or [string]$b.Y3IdentitySha256 -cnotmatch '^[A-F0-9]{64}$'))){throw 'Evidencia Y3 de publicación inválida.'}
    $paths=[Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase);$parent=$null
    foreach($key in @('Live','Staging','Previous')){
        $value=[string]$b[$key];if(-not [IO.Path]::IsPathRooted($value)){throw 'Ruta de publicación no absoluta.'}
        $full=[IO.Path]::GetFullPath($value).TrimEnd('\');$directory=[IO.Path]::GetDirectoryName($full)
        if($full -cne $value -or -not $directory -or ($parent -and -not [string]::Equals($parent,$directory,[StringComparison]::OrdinalIgnoreCase)) -or -not $paths.Add($full)){throw 'Rutas de publicación ambiguas.'};$parent=$directory
    }
    return $b
}
function Format-LtReady($Body){
    $canonical=ConvertTo-Json -InputObject $Body -Compress
    return (ConvertTo-Json -InputObject ([ordered]@{Body=$Body;Sha256=(Get-LtDecisionHash $canonical)}) -Compress)
}
function Read-LtPublishReady {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Path,[Parameter(Mandatory)][scriptblock]$AssertProtection)
    $full=[IO.Path]::GetFullPath($Path);Assert-LtDecision $AssertProtection $full 'Protección de registro incierta.'
    $item=Get-Item -LiteralPath $full -Force -ErrorAction Stop
    if($item.PSIsContainer -or ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)){throw 'Registro de publicación inseguro.'}
    $stream=[IO.FileStream]::new($full,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read)
    try{
        if($stream.Length -le 0 -or $stream.Length -gt 16384){throw 'Registro de publicación fuera de tamaño.'}
        $reader=[IO.StreamReader]::new($stream,[Text.UTF8Encoding]::new($false,$true),$false)
        try{$text=$reader.ReadToEnd()}finally{$reader.Dispose()}
        $parsed=ConvertFrom-Json $text -ErrorAction Stop
        $map=[ordered]@{};foreach($p in $parsed.Body.PSObject.Properties){$map[$p.Name]=$p.Value}
        $body=Convert-LtReadyBody $map
        if($text -cne (Format-LtReady $body)){throw 'Registro de publicación alterado o ambiguo.'}
        Assert-LtDecision $AssertProtection $full 'Protección de registro cambió.'
        return [pscustomobject]$body
    }finally{$stream.Dispose()}
}

# Caller must perform and verify all external effects and old-tree fencing
# before calling this function. The evidence callback must independently check
# the journal/originals, backup, external effects, scope and quiescence.
function Write-LtPublishReady {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Path,[Parameter(Mandatory)][System.Collections.IDictionary]$Evidence,
          [Parameter(Mandatory)][scriptblock]$AssertProtection,[Parameter(Mandatory)][scriptblock]$ConfirmPrerequisites)
    $body=Convert-LtReadyBody $Evidence;$full=[IO.Path]::GetFullPath($Path)
    Assert-LtDecision $AssertProtection $full 'Protección de registro incierta.'
    if([IO.File]::Exists($full) -or [IO.Directory]::Exists($full)){throw 'Registro de publicación ya existe.'}
    if([IO.Directory]::Exists($body.Live) -or -not [IO.Directory]::Exists($body.Previous) -or -not [IO.Directory]::Exists($body.Staging)){throw 'Árboles no están en la frontera de publicación.'}
    Assert-LtDecision $ConfirmPrerequisites $body 'Precondiciones de publicación inciertas.'
    $fingerprint=Get-LtPublicationTreeFingerprint -Root $body.Staging -AssertProtection $AssertProtection
    if($fingerprint.Sha256 -ne $body.TreeSha256){throw 'Árbol preparado no coincide con evidencia.'}
    $null=Write-LtCheckpointWriteThrough -Path $full -Content (Format-LtReady $body)
    $ready=Read-LtPublishReady -Path $full -AssertProtection $AssertProtection
    return $ready
}

function Invoke-LtPublishReadyRename {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$ReadyPath,[Parameter(Mandatory)][scriptblock]$AssertProtection,
          [Parameter(Mandatory)][scriptblock]$ConfirmPrerequisites,[Parameter(Mandatory)][scriptblock]$ReadExternalFingerprint)
    $ready=Read-LtPublishReady -Path $ReadyPath -AssertProtection $AssertProtection
    if([IO.Directory]::Exists($ready.Live) -or [IO.File]::Exists($ready.Live) -or -not [IO.Directory]::Exists($ready.Previous) -or -not [IO.Directory]::Exists($ready.Staging)){throw 'Estado de árboles cambió antes del rename.'}
    Assert-LtDecision $ConfirmPrerequisites $ready 'Precondiciones de publicación cambiaron.'
    $values=@(& $ReadExternalFingerprint $ready)
    if($values.Count -ne 1 -or [string]$values[0] -cne $ready.ExternalSha256){throw 'Efectos externos cambiaron antes del rename.'}
    $tree=Get-LtPublicationTreeFingerprint -Root $ready.Staging -AssertProtection $AssertProtection
    if($tree.Sha256 -ne $ready.TreeSha256){throw 'El destino cambió antes del rename.'}
    # The rename is the publication decision. Any later error must reconcile;
    # callers must never infer failure from a missing return value.
    return (Move-LtDirectorySameParent -Source $ready.Staging -Destination $ready.Live)
}

# Never compensates or deletes a marker. A target that cannot be classified
# exactly is a review state, even if a caller believes a rename failed.
function Get-LtPublicationDecision {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$ReadyPath,[Parameter(Mandatory)][scriptblock]$AssertProtection,
          [Parameter(Mandatory)][scriptblock]$ReadExternalFingerprint,[Parameter(Mandatory)][scriptblock]$ConfirmRecoverySafe)
    $ready=Read-LtPublishReady -Path $ReadyPath -AssertProtection $AssertProtection
    if([IO.Directory]::Exists($ready.Live)){
        try{$tree=Get-LtPublicationTreeFingerprint -Root $ready.Live -AssertProtection $AssertProtection}catch{return [pscustomobject]@{Decision='recovery-required';Reason='live-tree-unverifiable'}}
        if($tree.Sha256 -ne $ready.TreeSha256){return [pscustomobject]@{Decision='recovery-required';Reason='live-tree-unknown'}}
        try{$external=@(& $ReadExternalFingerprint $ready)}catch{return [pscustomobject]@{Decision='published-review-required';Reason='external-unverifiable'}}
        if($external.Count -ne 1 -or [string]$external[0] -cne $ready.ExternalSha256){return [pscustomobject]@{Decision='published-review-required';Reason='external-changed'}}
        return [pscustomobject]@{Decision='published';Reason='tree-and-external-match'}
    }
    if([IO.File]::Exists($ready.Live)){return [pscustomobject]@{Decision='recovery-required';Reason='live-path-not-directory'}}
    try{Assert-LtDecision $ConfirmRecoverySafe $ready 'Recuperación independiente no demostrada.'}
    catch{return [pscustomobject]@{Decision='recovery-required';Reason='recovery-not-proven'}}
    return [pscustomobject]@{Decision='compensation-permitted';Reason='unpublished-and-recovery-proven'}
}

# Combines observations already obtained without acquiring or consuming an
# abandoned mutex. It never releases a dead owner's session or changes a Y3
# journal, original, marker or backup.
function New-LtReconciliationReport {
    [CmdletBinding()]
    param([Parameter(Mandatory)]$Publication,[Parameter(Mandatory)][ValidateSet('clear','residual','busy','abandoned_mutex','unknown')][string]$Reservation,
          [Parameter(Mandatory)][ValidateSet('restored','pending','unknown')][string]$Recovery)
    if($Publication.Decision -notin @('published','published-review-required','compensation-permitted','recovery-required')){throw 'Decisión de publicación desconocida.'}
    return [pscustomobject]@{Publication=$Publication.Decision;PublicationReason=$Publication.Reason;Reservation=$Reservation;PhysicalRecovery=$Recovery;
        MaintenanceAuthorized=$false;ReservationCleanupPending=($Reservation -ne 'clear');RecoveryReviewPending=($Recovery -ne 'restored')}
}
Export-ModuleMember -Function Get-LtPublicationTreeFingerprint,Write-LtPublishReady,Read-LtPublishReady,Invoke-LtPublishReadyRename,Get-LtPublicationDecision,New-LtReconciliationReport
