# SPDX-License-Identifier: AGPL-3.0-only
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
Import-Module (Join-Path $PSScriptRoot 'MaintenanceNative.psm1') -ErrorAction Stop
$script:stages=@('prepared','backup-verified','arm-barrier-intent','barrier-armed','certificate-intent','certificate-done',
    'appx-intent','appx-done','task-intent','task-done','startup-intent','startup-done','enable-task-intent','enable-task-done','publish-intent','old-renamed','publish-ready','committed','recovery-required','compensated')

function Assert-LtJournalProtection([scriptblock]$Check,[string]$Path){
    $r=@(& $Check $Path);if($r.Count -ne 1 -or $r[0] -isnot [bool] -or -not $r[0]){throw 'Protección del registro de transacción incierta.'}
}
function Convert-LtJournalBody($j){
    if([int]$j.Version -ne 1 -or [string]$j.Id -notmatch '^[a-f0-9]{32}$' -or [string]$j.Direction -notin @('Update','Return49') -or [string]$j.Stage -notin $script:stages -or $j.Sequence -isnot [int] -and $j.Sequence -isnot [long] -or [long]$j.Sequence -lt 0 -or [string]$j.OldSha256 -notmatch '^[A-F0-9]{64}$' -or [string]$j.NewSha256 -notmatch '^[A-F0-9]{64}$' -or [string]$j.CoordinatorSha256 -notmatch '^[A-F0-9]{64}$' -or [string]$j.BackupManifestSha256 -notmatch '^[A-F0-9]{64}$'){throw 'Registro de transacción inválido.'}
    return [ordered]@{Version=1;Id=[string]$j.Id;Direction=[string]$j.Direction;Stage=[string]$j.Stage;Sequence=[long]$j.Sequence;OldSha256=[string]$j.OldSha256;NewSha256=[string]$j.NewSha256;CoordinatorSha256=[string]$j.CoordinatorSha256;BackupManifestSha256=[string]$j.BackupManifestSha256}
}
function Get-LtStringHash([string]$text){
    $sha=[Security.Cryptography.SHA256]::Create();try{return ([BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($text)))).Replace('-','')}finally{$sha.Dispose()}
}
function Format-LtJournal($body){
    $canonical=ConvertTo-Json -InputObject $body -Compress
    return (ConvertTo-Json -InputObject ([ordered]@{Body=$body;Sha256=(Get-LtStringHash $canonical)}) -Compress)
}
function Get-LtJournalBodySha256 {
    [CmdletBinding()]
    param([Parameter(Mandatory)]$Body)
    return (Get-LtStringHash (Format-LtJournal (Convert-LtJournalBody $Body)))
}
function Read-LtTransactionJournal {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Path,[Parameter(Mandatory)][scriptblock]$AssertProtection)
    $pathFull=[IO.Path]::GetFullPath($Path);Assert-LtJournalProtection $AssertProtection $pathFull
    $item=Get-Item -LiteralPath $pathFull -Force -ErrorAction Stop
    if($item.PSIsContainer -or ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)){throw 'Registro de transacción inseguro.'}
    $stream=[IO.FileStream]::new($pathFull,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read)
    try{
        if($stream.Length -le 0 -or $stream.Length -gt 8192){throw 'Registro de transacción fuera de tamaño.'}
        $reader=[IO.StreamReader]::new($stream,[Text.UTF8Encoding]::new($false,$true),$false)
        try{$text=$reader.ReadToEnd()}finally{$reader.Dispose()}
        $j=ConvertFrom-Json -InputObject $text -ErrorAction Stop
        if(-not $j.Body -or [string]$j.Sha256 -notmatch '^[A-F0-9]{64}$'){throw 'Registro de transacción incompleto.'}
        $body=Convert-LtJournalBody $j.Body
        $expected=Format-LtJournal $body
        if($text -cne $expected){throw 'Registro de transacción ambiguo o alterado.'}
        Assert-LtJournalProtection $AssertProtection $pathFull
        return [pscustomobject]$body
    }finally{$stream.Dispose()}
}
function Write-LtTransactionJournal {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Path,[Parameter(Mandatory)]$Body,
          [Parameter(Mandatory)][scriptblock]$AssertProtection,[string]$ExpectedStage)
    $pathFull=[IO.Path]::GetFullPath($Path);Assert-LtJournalProtection $AssertProtection $pathFull
    $next=Convert-LtJournalBody $Body
    if(Test-Path -LiteralPath $pathFull){
        $prior=Read-LtTransactionJournal -Path $pathFull -AssertProtection $AssertProtection
        if(-not $ExpectedStage -or $prior.Stage -ne $ExpectedStage -or $prior.Id -ne $next.Id -or $prior.Direction -ne $next.Direction -or $prior.OldSha256 -ne $next.OldSha256 -or $prior.NewSha256 -ne $next.NewSha256 -or $prior.CoordinatorSha256 -ne $next.CoordinatorSha256 -or $prior.BackupManifestSha256 -ne $next.BackupManifestSha256 -or [long]$next.Sequence -ne [long]$prior.Sequence+1){throw 'Transición de registro inesperada.'}
        $index=[array]::IndexOf($script:stages,[string]$prior.Stage)
        if($prior.Stage -eq 'committed' -or $prior.Stage -eq 'compensated' -or ($prior.Stage -eq 'recovery-required' -and $next.Stage -ne 'compensated') -or ($prior.Stage -ne 'recovery-required' -and $next.Stage -ne 'recovery-required' -and ($index -lt 0 -or $index+1 -ge $script:stages.Count -or $next.Stage -ne $script:stages[$index+1]))){throw 'La fase del registro no es sucesora.'}
    }elseif($ExpectedStage -or $next.Stage -ne 'prepared' -or $next.Sequence -ne 0){throw 'Falta el registro inicial.'}
    $encoded=Format-LtJournal $next
    $null=Write-LtCheckpointWriteThrough -Path $pathFull -Content $encoded
    $result=Read-LtTransactionJournal -Path $pathFull -AssertProtection $AssertProtection
    if($result.Id -ne $next.Id -or $result.Stage -ne $next.Stage -or $result.Sequence -ne $next.Sequence){throw 'No se confirmó el registro.'}
    return $result
}
Export-ModuleMember -Function Read-LtTransactionJournal,Write-LtTransactionJournal,Get-LtJournalBodySha256
