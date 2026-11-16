# SPDX-License-Identifier: AGPL-3.0-only
$ErrorActionPreference='Stop'
Import-Module (Join-Path $PSScriptRoot '..\scripts\MaintenanceProductionMetadata.psm1') -Force
$sid='S-1-5-18';$helper=Join-Path $env:ProgramFiles 'LegionTweaks-Z1E\LegionTweaks.Helper.exe';$work=Split-Path -Parent $helper
$xml=@"
<Task xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task"><Triggers><LogonTrigger><UserId>$sid</UserId></LogonTrigger></Triggers><Principals><Principal><UserId>$sid</UserId><LogonType>InteractiveToken</LogonType><RunLevel>HighestAvailable</RunLevel></Principal></Principals><Settings><Enabled>true</Enabled></Settings><Actions><Exec><Command>$helper</Command><WorkingDirectory>$work</WorkingDirectory></Exec></Actions></Task>
"@
$signer='A'*40;$state=[pscustomobject]@{Scope=$true;Certificate=$true}
$config=@{ExpectedSid=$sid;ExpectedHelper=$helper;SourceSigner=$signer;TargetSigner=$signer;SourceVersion='0.1.49.0';AssertScope={param($x)return $state.Scope}.GetNewClosure()}
$ops=@{Package={return [pscustomobject]@{PackageFamilyName='MazTema.LegionTweaks_wsckx05r95yk0';Publisher='CN=LegionTweaks Dev';Architecture='X64';Version='0.1.49.0';Status='Ok';PackageFullName='fixture'}};
    Task={return [pscustomobject]@{Settings=[pscustomobject]@{Enabled=$true}}};TaskXml={return $xml}.GetNewClosure();Certificate={param($thumb)if($state.Certificate){return [pscustomobject]@{Thumbprint=$thumb}}}.GetNewClosure()}
$meta=Get-LtProductionMetadata -Configuration $config -Operations $ops
if(-not $meta.TaskWasEnabled -or -not $meta.TargetCertificateWasPresent -or $meta.TaskXml -cne $xml){throw 'Metadatos no coinciden.'}
$state.Certificate=$false;$meta=Get-LtProductionMetadata -Configuration $config -Operations $ops
if($meta.TargetCertificateWasPresent -or $meta.SourceCertificateWasPresent){throw 'Certificado ausente fue presentado como confiado.'}
$state.Scope=$false;$blocked=$false
try{Get-LtProductionMetadata -Configuration $config -Operations $ops|Out-Null}catch{$blocked=$true}
if(-not $blocked){throw 'Ámbito incierto aceptado.'}
Write-Output 'PASS metadatos de paquete, tarea y confianza usan consultas simuladas y bloquean ámbito incierto'
