# SPDX-License-Identifier: AGPL-3.0-only
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
Import-Module (Join-Path $PSScriptRoot 'MaintenancePackage.psm1') -ErrorAction Stop
Import-Module (Join-Path $PSScriptRoot 'MaintenancePayload.psm1') -ErrorAction Stop
Import-Module (Join-Path $PSScriptRoot 'MaintenanceProtection.psm1') -ErrorAction Stop
function New-LtPreparedTarget {
    [CmdletBinding()]
    param([Parameter(Mandatory)][System.Collections.IDictionary]$Target,[Parameter(Mandatory)][string]$Staging,
          [Parameter(Mandatory)][scriptblock]$AssertProtection,[System.Collections.IDictionary]$Operations)
    foreach($key in @('Archive','Extracted','ArchiveSha256','HelperPath','HelperSha256','QamPath','QamSha256','WidgetPath','Signer')){if(-not $Target.Contains($key) -or -not $Target[$key]){throw "Falta pin de staging $key."}}
    $package=Get-Command Test-LtPinnedPackage -CommandType Function -ErrorAction Stop
    $signatures=Get-Command Test-LtPinnedComponentSignatures -CommandType Function -ErrorAction Stop
    $setAcl=Get-Command Set-LtStagingAcl -CommandType Function -ErrorAction Stop
    if(-not $Operations){$Operations=@{
        Package={param($t)
            $verified=& $package -Archive $t.Archive -Extracted $t.Extracted -ExpectedSha256 $t.ArchiveSha256
            if($verified.AuthorizesMaintenance -ne $false -or $verified.ArchiveSha256 -ne $t.ArchiveSha256 -or (Get-FileHash -LiteralPath $t.HelperPath -Algorithm SHA256).Hash -ne $t.HelperSha256 -or (Get-FileHash -LiteralPath $t.QamPath -Algorithm SHA256).Hash -ne $t.QamSha256){throw 'Paquete de staging no coincide.'}
            $s=@(& $signatures -Paths @($t.HelperPath,$t.QamPath,$t.WidgetPath) -ExpectedSigner $t.Signer -AllowOnlyUntrustedRoot)
            if($s.Count -ne 3){throw 'Firmas de staging incompletas.'};return $true
        }.GetNewClosure()
        ApplyAcl={param($path)& $setAcl -Tree $path}.GetNewClosure()
    }}
    if($Operations.Package -isnot [scriptblock] -or $Operations.ApplyAcl -isnot [scriptblock]){throw 'Operaciones de staging inválidas.'}
    $verify={param($unused)$r=@(& $Operations.Package $Target);return ($r.Count -eq 1 -and $r[0] -is [bool] -and $r[0])}.GetNewClosure()
    return (New-LtDestinationTree -Extracted $Target.Extracted -Tree $Staging -Family 'MazTema.LegionTweaks_wsckx05r95yk0' -Signer $Target.Signer -AssertPackage $verify -AssertProtection $AssertProtection -ApplyAndValidateAcl $Operations.ApplyAcl)
}
Export-ModuleMember -Function New-LtPreparedTarget
