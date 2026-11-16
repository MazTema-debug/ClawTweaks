# SPDX-License-Identifier: AGPL-3.0-only
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
Import-Module (Join-Path $PSScriptRoot 'MaintenanceSource.psm1') -ErrorAction Stop
Import-Module (Join-Path $PSScriptRoot 'MaintenanceRollback.psm1') -ErrorAction Stop
function Get-LtEvidenceHash([byte[]]$Bytes){
    $sha=[Security.Cryptography.SHA256]::Create();try{return ([BitConverter]::ToString($sha.ComputeHash($Bytes))).Replace('-','')}finally{$sha.Dispose()}
}

# Read-only snapshot of the current account's AppX, task, public trust and
# startup suppression. Y3 journal and originals deliberately do not enter it:
# they may legitimately change after the publication boundary.
function Get-LtWindowsPublicationFingerprint {
    [CmdletBinding()]
    param([Parameter(Mandatory)][System.Collections.IDictionary]$Configuration,[System.Collections.IDictionary]$Operations,[switch]$IncludeStartupState)
    foreach($k in @('ExpectedSid','WidgetPath','WidgetSha256','Version','Signer','HelperPath','AssertProtection')){if(-not $Configuration.Contains($k) -or -not $Configuration[$k]){throw "Falta pin externo $k."}}
    if($Configuration.WidgetSha256 -cnotmatch '^[A-F0-9]{64}$' -or $Configuration.Signer -cnotmatch '^[A-F0-9]{40}$' -or $Configuration.AssertProtection -isnot [scriptblock]){throw 'Pins externos inválidos.'}
    $id=[Security.Principal.WindowsIdentity]::GetCurrent();try{if(-not $id.User -or $id.User.Value -ne $Configuration.ExpectedSid){throw 'Identidad externa no coincide.'}}finally{$id.Dispose()}
    $expectedHelper=Join-Path $env:ProgramFiles 'LegionTweaks-Z1E\LegionTweaks.Helper.exe'
    if(-not [string]::Equals([IO.Path]::GetFullPath($Configuration.HelperPath),[IO.Path]::GetFullPath($expectedHelper),[StringComparison]::OrdinalIgnoreCase)){throw 'Ruta externa de helper inesperada.'}
    $verifyWidget=Get-Command Test-LtInstalledWidgetContents -CommandType Function -ErrorAction Stop
    if(-not $Operations){$Operations=@{
        Package={@(Get-AppxPackage -Name 'MazTema.LegionTweaks' -ErrorAction Stop)}
        TaskXml={Export-ScheduledTask -TaskName 'LegionTweaks-Z1E' -ErrorAction Stop}
        Certificate={param($thumb)@(Get-ChildItem -LiteralPath 'Cert:\LocalMachine\TrustedPeople' -ErrorAction Stop|Where-Object {$_.Thumbprint -eq $thumb})}
        VerifyWidget={param($widget,$location)& $verifyWidget -PackagePath $widget -InstallLocation $location}.GetNewClosure()
        Startup={param($sid,$protect)
            $dir=Join-Path (Join-Path $env:ProgramData 'LegionTweaks') $sid;$path=Join-Path $dir 'controller-startup-suppress-once.json'
            $safe=@(& $protect $dir);if($safe.Count -ne 1 -or $safe[0] -isnot [bool] -or -not $safe[0]){throw 'Directorio de inicio no protegido.'}
            if(-not [IO.File]::Exists($path)){if([IO.Directory]::Exists($path)){throw 'Supresión de inicio es directorio.'};return 'absent'}
            $item=Get-Item -LiteralPath $path -Force -ErrorAction Stop
            if($item.PSIsContainer -or ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)){throw 'Supresión de inicio insegura.'}
            $stream=[IO.FileStream]::new($path,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read)
            try{if($stream.Length -gt 1024){throw 'Supresión de inicio fuera de tamaño.'};$reader=[IO.StreamReader]::new($stream,[Text.UTF8Encoding]::new($false,$true),$false);try{return $reader.ReadToEnd()}finally{$reader.Dispose()}}finally{$stream.Dispose()}
        }
    }}
    foreach($key in @('Package','TaskXml','Certificate','VerifyWidget','Startup')){if(-not $Operations.Contains($key) -or $Operations[$key] -isnot [scriptblock]){throw "Falta observación $key."}}
    $check=@(& $Configuration.AssertProtection $Configuration.WidgetPath)
    if($check.Count -ne 1 -or $check[0] -isnot [bool] -or -not $check[0] -or (Get-FileHash -LiteralPath $Configuration.WidgetPath -Algorithm SHA256).Hash -ne $Configuration.WidgetSha256){throw 'Protección o hash del widget incierto.'}
    $packages=@(& $Operations.Package)
    if($packages.Count -ne 1 -or $packages[0].PackageFamilyName -ne 'MazTema.LegionTweaks_wsckx05r95yk0' -or $packages[0].Publisher -ne 'CN=LegionTweaks Dev' -or [string]$packages[0].Architecture -ne 'X64' -or [version]$packages[0].Version -ne [version]$Configuration.Version -or [string]$packages[0].Status -ne 'Ok' -or -not $packages[0].PackageFullName -or -not $packages[0].InstallLocation){throw 'Paquete externo no coincide.'}
    $check=@(& $Configuration.AssertProtection ([string]$packages[0].InstallLocation))
    if($check.Count -ne 1 -or $check[0] -isnot [bool] -or -not $check[0]){throw 'Instalación del widget no protegida.'}
    $widgetOk=@(& $Operations.VerifyWidget $Configuration.WidgetPath ([string]$packages[0].InstallLocation))
    if($widgetOk.Count -ne 1 -or $widgetOk[0] -isnot [bool] -or -not $widgetOk[0]){throw 'Contenido instalado del widget incierto.'}
    $xml=@(& $Operations.TaskXml);if($xml.Count -ne 1){throw 'Tarea externa ambigua.'}
    $null=Test-LtRollbackTaskXml -Xml ([string]$xml[0]) -ExpectedSid $Configuration.ExpectedSid -ExpectedHelper $Configuration.HelperPath -ExpectedEnabled $true
    $document=[Xml.XmlDocument]::new();$document.PreserveWhitespace=$false;$document.XmlResolver=$null;$document.LoadXml([string]$xml[0])
    $certs=@(& $Operations.Certificate $Configuration.Signer)
    if($certs.Count -ne 1 -or $certs[0].Thumbprint -ne $Configuration.Signer -or -not $certs[0].RawData){throw 'Confianza externa incierta.'}
    $startup=@(& $Operations.Startup $Configuration.ExpectedSid $Configuration.AssertProtection)
    if($startup.Count -ne 1 -or [string]$startup[0] -cnotin @('absent','{"Skip":true,"Version":1}')){throw 'Supresión externa inesperada.'}
    if($Configuration.Contains('RequireStartup') -and $Configuration.RequireStartup -eq $true -and [string]$startup[0] -cne '{"Skip":true,"Version":1}'){throw 'Falta supresión de inicio antes de publicar.'}
    # The ready fingerprint is created only when RequireStartup confirms the
    # exact marker. After publication the helper may consume it legitimately.
    # Both allowed observations bind to that same pre-publication requirement;
    # neither inspection nor reconciliation recreates the consumed marker.
    $body=[ordered]@{Version=1;Sid=$Configuration.ExpectedSid;PackageFullName=[string]$packages[0].PackageFullName;InstallLocation=[string]$packages[0].InstallLocation;
        WidgetSha256=$Configuration.WidgetSha256;TaskXml=$document.OuterXml;Signer=$Configuration.Signer;CertificateSha256=(Get-LtEvidenceHash ([byte[]]$certs[0].RawData));Startup='{"Skip":true,"Version":1}'}
    $json=ConvertTo-Json -InputObject $body -Compress
    $digest=Get-LtEvidenceHash ([Text.Encoding]::UTF8.GetBytes($json))
    if($IncludeStartupState){return [pscustomobject]@{Sha256=$digest;StartupState=$(if([string]$startup[0] -eq 'absent'){'absent'}else{'present'});AuthorizesMaintenance=$false}}
    return $digest
}
Export-ModuleMember -Function Get-LtWindowsPublicationFingerprint
