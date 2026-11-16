# SPDX-License-Identifier: AGPL-3.0-only
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'

function Normalize-LtTaskXml([string]$Xml){
    $doc=[Xml.XmlDocument]::new();$doc.PreserveWhitespace=$false;$doc.XmlResolver=$null;$doc.LoadXml($Xml);return $doc
}
function Get-LtRollbackSid([string]$Value){
    try{return [Security.Principal.SecurityIdentifier]::new($Value).Value}
    catch{try{return ([Security.Principal.NTAccount]::new($Value)).Translate([Security.Principal.SecurityIdentifier]).Value}catch{throw 'Identidad de tarea no resoluble.'}}
}
function Test-LtRollbackTaskXml {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Xml,[Parameter(Mandatory)][string]$ExpectedSid,[Parameter(Mandatory)][string]$ExpectedHelper,[Parameter(Mandatory)][bool]$ExpectedEnabled)
    $doc=Normalize-LtTaskXml $Xml
    $actions=@($doc.SelectNodes('/*[local-name()="Task"]/*[local-name()="Actions"]/*'))
    $principals=@($doc.SelectNodes('/*[local-name()="Task"]/*[local-name()="Principals"]/*[local-name()="Principal"]'))
    $triggers=@($doc.SelectNodes('/*[local-name()="Task"]/*[local-name()="Triggers"]/*'))
    $enabled=@($doc.SelectNodes('/*[local-name()="Task"]/*[local-name()="Settings"]/*[local-name()="Enabled"]'))
    if($actions.Count -ne 1 -or $actions[0].LocalName -ne 'Exec' -or $principals.Count -ne 1 -or $triggers.Count -ne 1 -or $triggers[0].LocalName -ne 'LogonTrigger' -or $enabled.Count -ne 1){throw 'XML de tarea no es el esperado.'}
    $command=$actions[0].SelectSingleNode('./*[local-name()="Command"]');$args=$actions[0].SelectSingleNode('./*[local-name()="Arguments"]');$working=$actions[0].SelectSingleNode('./*[local-name()="WorkingDirectory"]')
    $user=$principals[0].SelectSingleNode('./*[local-name()="UserId"]');$logon=$principals[0].SelectSingleNode('./*[local-name()="LogonType"]');$level=$principals[0].SelectSingleNode('./*[local-name()="RunLevel"]')
    $triggerUser=$triggers[0].SelectSingleNode('./*[local-name()="UserId"]')
    if(-not $command -or -not $working -or -not $user -or -not $logon -or -not $level -or -not $triggerUser -or ($args -and $args.InnerText.Trim()) -or
       -not [string]::Equals($command.InnerText,$ExpectedHelper,[StringComparison]::OrdinalIgnoreCase) -or -not [string]::Equals($working.InnerText,(Split-Path -Parent $ExpectedHelper),[StringComparison]::OrdinalIgnoreCase) -or
       (Get-LtRollbackSid $user.InnerText) -ne $ExpectedSid -or (Get-LtRollbackSid $triggerUser.InnerText) -ne $ExpectedSid -or $logon.InnerText -ne 'InteractiveToken' -or $level.InnerText -ne 'HighestAvailable' -or
       $enabled[0].InnerText -ne $ExpectedEnabled.ToString().ToLowerInvariant()){throw 'XML de tarea difiere del contrato.'}
    return $true
}
function New-LtRollbackAdapters {
    [CmdletBinding()]
    param([Parameter(Mandatory)][System.Collections.IDictionary]$Configuration,[System.Collections.IDictionary]$Operations)
    foreach($key in @('Metadata','ExpectedSid','ExpectedHelper','TaskName','TargetSigner','SourceSigner','ApplySourcePackage','AssertScope')){if(-not $Configuration.Contains($key) -or $null -eq $Configuration[$key]){throw "Falta configuración de retorno $key."}}
    if($Configuration.TaskName -ne 'LegionTweaks-Z1E' -or $Configuration.TargetSigner -notmatch '^[0-9A-Fa-f]{40}$' -or $Configuration.SourceSigner -notmatch '^[0-9A-Fa-f]{40}$' -or $Configuration.ApplySourcePackage -isnot [scriptblock] -or $Configuration.AssertScope -isnot [scriptblock]){throw 'Configuración de retorno inválida.'}
    $meta=$Configuration.Metadata
    if(-not $meta.TaskXml -or $meta.TaskWasEnabled -isnot [bool] -or $meta.TargetCertificateWasPresent -isnot [bool] -or $meta.SourceCertificateWasPresent -isnot [bool]){throw 'Respaldo de tarea o confianza incompleto.'}
    $taskCheck=Get-Command Test-LtRollbackTaskXml -CommandType Function -ErrorAction Stop
    $null=& $taskCheck -Xml $meta.TaskXml -ExpectedSid $Configuration.ExpectedSid -ExpectedHelper $Configuration.ExpectedHelper -ExpectedEnabled $meta.TaskWasEnabled
    if(-not $Operations){$Operations=@{
        RegisterTask={param($name,$xml) Register-ScheduledTask -TaskName $name -Xml $xml -Force -ErrorAction Stop|Out-Null}
        ReadTask={param($name) Export-ScheduledTask -TaskName $name -ErrorAction Stop}
        TargetCertificate={param($thumb) @(Get-ChildItem -LiteralPath 'Cert:\LocalMachine\TrustedPeople' -ErrorAction Stop | Where-Object {$_.Thumbprint -eq $thumb})}
        VerifySourcePackage={param($c)
            $p=@(Get-AppxPackage -Name 'MazTema.LegionTweaks' -ErrorAction Stop)
            return ($p.Count -eq 1 -and [string]$p[0].PackageFullName -eq [string]$c.Metadata.PackageFullName -and [string]$p[0].Status -eq 'Ok')
        }
    }}
    foreach($key in @('RegisterTask','ReadTask','TargetCertificate','VerifySourcePackage')){if(-not $Operations.Contains($key) -or $Operations[$key] -isnot [scriptblock]){throw "Falta operación de retorno $key."}}
    $checkScope={param($ctx)$r=@(& $Configuration.AssertScope $ctx);if($r.Count -ne 1 -or $r[0] -isnot [bool] -or -not $r[0]){throw 'Alcance de retorno incierto.'};return $true}.GetNewClosure()
    $normalize=${function:Normalize-LtTaskXml}
    $verify={param($ctx)
        $null=& $checkScope $ctx
        $source=@(& $Operations.VerifySourcePackage $Configuration)
        if($source.Count -ne 1 -or $source[0] -isnot [bool] -or -not $source[0]){throw 'Paquete de origen no confirmado.'}
        $actual=@(& $Operations.ReadTask $Configuration.TaskName)
        if($actual.Count -ne 1 -or (& $normalize ([string]$actual[0])).OuterXml -cne (& $normalize ([string]$meta.TaskXml)).OuterXml){throw 'Tarea restaurada no coincide con respaldo.'}
        $trust=@(& $Operations.TargetCertificate $Configuration.TargetSigner)
        if(-not $meta.TargetCertificateWasPresent -and $trust.Count){throw 'La confianza añadida requiere revisión; se conserva.'}
        if($meta.TargetCertificateWasPresent -and ($trust.Count -ne 1 -or $trust[0].Thumbprint -ne $Configuration.TargetSigner)){throw 'No se conservó la confianza previa.'}
        $sourceTrust=@(& $Operations.TargetCertificate $Configuration.SourceSigner)
        if($meta.SourceCertificateWasPresent -and ($sourceTrust.Count -ne 1 -or $sourceTrust[0].Thumbprint -ne $Configuration.SourceSigner)){throw 'No se conservó la confianza del origen.'}
        if(-not $meta.SourceCertificateWasPresent -and $sourceTrust.Count){throw 'La confianza del origen cambió; se conserva para revisión.'}
        return $true
    }.GetNewClosure()
    $restore={param($ctx)
        $null=& $checkScope $ctx
        $installed=@(& $Configuration.ApplySourcePackage $ctx)
        if($installed.Count -ne 1 -or $installed[0] -isnot [bool] -or -not $installed[0]){throw 'No se restauró AppX de origen.'}
        $null=& $checkScope $ctx
        & $Operations.RegisterTask $Configuration.TaskName ([string]$meta.TaskXml)
        return (& $verify $ctx)
    }.GetNewClosure()
    return @{RestoreExternal=$restore;VerifyExternal=$verify}
}
function New-LtRollbackFromBackup {
    [CmdletBinding()]
    param([Parameter(Mandatory)][System.Collections.IDictionary]$Configuration,[System.Collections.IDictionary]$Operations)
    foreach($key in @('GetMetadata','ExpectedSid','ExpectedHelper','TaskName','TargetSigner','SourceSigner','ApplySourcePackage','AssertScope')){
        if(-not $Configuration.Contains($key) -or $null -eq $Configuration[$key]){throw "Falta configuración dinámica de retorno $key."}
    }
    if($Configuration.GetMetadata -isnot [scriptblock]){throw 'Falta lectura de respaldo verificado.'}
    $factory=${function:New-LtRollbackAdapters}
    $make={param($ctx)
        $metadata=@(& $Configuration.GetMetadata $ctx)
        if($metadata.Count -ne 1 -or -not $metadata[0]){throw 'Metadatos verificados de retorno inciertos.'}
        $config=@{Metadata=$metadata[0];ExpectedSid=$Configuration.ExpectedSid;ExpectedHelper=$Configuration.ExpectedHelper;TaskName=$Configuration.TaskName;
            TargetSigner=$Configuration.TargetSigner;SourceSigner=$Configuration.SourceSigner;ApplySourcePackage=$Configuration.ApplySourcePackage;AssertScope=$Configuration.AssertScope}
        if($Operations){return (& $factory -Configuration $config -Operations $Operations)}
        return (& $factory -Configuration $config)
    }.GetNewClosure()
    $restore={param($ctx)$adapter=& $make $ctx;return (& $adapter.RestoreExternal $ctx)}.GetNewClosure()
    $verify={param($ctx)$adapter=& $make $ctx;return (& $adapter.VerifyExternal $ctx)}.GetNewClosure()
    return @{RestoreExternal=$restore;VerifyExternal=$verify}
}
Export-ModuleMember -Function Test-LtRollbackTaskXml,New-LtRollbackAdapters,New-LtRollbackFromBackup
