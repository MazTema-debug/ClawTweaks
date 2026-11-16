# SPDX-License-Identifier: AGPL-3.0-only
Set-StrictMode -Version Latest
function Get-LtProductionPaths {
    [CmdletBinding()]
    param([Parameter(Mandatory)]$Request,[Parameter(Mandatory)][string]$ExpectedSid,[System.Collections.IDictionary]$Locations)
    if($Request.AuthorizesMaintenance -ne $false -or [string]$Request.Id -cnotmatch '^[a-f0-9]{32}$' -or $Request.Direction -cnotin @('Update','Return49')){throw 'Solicitud no apta para derivar rutas.'}
    try{if([Security.Principal.SecurityIdentifier]::new($ExpectedSid).Value -cne $ExpectedSid){throw 'SID no canónico.'}}catch{throw 'SID inválido.'}
    if(-not $Locations){$Locations=@{Common=[Environment]::GetFolderPath([Environment+SpecialFolder]::CommonApplicationData);Local=[Environment]::GetFolderPath([Environment+SpecialFolder]::LocalApplicationData);ProgramFiles=[Environment]::GetFolderPath([Environment+SpecialFolder]::ProgramFiles)}}
    foreach($k in @('Common','Local','ProgramFiles')){if(-not $Locations.Contains($k) -or -not [IO.Path]::IsPathRooted([string]$Locations[$k])){throw 'Ubicación base inválida.'}}
    $common=[IO.Path]::GetFullPath([string]$Locations.Common).TrimEnd('\');$program=[IO.Path]::GetFullPath([string]$Locations.ProgramFiles).TrimEnd('\');$local=[IO.Path]::GetFullPath([string]$Locations.Local).TrimEnd('\')
    $base=Join-Path $common 'LegionTweaks\maintenance-v1';$transaction=Join-Path (Join-Path $base 'transactions') $Request.Id
    $widget=Join-Path $local 'Packages\MazTema.LegionTweaks_wsckx05r95yk0'
    return [pscustomobject]@{Base=$base;PackageStore=(Join-Path $base 'packages');Transaction=$transaction;Lease=(Join-Path $base 'coordinator-v1.lock');
        Journal=(Join-Path $transaction 'flow-journal.json');Ready=(Join-Path $transaction 'publish-ready.json');Backup=(Join-Path $transaction 'backupset');
        Live=(Join-Path $program 'LegionTweaks-Z1E');Staging=(Join-Path $program ('LegionTweaks-Z1E-stage-'+$Request.Id));Previous=(Join-Path $program ('LegionTweaks-Z1E-previous-'+$Request.Id));
        Failed=(Join-Path $program ('LegionTweaks-Z1E-failed-'+$Request.Id));Restore=(Join-Path $program ('LegionTweaks-Z1E-rollback-'+$Request.Id));
        UserData=(Join-Path (Join-Path $common 'LegionTweaks') $ExpectedSid);WidgetSettings=(Join-Path $widget 'Settings');WidgetLocalState=(Join-Path $widget 'LocalState');AuthorizesMaintenance=$false}
}
function Assert-LtProductionVolumes {
    [CmdletBinding()]
    param([Parameter(Mandatory)]$Paths,[scriptblock]$ReadDrive)
    if(-not $ReadDrive){$ReadDrive={param($root)[IO.DriveInfo]::new($root)}}
    $seen=[Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach($path in @($Paths.Base,$Paths.Live,$Paths.UserData,$Paths.WidgetSettings)){
        if(-not $path -or -not [IO.Path]::IsPathRooted([string]$path)){throw 'Ruta de volumen inválida.'}
        $root=[IO.Path]::GetPathRoot([IO.Path]::GetFullPath([string]$path))
        if($seen.Add($root)){
            $drive=@(& $ReadDrive $root)
            if($drive.Count -ne 1 -or $drive[0].DriveType -ne [IO.DriveType]::Fixed -or $drive[0].IsReady -ne $true -or [string]$drive[0].DriveFormat -ine 'NTFS'){throw 'Mantenimiento requiere volúmenes NTFS locales fijos.'}
        }
    }
    return $true
}
Export-ModuleMember -Function Get-LtProductionPaths,Assert-LtProductionVolumes
