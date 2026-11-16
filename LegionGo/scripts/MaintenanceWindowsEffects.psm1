# SPDX-License-Identifier: AGPL-3.0-only
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
function Test-LtSafeDecision([scriptblock]$Checker,$Context){
    $values=@(& $Checker $Context)
    if($values.Count -ne 1 -or $values[0] -isnot [bool] -or -not $values[0]){throw 'La transacción dejó de estar protegida.'}
    return $true
}
function Get-LtPrincipalSid([string]$Value){
    if(-not $Value){throw 'Identidad de tarea ausente.'}
    try{return [Security.Principal.SecurityIdentifier]::new($Value).Value}
    catch {try{return ([Security.Principal.NTAccount]::new($Value)).Translate([Security.Principal.SecurityIdentifier]).Value}catch{throw 'No se pudo resolver la identidad de la tarea.'}}
}
function Select-LtMissingDependencies {
    [CmdletBinding()]
    param([AllowEmptyCollection()][string[]]$Paths=@(),[scriptblock]$GetFrameworks)
    if(-not $GetFrameworks){$GetFrameworks={param($name) Get-AppxPackage -Name $name -PackageTypeFilter Framework -ErrorAction Stop}}
    Add-Type -AssemblyName System.IO.Compression,System.IO.Compression.FileSystem
    foreach($path in $Paths){
        $archive=[IO.Compression.ZipFile]::OpenRead($path)
        try{
            $entry=$archive.GetEntry('AppxManifest.xml');if(-not $entry){throw 'Dependencia sin manifiesto.'}
            $reader=[IO.StreamReader]::new($entry.Open());try{[xml]$manifest=$reader.ReadToEnd()}finally{$reader.Dispose()}
        }finally{$archive.Dispose()}
        $identity=$manifest.Package.Identity;$architecture=[string]$identity.ProcessorArchitecture
        if(-not $identity.Name -or -not $identity.Publisher -or -not $identity.Version -or $manifest.Package.Properties.Framework -ne 'true' -or $architecture -notin @('x64','neutral')){throw 'Dependencia de framework inválida.'}
        $required=[version]$identity.Version
        $healthy=@(& $GetFrameworks ([string]$identity.Name) | Where-Object {$_.IsFramework -and $_.Status -eq 'Ok' -and $_.Publisher -eq [string]$identity.Publisher -and [string]$_.Architecture -in @($architecture,'Neutral') -and [version]$_.Version -ge $required})
        if($healthy.Count -eq 0){$path}
    }
}

# Real Windows effect adapters. These are NOT a complete transaction or an
# entry point. The coordinator must establish identity, pins, recovery,
# backup, quiescence and persistent exclusion before invoking them.
function New-LtWindowsEffects {
    [CmdletBinding()]
    param([Parameter(Mandatory)][System.Collections.IDictionary]$Configuration,
          [System.Collections.IDictionary]$Operations)
    $required=@('CertificatePath','Signer','WidgetPath','WidgetSha256','PackageName','PackageFamilyName','Publisher','Version','TaskName','User','HelperPath','AssertSafe')
    foreach($key in $required){if(-not $Configuration.Contains($key) -or $null -eq $Configuration[$key]){throw "Falta configuración $key."}}
    if($Configuration.AssertSafe -isnot [scriptblock] -or $Configuration.Signer -notmatch '^[0-9A-Fa-f]{40}$' -or $Configuration.WidgetSha256 -notmatch '^[0-9A-Fa-f]{64}$' -or $Configuration.PackageName -ne 'MazTema.LegionTweaks' -or $Configuration.PackageFamilyName -ne 'MazTema.LegionTweaks_wsckx05r95yk0' -or $Configuration.Publisher -ne 'CN=LegionTweaks Dev' -or $Configuration.TaskName -ne 'LegionTweaks-Z1E'){throw 'Configuración de efecto inválida.'}
    $expectedHelper=Join-Path $env:ProgramFiles 'LegionTweaks-Z1E\LegionTweaks.Helper.exe'
    if(-not [string]::Equals([IO.Path]::GetFullPath($Configuration.HelperPath),[IO.Path]::GetFullPath($expectedHelper),[StringComparison]::OrdinalIgnoreCase)){throw 'Ruta de helper no permitida.'}
    $current=[Security.Principal.WindowsIdentity]::GetCurrent()
    try{if($current.Name -ne $Configuration.User -or -not $current.User){throw 'La cuenta de tarea no coincide con la identidad actual.'};$currentSid=$current.User.Value}finally{$current.Dispose()}
    if(-not $Operations){
        $Operations=@{
            GetCertificate={param($thumb) @(Get-ChildItem -LiteralPath 'Cert:\LocalMachine\TrustedPeople' -ErrorAction Stop | Where-Object {$_.Thumbprint -eq $thumb})}
            ImportCertificate={param($file) Import-Certificate -FilePath $file -CertStoreLocation 'Cert:\LocalMachine\TrustedPeople' -ErrorAction Stop | Out-Null}
            GetSignature={param($file) Get-AuthenticodeSignature -LiteralPath $file}
            GetFrameworks={param($name) Get-AppxPackage -Name $name -PackageTypeFilter Framework -ErrorAction Stop}
            AddPackage={param($file,$dependencies) if(@($dependencies).Count){Add-AppxPackage -Path $file -DependencyPath $dependencies -ForceUpdateFromAnyVersion -ErrorAction Stop}else{Add-AppxPackage -Path $file -ForceUpdateFromAnyVersion -ErrorAction Stop}}
            GetPackage={param($name) @(Get-AppxPackage -Name $name -ErrorAction Stop)}
            RegisterTask={param($c)
                $action=New-ScheduledTaskAction -Execute $c.HelperPath -WorkingDirectory (Split-Path -Parent $c.HelperPath)
                $trigger=New-ScheduledTaskTrigger -AtLogOn -User $c.User
                $principal=New-ScheduledTaskPrincipal -UserId $c.User -LogonType Interactive -RunLevel Highest
                $settings=New-ScheduledTaskSettingsSet -Disable -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -ExecutionTimeLimit ([TimeSpan]::Zero) -RestartCount 3 -RestartInterval (New-TimeSpan -Minutes 1) -MultipleInstances IgnoreNew
                Register-ScheduledTask -TaskName $c.TaskName -Action $action -Trigger $trigger -Principal $principal -Settings $settings -Force -ErrorAction Stop | Out-Null
            }
            EnableTask={param($name) Enable-ScheduledTask -TaskName $name -ErrorAction Stop | Out-Null}
            GetTask={param($name) Get-ScheduledTask -TaskName $name -ErrorAction Stop}
        }
    }
    $checkSafe=${function:Test-LtSafeDecision};$principalSid=${function:Get-LtPrincipalSid}
    foreach($key in @('GetCertificate','ImportCertificate','GetSignature','GetFrameworks','AddPackage','GetPackage','RegisterTask','EnableTask','GetTask')){if(-not $Operations.Contains($key) -or $Operations[$key] -isnot [scriptblock]){throw "Falta operación Windows $key."}}
    $selectDependencies=${function:Select-LtMissingDependencies}
    $certificate={param($ctx)
        $null=& $checkSafe $Configuration.AssertSafe $ctx
        $certificate=[Security.Cryptography.X509Certificates.X509Certificate2]::new([string]$Configuration.CertificatePath)
        if($certificate.Thumbprint -ne $Configuration.Signer -or $certificate.Subject -ne $Configuration.Publisher){throw 'Certificado no coincide con la candidata.'}
        $existing=& $Operations.GetCertificate $Configuration.Signer
        $ctx['CertificateWasPresent']=[bool]$existing
        if(-not $existing){& $Operations.ImportCertificate $Configuration.CertificatePath}
        $after=& $Operations.GetCertificate $Configuration.Signer
        if(-not $after -or $after.Thumbprint -ne $Configuration.Signer){throw 'No se confirmó confianza en TrustedPeople.'}
        return $true
    }.GetNewClosure()
    $appx={param($ctx)
        $null=& $checkSafe $Configuration.AssertSafe $ctx
        if((Get-FileHash -LiteralPath $Configuration.WidgetPath -Algorithm SHA256).Hash -ne $Configuration.WidgetSha256){throw 'El paquete widget cambió.'}
        $sig=& $Operations.GetSignature $Configuration.WidgetPath
        if($sig.Status -ne 'Valid' -or -not $sig.SignerCertificate -or $sig.SignerCertificate.Thumbprint -ne $Configuration.Signer){throw 'La firma del widget no es válida.'}
        $deps=if($Configuration.Contains('DependencyPaths')){@($Configuration.DependencyPaths)}else{@()}
        if($deps.Count){
            if(-not $Configuration.Contains('DependencyPins') -or $Configuration.DependencyPins -isnot [System.Collections.IDictionary]){throw 'Faltan pins de dependencias.'}
            foreach($dep in $deps){
                if(-not $Configuration.DependencyPins.Contains($dep) -or (Get-FileHash -LiteralPath $dep -Algorithm SHA256).Hash -ne $Configuration.DependencyPins[$dep]){throw 'Dependencia no coincide con su pin.'}
                $dependencySignature=& $Operations.GetSignature $dep
                if($dependencySignature.Status -ne 'Valid'){throw 'Firma de dependencia no válida.'}
            }
            $deps=@(& $selectDependencies -Paths $deps -GetFrameworks $Operations.GetFrameworks)
        }
        & $Operations.AddPackage $Configuration.WidgetPath $deps
        $installed=@(& $Operations.GetPackage $Configuration.PackageName)
        if($installed.Count -ne 1 -or $installed[0].Publisher -ne $Configuration.Publisher -or $installed[0].PackageFamilyName -ne $Configuration.PackageFamilyName -or [string]$installed[0].Architecture -ne 'X64' -or [version]$installed[0].Version -ne [version]$Configuration.Version -or [string]$installed[0].Status -ne 'Ok'){throw 'No se confirmó el paquete exacto para la cuenta.'}
        return $true
    }.GetNewClosure()
    $verifyTask={param($registered,[bool]$enabled)
        if(-not $registered -or $registered.Settings.Enabled -ne $enabled -or @($registered.Actions).Count -ne 1 -or @($registered.Triggers).Count -ne 1 -or
           -not [string]::Equals([string]$registered.Actions[0].Execute,[string]$Configuration.HelperPath,[StringComparison]::OrdinalIgnoreCase) -or
           -not [string]::Equals([string]$registered.Actions[0].WorkingDirectory,(Split-Path -Parent $Configuration.HelperPath),[StringComparison]::OrdinalIgnoreCase) -or
           (& $principalSid ([string]$registered.Principal.UserId)) -ne $currentSid -or [string]$registered.Principal.LogonType -ne 'Interactive' -or [string]$registered.Principal.RunLevel -ne 'Highest' -or
           [string]$registered.Triggers[0].CimClass.CimClassName -ne 'MSFT_TaskLogonTrigger' -or (& $principalSid ([string]$registered.Triggers[0].UserId)) -ne $currentSid -or
           $registered.Settings.DisallowStartIfOnBatteries -ne $false -or $registered.Settings.StopIfGoingOnBatteries -ne $false -or [int]$registered.Settings.RestartCount -ne 3 -or
           [string]$registered.Settings.RestartInterval -ne 'PT1M' -or [string]$registered.Settings.ExecutionTimeLimit -ne 'PT0S' -or [string]$registered.Settings.MultipleInstances -ne 'IgnoreNew'){
            throw 'El estado o definición de la tarea no coinciden.'
        }
        return $true
    }.GetNewClosure()
    $task={param($ctx)
        $null=& $checkSafe $Configuration.AssertSafe $ctx
        & $Operations.RegisterTask $Configuration
        return (& $verifyTask (& $Operations.GetTask $Configuration.TaskName) $false)
    }.GetNewClosure()
    $enable={param($ctx)
        $null=& $checkSafe $Configuration.AssertSafe $ctx
        $null=& $verifyTask (& $Operations.GetTask $Configuration.TaskName) $false
        & $Operations.EnableTask $Configuration.TaskName
        return (& $verifyTask (& $Operations.GetTask $Configuration.TaskName) $true)
    }.GetNewClosure()
    return @{ApplyCertificate=$certificate;ApplyAppx=$appx;ApplyTask=$task;EnableTask=$enable}
}
Export-ModuleMember -Function New-LtWindowsEffects,Select-LtMissingDependencies
