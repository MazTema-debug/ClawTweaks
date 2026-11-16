# SPDX-License-Identifier: AGPL-3.0-only
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
Import-Module (Join-Path $PSScriptRoot 'MaintenanceNative.psm1') -ErrorAction Stop
Import-Module (Join-Path $PSScriptRoot 'MaintenanceImageBarrier.psm1') -ErrorAction Stop
Import-Module (Join-Path $PSScriptRoot 'MaintenancePublicationDecision.psm1') -ErrorAction Stop
function Test-LtTreeDecision([scriptblock]$Checker,$Argument){
    $values=@(& $Checker $Argument)
    if($values.Count -ne 1 -or $values[0] -isnot [bool] -or -not $values[0]){throw 'Una comprobación de publicación o retorno falló.'}
    return $true
}

function Test-LtDisabledImage([string]$Image,[string[]]$ExpectedPins) {
    $stream=[IO.FileStream]::new($Image,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read)
    try{
        if($stream.Length -lt 2 -or $stream.Length -gt 67108864 -or $stream.ReadByte() -ne 0 -or $stream.ReadByte() -ne 90){throw 'Barrera de imagen ausente.'}
        $stream.Position=0;$bytes=[byte[]]::new([int]$stream.Length);$n=0
        while($n -lt $bytes.Length){$read=$stream.Read($bytes,$n,$bytes.Length-$n);if($read -le 0){throw 'Lectura incompleta.'};$n+=$read}
        $bytes[0]=77;$sha=[Security.Cryptography.SHA256]::Create()
        try{$hash=([BitConverter]::ToString($sha.ComputeHash($bytes))).Replace('-','')}finally{$sha.Dispose()}
        if($ExpectedPins -notcontains $hash){throw 'Cuerpo de imagen cambió.'}
    }finally{$stream.Dispose()}
    return $true
}

# Tree publication layer only: the coordinator must provide real, verified
# implementations for scope, external rollback and final tree verification.
# No effect runs until a returned adapter is called by the coordinator.
function New-LtTreePublicationAdapters {
    [CmdletBinding()]
    param([Parameter(Mandatory)][System.Collections.IDictionary]$Configuration)
    foreach($key in @('Live','Staging','Previous','Failed','Restore','OldPins','NewPins','AssertScope','AfterOldRename','RestoreExternal','VerifyExternal','VerifyTarget','VerifyRestore','ReadyPath','GetReadyEvidence','ConfirmPrerequisites','ReadExternalFingerprint','ConfirmRecoverySafe')){if(-not $Configuration.Contains($key) -or $null -eq $Configuration[$key]){throw "Falta configuración $key."}}
    foreach($key in @('AssertScope','AfterOldRename','RestoreExternal','VerifyExternal','VerifyTarget','VerifyRestore','GetReadyEvidence','ConfirmPrerequisites','ReadExternalFingerprint','ConfirmRecoverySafe')){if($Configuration[$key] -isnot [scriptblock]){throw "Falta callback $key."}}
    $testDisabled=${function:Test-LtDisabledImage};$check=${function:Test-LtTreeDecision}
    $getTree=Get-Command Get-LtPublicationTreeFingerprint -CommandType Function -ErrorAction Stop
    $disableImage=Get-Command Disable-LtImageForPublication -CommandType Function -ErrorAction Stop
    $moveTree=Get-Command Move-LtDirectorySameParent -CommandType Function -ErrorAction Stop
    $writeReady=Get-Command Write-LtPublishReady -CommandType Function -ErrorAction Stop
    $publishReady=Get-Command Invoke-LtPublishReadyRename -CommandType Function -ErrorAction Stop
    $decide=Get-Command Get-LtPublicationDecision -CommandType Function -ErrorAction Stop
    $seen=[Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase);$parent=$null
    foreach($key in @('Live','Staging','Previous','Failed','Restore')){
        $path=[IO.Path]::GetFullPath([string]$Configuration[$key]).TrimEnd('\');$directory=[IO.Path]::GetDirectoryName($path)
        if(-not $directory -or ($parent -and -not [string]::Equals($parent,$directory,[StringComparison]::OrdinalIgnoreCase)) -or -not $seen.Add($path)){throw 'Árboles deben ser distintos y compartir padre.'}
        $parent=$directory;$Configuration[$key]=$path
    }
    if($Configuration.OldPins -isnot [System.Collections.IDictionary] -or $Configuration.NewPins -isnot [System.Collections.IDictionary] -or $Configuration.OldPins.Count -eq 0 -or $Configuration.OldPins.Count -ne $Configuration.NewPins.Count){throw 'Faltan pins de imágenes.'}
    foreach($relative in $Configuration.OldPins.Keys){
        if(-not $relative -or [IO.Path]::IsPathRooted($relative) -or $relative.Contains(':') -or $relative -match '(^|[\\/])\.\.?($|[\\/])' -or -not $Configuration.NewPins.Contains($relative) -or $Configuration.OldPins[$relative] -notmatch '^[0-9A-Fa-f]{64}$' -or $Configuration.NewPins[$relative] -notmatch '^[0-9A-Fa-f]{64}$'){throw 'Pin o ruta de imagen inválidos.'}
    }
    $arm={param($ctx)
        $null=& $check $Configuration.AssertScope $ctx
        foreach($relative in $Configuration.OldPins.Keys){
            $image=Join-Path $Configuration.Live $relative
            & $disableImage -Image $image -ExpectedSha256 $Configuration.OldPins[$relative] -AssertScope {param($p)return (& $check $Configuration.AssertScope $ctx)}|Out-Null
            if($Configuration.Contains('AfterImageForTest')){$null=& $check $Configuration.AfterImageForTest $relative}
        }
        return $true
    }.GetNewClosure()
    $verify={param($ctx)
        $null=& $check $Configuration.AssertScope $ctx
        foreach($relative in $Configuration.OldPins.Keys){
            $image=Join-Path $Configuration.Live $relative
            $null=& $testDisabled -Image $image -ExpectedPins @($Configuration.OldPins[$relative])
        }
        return $true
    }.GetNewClosure()
    $prepare={param($ctx)
        $null=& $check $Configuration.AssertScope $ctx;$null=& $check $Configuration.VerifyTarget $Configuration.Staging
        & $moveTree -Source $Configuration.Live -Destination $Configuration.Previous|Out-Null
        $null=& $check $Configuration.AfterOldRename $ctx
        $tree=& $getTree -Root $Configuration.Staging -AssertProtection $Configuration.AssertScope
        $evidence=@(& $Configuration.GetReadyEvidence $ctx)
        if($evidence.Count -ne 1 -or $evidence[0] -isnot [System.Collections.IDictionary]){throw 'Falta evidencia de publicación.'}
        $body=[ordered]@{Version=1;Id=$evidence[0].Id;Direction=$ctx.Direction;Live=$Configuration.Live;Staging=$Configuration.Staging;Previous=$Configuration.Previous;
            TreeSha256=$tree.Sha256;ExternalSha256=$evidence[0].ExternalSha256;JournalSha256=$evidence[0].JournalSha256;BackupSha256=$evidence[0].BackupSha256;
            Y3Classification=$evidence[0].Y3Classification;Y3ContentSha256=$evidence[0].Y3ContentSha256;Y3IdentitySha256=$evidence[0].Y3IdentitySha256;Y3ProtectionSha256=$evidence[0].Y3ProtectionSha256}
        $null=& $writeReady -Path $Configuration.ReadyPath -Evidence $body -AssertProtection $Configuration.AssertScope -ConfirmPrerequisites $Configuration.ConfirmPrerequisites
        return $true
    }.GetNewClosure()
    $publish={param($ctx)
        return (& $publishReady -ReadyPath $Configuration.ReadyPath -AssertProtection $Configuration.AssertScope -ConfirmPrerequisites $Configuration.ConfirmPrerequisites -ReadExternalFingerprint $Configuration.ReadExternalFingerprint)
    }.GetNewClosure()
    $reconcile={param($ctx)
        $decision=& $decide -ReadyPath $Configuration.ReadyPath -AssertProtection $Configuration.AssertScope -ReadExternalFingerprint $Configuration.ReadExternalFingerprint -ConfirmRecoverySafe $Configuration.ConfirmRecoverySafe
        if($decision.Decision -eq 'published'){return 'committed'}
        if($decision.Decision -eq 'compensation-permitted'){return 'not-committed'}
        return 'uncertain'
    }.GetNewClosure()
    # Diagnostic only: it never changes the published tree, reservation or Y3.
    $inspect={param($ctx)
        return (& $decide -ReadyPath $Configuration.ReadyPath -AssertProtection $Configuration.AssertScope -ReadExternalFingerprint $Configuration.ReadExternalFingerprint -ConfirmRecoverySafe $Configuration.ConfirmRecoverySafe)
    }.GetNewClosure()
    $published={param($ctx)return (& $check $Configuration.VerifyTarget $Configuration.Live)}.GetNewClosure()
    $compensate={param($ctx)
        $null=& $check $Configuration.AssertScope $ctx;$null=& $check $Configuration.VerifyRestore $Configuration.Restore
        if(Test-Path -LiteralPath $Configuration.Live){
            foreach($relative in $Configuration.OldPins.Keys){
                $image=Join-Path $Configuration.Live $relative
                $fs=[IO.FileStream]::new($image,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read)
                try{$first=$fs.ReadByte()}finally{$fs.Dispose()}
                if($first -eq 0){$null=& $testDisabled -Image $image -ExpectedPins @($Configuration.OldPins[$relative],$Configuration.NewPins[$relative])}
                else{
                    $hash=(Get-FileHash -LiteralPath $image -Algorithm SHA256).Hash
                    if($hash -ne $Configuration.OldPins[$relative] -and $hash -ne $Configuration.NewPins[$relative]){throw 'Imagen activa no coincide con ningún pin.'}
                    & $disableImage -Image $image -ExpectedSha256 $hash -AssertScope {param($p)return (& $check $Configuration.AssertScope $ctx)}|Out-Null
                }
            }
        }
        $null=& $check $Configuration.RestoreExternal $ctx;$null=& $check $Configuration.VerifyExternal $ctx
        if(Test-Path -LiteralPath $Configuration.Live){& $moveTree -Source $Configuration.Live -Destination $Configuration.Failed|Out-Null}
        & $moveTree -Source $Configuration.Restore -Destination $Configuration.Live|Out-Null
        return $true
    }.GetNewClosure()
    $compensated={param($ctx)$null=& $check $Configuration.VerifyExternal $ctx;$null=& $check $Configuration.VerifyRestore $Configuration.Live;return $true}.GetNewClosure()
    return @{ArmBarrier=$arm;VerifyBarrier=$verify;PreparePublication=$prepare;PublishTree=$publish;ReconcileCommit=$reconcile;InspectPublication=$inspect;VerifyPublished=$published;Compensate=$compensate;VerifyCompensated=$compensated}
}
Export-ModuleMember -Function New-LtTreePublicationAdapters,Test-LtDisabledImage
