# SPDX-License-Identifier: AGPL-3.0-only
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
Import-Module (Join-Path $PSScriptRoot 'MaintenanceRollback.psm1') -ErrorAction Stop
function Get-LtProductionMetadata {
    [CmdletBinding()]
    param([Parameter(Mandatory)][System.Collections.IDictionary]$Configuration,[System.Collections.IDictionary]$Operations)
    foreach($key in @('ExpectedSid','ExpectedHelper','SourceSigner','TargetSigner','SourceVersion','AssertScope')){if(-not $Configuration.Contains($key) -or -not $Configuration[$key]){throw "Falta metadato $key."}}
    if($Configuration.SourceSigner -cnotmatch '^[A-F0-9]{40}$' -or $Configuration.TargetSigner -cnotmatch '^[A-F0-9]{40}$' -or $Configuration.AssertScope -isnot [scriptblock]){throw 'Identidad de metadatos inválida.'}
    $taskCheck=Get-Command Test-LtRollbackTaskXml -CommandType Function -ErrorAction Stop
    if(-not $Operations){$Operations=@{
        Package={@(Get-AppxPackage -Name 'MazTema.LegionTweaks' -ErrorAction Stop)}
        Task={Get-ScheduledTask -TaskName 'LegionTweaks-Z1E' -ErrorAction Stop}
        TaskXml={Export-ScheduledTask -TaskName 'LegionTweaks-Z1E' -ErrorAction Stop}
        Certificate={param($thumb)@(Get-ChildItem -LiteralPath 'Cert:\LocalMachine\TrustedPeople' -ErrorAction Stop|Where-Object {$_.Thumbprint -eq $thumb})}
    }}
    foreach($key in @('Package','Task','TaskXml','Certificate')){if(-not $Operations.Contains($key) -or $Operations[$key] -isnot [scriptblock]){throw "Falta lectura de metadatos $key."}}
    $check=@(& $Configuration.AssertScope $null);if($check.Count -ne 1 -or $check[0] -isnot [bool] -or -not $check[0]){throw 'Ámbito de metadatos incierto.'}
    $packages=@(& $Operations.Package)
    if($packages.Count -ne 1 -or $packages[0].PackageFamilyName -ne 'MazTema.LegionTweaks_wsckx05r95yk0' -or $packages[0].Publisher -ne 'CN=LegionTweaks Dev' -or [string]$packages[0].Architecture -ne 'X64' -or [version]$packages[0].Version -ne [version]$Configuration.SourceVersion -or [string]$packages[0].Status -ne 'Ok' -or -not $packages[0].PackageFullName){throw 'Paquete de origen de metadatos incierto.'}
    $tasks=@(& $Operations.Task);$xml=@(& $Operations.TaskXml)
    if($tasks.Count -ne 1 -or $xml.Count -ne 1 -or $tasks[0].Settings.Enabled -isnot [bool]){throw 'Tarea de origen incierta.'}
    $enabled=[bool]$tasks[0].Settings.Enabled
    $null=& $taskCheck -Xml ([string]$xml[0]) -ExpectedSid $Configuration.ExpectedSid -ExpectedHelper $Configuration.ExpectedHelper -ExpectedEnabled $enabled
    $sourceCert=@(& $Operations.Certificate $Configuration.SourceSigner);$targetCert=@(& $Operations.Certificate $Configuration.TargetSigner)
    if($sourceCert.Count -gt 1 -or $targetCert.Count -gt 1){throw 'Confianza de certificado ambigua.'}
    if(($sourceCert.Count -eq 1 -and $sourceCert[0].Thumbprint -ne $Configuration.SourceSigner) -or ($targetCert.Count -eq 1 -and $targetCert[0].Thumbprint -ne $Configuration.TargetSigner)){throw 'Confianza de certificado no coincide.'}
    $check=@(& $Configuration.AssertScope $null);if($check.Count -ne 1 -or $check[0] -isnot [bool] -or -not $check[0]){throw 'Ámbito de metadatos cambió.'}
    return @{TaskXml=[string]$xml[0];TaskWasEnabled=$enabled;PackageFullName=[string]$packages[0].PackageFullName;SourceCertificateWasPresent=($sourceCert.Count -eq 1);TargetCertificateWasPresent=($targetCert.Count -eq 1)}
}
Export-ModuleMember -Function Get-LtProductionMetadata
