# SPDX-License-Identifier: AGPL-3.0-only
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
Import-Module (Join-Path $PSScriptRoot 'MaintenanceScope.psm1') -ErrorAction Stop
Import-Module (Join-Path $PSScriptRoot 'MaintenanceUserData.psm1') -ErrorAction Stop
function New-LtProtectionRouter {
    [CmdletBinding()]
    param([Parameter(Mandatory)]$Guard,[Parameter(Mandatory)][string]$ExpectedSid,[System.Collections.IDictionary]$Operations)
    $scope=Get-Command Test-LtScopeGuard -CommandType Function -ErrorAction Stop
    $user=Get-Command Test-LtWidgetDataProtection -CommandType Function -ErrorAction Stop
    if(-not $Operations){
        $root=Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::LocalApplicationData)) 'Packages\MazTema.LegionTweaks_wsckx05r95yk0'
        $y3=Join-Path (Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::CommonApplicationData)) 'LegionTweaks') $ExpectedSid
        $Operations=@{WidgetRoot=$root;Y3Root=$y3;TestScope={param($guard,$path,$exclude)& $scope -Guard $guard -Paths @($path) -ExcludeY3AdmissionGuard:$exclude}.GetNewClosure();TestUser={param($path,$sid)& $user -Path $path -ExpectedSid $sid}.GetNewClosure()}
    }
    if(-not $Operations.Contains('WidgetRoot') -or -not [IO.Path]::IsPathRooted([string]$Operations.WidgetRoot) -or $Operations.TestScope -isnot [scriptblock] -or $Operations.TestUser -isnot [scriptblock]){throw 'Enrutador de protección inválido.'}
    $roots=@((Join-Path $Operations.WidgetRoot 'Settings'),(Join-Path $Operations.WidgetRoot 'LocalState'))
    $callback={param($p1,$p2,$p3)
        $paths=@($p1,$p2,$p3|Where-Object {$_})
        if($paths.Count -eq 0){throw 'Falta ruta de protección.'}
        foreach($path in $paths){
            $full=[IO.Path]::GetFullPath([string]$path).TrimEnd('\');$widget=$false
            foreach($root in $roots){$r=[IO.Path]::GetFullPath($root).TrimEnd('\');if([string]::Equals($full,$r,[StringComparison]::OrdinalIgnoreCase) -or $full.StartsWith($r+'\',[StringComparison]::OrdinalIgnoreCase)){$widget=$true;break}}
            $isY3=$Operations.Contains('Y3Root') -and [string]::Equals($full,[IO.Path]::GetFullPath([string]$Operations.Y3Root).TrimEnd('\'),[StringComparison]::OrdinalIgnoreCase)
            $decision=if($widget){@(& $Operations.TestUser $full $ExpectedSid)}else{@(& $Operations.TestScope $Guard $full $isY3)}
            if($decision.Count -ne 1 -or $decision[0] -isnot [bool] -or -not $decision[0]){throw 'Protección de ruta no confirmada.'}
        }
        return $true
    }.GetNewClosure()
    return $callback
}
Export-ModuleMember -Function New-LtProtectionRouter
