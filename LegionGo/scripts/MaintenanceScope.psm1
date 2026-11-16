# SPDX-License-Identifier: AGPL-3.0-only
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
Import-Module (Join-Path $PSScriptRoot 'MaintenanceProtection.psm1') -ErrorAction Stop
$script:guards=[Collections.Generic.Dictionary[object,object]]::new()
function New-LtScopeGuard {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string[]]$Anchors,[Parameter(Mandatory)][string[]]$AllowedRoots,[scriptblock]$Snapshot)
    if($Anchors.Count -eq 0 -or $AllowedRoots.Count -eq 0){throw 'Falta alcance protegido.'}
    foreach($path in @($Anchors)+@($AllowedRoots)){if(-not $path -or -not [IO.Path]::IsPathRooted($path)){throw 'Ruta de alcance no absoluta.'}}
    $anchors=@($Anchors|ForEach-Object {[IO.Path]::GetFullPath($_).TrimEnd('\')}|Sort-Object -Unique)
    $roots=@($AllowedRoots|ForEach-Object {[IO.Path]::GetFullPath($_).TrimEnd('\')}|Sort-Object -Unique)
    if($anchors.Count -ne $Anchors.Count -or $roots.Count -ne $AllowedRoots.Count){throw 'Rutas de alcance duplicadas.'}
    if(-not $Snapshot){
        $getSnapshot=Get-Command Get-LtProtectedSnapshot -CommandType Function -ErrorAction Stop
        $Snapshot={param($paths,$shallow,$exclude)& $getSnapshot -Roots $paths -WithoutDescendants:$shallow -ExcludeY3AdmissionGuard:$exclude}.GetNewClosure()
    }
    $base=@(& $Snapshot $anchors $true)
    if($base.Count -ne 1 -or $base[0].AuthorizesMaintenance -ne $false -or $base[0].ProtectionSha256 -notmatch '^[A-F0-9]{64}$'){throw 'Anclas protegidas inciertas.'}
    $token=[pscustomobject]@{AuthorizesMaintenance=$false}
    $script:guards.Add($token,[pscustomobject]@{Anchors=$anchors;Roots=$roots;Snapshot=$Snapshot;Baseline=$base[0].ProtectionSha256;Token=$token})
    return $token
}
function Test-LtScopeGuard {
    [CmdletBinding()]
    param([Parameter(Mandatory)]$Guard,[string[]]$Paths=@(),[switch]$ExcludeY3AdmissionGuard)
    if(-not $script:guards.ContainsKey($Guard)){throw 'Alcance ajeno o inválido.'}
    $entry=$script:guards[$Guard]
    if(-not [object]::ReferenceEquals($entry.Token,$Guard) -or $Guard.AuthorizesMaintenance -ne $false){throw 'Token de alcance modificado.'}
    $current=@(& $entry.Snapshot $entry.Anchors $true)
    if($current.Count -ne 1 -or $current[0].AuthorizesMaintenance -ne $false -or $current[0].ProtectionSha256 -ne $entry.Baseline){throw 'Cambió identidad o ACL de un ancla.'}
    foreach($path in $Paths){
        if(-not $path -or -not [IO.Path]::IsPathRooted($path)){throw 'Ruta fuera del alcance.'}
        $full=[IO.Path]::GetFullPath($path).TrimEnd('\');$allowed=$false
        foreach($root in $entry.Roots){if([string]::Equals($full,$root,[StringComparison]::OrdinalIgnoreCase) -or $full.StartsWith($root+'\',[StringComparison]::OrdinalIgnoreCase)){$allowed=$true;break}}
        if(-not $allowed){throw 'Ruta fuera del alcance autorizado.'}
        $existing=$full;$missing=$false
        while(-not [IO.File]::Exists($existing) -and -not [IO.Directory]::Exists($existing)){
            $missing=$true
            $parent=[IO.Path]::GetDirectoryName($existing);if(-not $parent -or $parent -eq $existing){throw 'No hay ancestro de alcance verificable.'};$existing=$parent
        }
        $state=@(& $entry.Snapshot @($existing) $missing ([bool]$ExcludeY3AdmissionGuard))
        if($state.Count -ne 1 -or $state[0].AuthorizesMaintenance -ne $false -or $state[0].ProtectionSha256 -notmatch '^[A-F0-9]{64}$'){throw 'Protección de ruta incierta.'}
    }
    return $true
}
Export-ModuleMember -Function New-LtScopeGuard,Test-LtScopeGuard
