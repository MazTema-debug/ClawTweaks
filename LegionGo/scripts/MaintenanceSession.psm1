# SPDX-License-Identifier: AGPL-3.0-only
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
$script:verifiedAssemblies=[Collections.Generic.Dictionary[Reflection.Assembly,object]]::new()
$script:ownedSessions=[Collections.Generic.HashSet[object]]::new()
function New-LtSessionFailure([Exception]$Failure,[bool]$ReservationMayRemain,[string]$Fallback){
    $reason=$Fallback;$cursor=$Failure
    while($cursor){
        if($cursor.Data -and $cursor.Data.Contains('LegionTweaksReason')){$reason=[string]$cursor.Data['LegionTweaksReason'];break}
        $property=$cursor.GetType().GetProperty('Reason',[Reflection.BindingFlags]'Public,Instance')
        if($property){$value=[string]$property.GetValue($cursor,$null);if($value){$reason=$value;break}}
        $cursor=$cursor.InnerException
    }
    $result=[InvalidOperationException]::new('La sesión de mantenimiento requiere revisión.',$Failure)
    $result.Data['LegionTweaksReason']=$reason;$result.Data['ReservationMayRemain']=$ReservationMayRemain
    return $result
}

# This module has no entry point and does not start the helper. Loading and
# calling a real session is permitted only after all coordinator preconditions.
function Test-LtSignatureDecision {
    param($Signature,[string]$ExpectedSigner,[object[]]$ChainStatus,[bool]$AllowUntrustedRoot)
    if(-not $Signature.SignerCertificate -or $Signature.SignerCertificate.Thumbprint -ne $ExpectedSigner){throw 'Firmante de helper incorrecto.'}
    if([string]$Signature.Status -eq 'Valid'){return 'trusted'}
    if(-not $AllowUntrustedRoot -or [string]$Signature.Status -notin @('UnknownError','NotTrusted') -or @($ChainStatus).Count -ne 1 -or $ChainStatus[0].Status -ne [Security.Cryptography.X509Certificates.X509ChainStatusFlags]::UntrustedRoot -or $Signature.StatusMessage.Trim().TrimEnd('.') -ne $ChainStatus[0].StatusInformation.Trim().TrimEnd('.')){throw 'Firma del helper inválida o confianza incierta.'}
    return 'verified-untrusted-root'
}
function Import-LtPinnedHelperAssembly {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][ValidatePattern('^[0-9A-Fa-f]{64}$')][string]$ExpectedSha256,
        [Parameter(Mandatory)][ValidatePattern('^[0-9A-Fa-f]{40}$')][string]$ExpectedSigner,
        [switch]$AllowOnlyUntrustedRoot)
    $full=[IO.Path]::GetFullPath($Path);$item=Get-Item -LiteralPath $full -Force
    if($item.PSIsContainer -or ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)){throw 'Asamblea de helper insegura.'}
    # Keep the image open without write/delete sharing during hashing, signature
    # verification and copying. Caller additionally validates all ancestors/ACLs.
    $stream=[IO.FileStream]::new($full,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read)
    try {
        if($stream.Length -le 0 -or $stream.Length -gt 67108864){throw 'Tamaño de helper inválido.'}
        $sha=[Security.Cryptography.SHA256]::Create()
        try{$actual=([BitConverter]::ToString($sha.ComputeHash($stream))).Replace('-','')}finally{$sha.Dispose()}
        if($actual -ne $ExpectedSha256){throw 'Hash del helper no coincide.'}
        $signature=Get-AuthenticodeSignature -LiteralPath $full
        $chain=[Security.Cryptography.X509Certificates.X509Chain]::new()
        try{
            $chain.ChainPolicy.RevocationMode=[Security.Cryptography.X509Certificates.X509RevocationMode]::NoCheck
            if($signature.SignerCertificate){$null=$chain.Build($signature.SignerCertificate)}
            $trust=Test-LtSignatureDecision -Signature $signature -ExpectedSigner $ExpectedSigner -ChainStatus @($chain.ChainStatus) -AllowUntrustedRoot ([bool]$AllowOnlyUntrustedRoot)
        }finally{$chain.Dispose()}
        $stream.Position=0;$buffer=[byte[]]::new([int]$stream.Length);$offset=0
        while($offset -lt $buffer.Length){$n=$stream.Read($buffer,$offset,$buffer.Length-$offset);if($n -le 0){throw 'Lectura incompleta de la asamblea.'};$offset+=$n}
        $sha=[Security.Cryptography.SHA256]::Create()
        try{$copied=([BitConverter]::ToString($sha.ComputeHash($buffer))).Replace('-','')}finally{$sha.Dispose()}
        if($copied -ne $ExpectedSha256){throw 'La asamblea cambió durante la lectura.'}
        $assembly=[Reflection.Assembly]::Load($buffer)
        $verified=[pscustomobject]@{Assembly=$assembly;TrustStatus=$trust;Sha256=$actual;Signer=$ExpectedSigner.ToUpperInvariant();AuthorizesMaintenance=$false}
        $script:verifiedAssemblies[$assembly]=[pscustomobject]@{Token=$verified;TrustStatus=$trust;Sha256=$actual;Signer=$ExpectedSigner.ToUpperInvariant()}
        return $verified
    } finally {$stream.Dispose()}
}
function Open-LtMandoSession {
    [CmdletBinding()]
    param([Parameter(Mandatory)][object]$VerifiedHelper,[Parameter(Mandatory)][string]$ExpectedSid)
    if($VerifiedHelper.AuthorizesMaintenance -ne $false -or $VerifiedHelper.TrustStatus -notin @('trusted','verified-untrusted-root') -or $VerifiedHelper.Assembly -isnot [Reflection.Assembly] -or -not $script:verifiedAssemblies.ContainsKey($VerifiedHelper.Assembly)){throw 'Verificación del helper inválida.'}
    $record=$script:verifiedAssemblies[$VerifiedHelper.Assembly]
    if(-not [object]::ReferenceEquals($record.Token,$VerifiedHelper) -or $record.TrustStatus -ne $VerifiedHelper.TrustStatus -or $record.Sha256 -ne $VerifiedHelper.Sha256 -or $record.Signer -ne $VerifiedHelper.Signer){throw 'La verificación del helper cambió.'}
    $current=[Security.Principal.WindowsIdentity]::GetCurrent()
    try {if(-not $current.User -or $current.User.Value -ne $ExpectedSid -or -not [Security.Principal.WindowsPrincipal]::new($current).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)){throw 'La identidad efectiva no es la esperada y elevada.'}}finally{$current.Dispose()}
    $type=$VerifiedHelper.Assembly.GetType('LegionTweaks.FpsY3MaintenanceSession',$true)
    try{$session=$type.GetMethod('BeginCurrentIdentity',[Reflection.BindingFlags]'Public,Static').Invoke($null,[object[]]@($ExpectedSid))}
    catch{throw (New-LtSessionFailure $_.Exception $true 'session_begin_uncertain')}
    try {
        if($session.AuthorizesMaintenance -ne $false -or $session.Classification -notin @('absent','restored_with_original')){throw 'Clasificación de sesión inesperada.'}
        if(-not $session.Revalidate()){throw 'Snapshot de sesión cambió.'}
        $null=$script:ownedSessions.Add($session)
        return $session
    } catch {$failure=$_.Exception;if($session){try{$session.Dispose()}catch{}};throw (New-LtSessionFailure $failure $true 'session_validation_uncertain')}
}
function Test-LtMandoSessions {
    [CmdletBinding()]
    param([Parameter(Mandatory)][object[]]$Sessions)
    if($Sessions.Count -eq 0){throw 'No hay sesiones Y3.'}
    foreach($session in $Sessions){
        if(-not $session -or -not $script:ownedSessions.Contains($session) -or $session.GetType().FullName -ne 'LegionTweaks.FpsY3MaintenanceSession' -or $session.AuthorizesMaintenance -ne $false){throw 'Sesión Y3 ajena o inválida.'}
        try{if(-not $session.Revalidate()){throw [InvalidOperationException]::new('El snapshot Y3 cambió.')}}
        catch{throw (New-LtSessionFailure $_.Exception $true 'session_revalidation_uncertain')}
    }
    return $true
}
function Close-LtMandoSessions {
    [CmdletBinding()]
    param([Parameter(Mandatory)][object[]]$Sessions)
    if($Sessions.Count -eq 0){throw 'No hay sesiones Y3.'}
    $failures=[Collections.Generic.List[Exception]]::new()
    foreach($session in $Sessions){
        if(-not $session -or -not $script:ownedSessions.Contains($session)){$failures.Add([InvalidOperationException]::new('Sesión Y3 ajena al coordinador.'));continue}
        try{$session.Release()}catch{$failures.Add((New-LtSessionFailure $_.Exception $true 'session_release_uncertain'))}
        try{$session.Dispose();$null=$script:ownedSessions.Remove($session)}catch{$failures.Add((New-LtSessionFailure $_.Exception $true 'session_dispose_uncertain'))}
    }
    if($failures.Count){throw [AggregateException]::new('No se confirmó la liberación de una sesión Y3.',$failures)}
    return $true
}
function Preserve-LtMandoSessions {
    [CmdletBinding()]
    param([Parameter(Mandatory)][object[]]$Sessions)
    if($Sessions.Count -eq 0){throw 'No hay sesiones Y3.'}
    foreach($session in $Sessions){
        if(-not $session -or -not $script:ownedSessions.Contains($session)){throw 'Sesión Y3 ajena al coordinador.'}
        try{$session.Dispose();$null=$script:ownedSessions.Remove($session)}
        catch{throw (New-LtSessionFailure $_.Exception $true 'session_dispose_uncertain')}
    }
    return $true
}
Export-ModuleMember -Function Import-LtPinnedHelperAssembly,Open-LtMandoSession,Test-LtMandoSessions,Close-LtMandoSessions,Preserve-LtMandoSessions
