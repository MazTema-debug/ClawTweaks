# SPDX-License-Identifier: AGPL-3.0-only
Set-StrictMode -Version Latest
function Get-LtCliIdentity {
    $identity=[Security.Principal.WindowsIdentity]::GetCurrent()
    try{
        if(-not $identity.User -or -not [Security.Principal.WindowsPrincipal]::new($identity).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)){throw 'La reconciliación requiere identidad elevada.'}
        return [pscustomobject]@{Sid=$identity.User.Value;Name=$identity.Name}
    }finally{$identity.Dispose()}
}
function Get-LtCliLocations {
    return [pscustomobject]@{CommonData=[Environment]::GetFolderPath([Environment+SpecialFolder]::CommonApplicationData);ProgramFiles=[Environment]::GetFolderPath([Environment+SpecialFolder]::ProgramFiles)}
}
Export-ModuleMember -Function Get-LtCliIdentity,Get-LtCliLocations
