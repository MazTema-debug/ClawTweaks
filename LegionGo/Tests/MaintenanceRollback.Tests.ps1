# SPDX-License-Identifier: AGPL-3.0-only
# Only injected operations and fixture XML; no scheduled tasks, AppX or certificate stores are touched.
$ErrorActionPreference='Stop'
Import-Module (Join-Path $PSScriptRoot '..\scripts\MaintenanceRollback.psm1') -Force
$sid='S-1-5-18'
$helper=Join-Path (Join-Path $env:ProgramFiles 'LegionTweaks-Z1E') 'LegionTweaks.Helper.exe'
$working=Split-Path -Parent $helper
$xml=@"
<Task xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task"><Triggers><LogonTrigger><UserId>$sid</UserId></LogonTrigger></Triggers><Principals><Principal><UserId>$sid</UserId><LogonType>InteractiveToken</LogonType><RunLevel>HighestAvailable</RunLevel></Principal></Principals><Settings><Enabled>true</Enabled></Settings><Actions><Exec><Command>$helper</Command><WorkingDirectory>$working</WorkingDirectory></Exec></Actions></Task>
"@
if(-not (Test-LtRollbackTaskXml -Xml $xml -ExpectedSid $sid -ExpectedHelper $helper -ExpectedEnabled $true)){throw 'XML válido rechazado.'}
foreach($bad in @($xml.Replace('<Actions>','<Actions><Exec><Command>other.exe</Command></Exec>'),$xml.Replace('<UserId>S-1-5-18</UserId>','<UserId>S-1-5-19</UserId>'),$xml.Replace('<Enabled>true</Enabled>','<Enabled>false</Enabled>'))){
    $blocked=$false
    try{Test-LtRollbackTaskXml -Xml $bad -ExpectedSid $sid -ExpectedHelper $helper -ExpectedEnabled $true|Out-Null}catch{$blocked=$true}
    if(-not $blocked){throw 'XML de tarea inesperado aceptado.'}
}
Write-Output 'PASS contrato XML rechaza acciones, identidad y estado inesperados'
$state=[pscustomobject]@{Task='';CertPresent=$true;SourceRestored=$false;Scope=$true}
$metadata=[pscustomobject]@{TaskXml=$xml;TaskWasEnabled=$true;TargetCertificateWasPresent=$true;SourceCertificateWasPresent=$true;PackageFullName='fixture'}
$config=@{Metadata=$metadata;ExpectedSid=$sid;ExpectedHelper=$helper;TaskName='LegionTweaks-Z1E';TargetSigner=('A'*40);SourceSigner=('A'*40);AssertScope={param($ctx)return $state.Scope}.GetNewClosure();ApplySourcePackage={param($ctx)$state.SourceRestored=$true;return $true}.GetNewClosure()}
$ops=@{RegisterTask={param($name,$value)$state.Task=$value}.GetNewClosure();ReadTask={param($name)return $state.Task}.GetNewClosure();TargetCertificate={param($thumb)if($state.CertPresent){return [pscustomobject]@{Thumbprint=$thumb}}}.GetNewClosure();VerifySourcePackage={param($c)return $state.SourceRestored}.GetNewClosure()}
$adapter=New-LtRollbackAdapters -Configuration $config -Operations $ops
if(-not (& $adapter.RestoreExternal @{}) -or -not $state.SourceRestored -or $state.Task -cne $xml){throw 'No se restauraron los recursos simulados.'}
Write-Output 'PASS retorno verifica AppX y tarea de fixture y conserva confianza anterior'
$metadata.TargetCertificateWasPresent=$false
$blocked=$false
try{& $adapter.VerifyExternal @{}|Out-Null}catch{$blocked=$true}
if(-not $blocked -or -not $state.CertPresent){throw 'Confianza añadida no bloqueó o fue retirada.'}
Write-Output 'PASS confianza nueva exige revisión y se conserva'
$metadata.TargetCertificateWasPresent=$true;$state.Scope=$false;$state.SourceRestored=$false
$blocked=$false
try{& $adapter.RestoreExternal @{}|Out-Null}catch{$blocked=$true}
if(-not $blocked -or $state.SourceRestored){throw 'Ámbito incierto no detuvo el retorno.'}
Write-Output 'PASS ámbito incierto detiene los efectos de retorno'
$state.Scope=$true;$state.SourceRestored=$false;$state.Task=''
$dynamic=@{GetMetadata={param($ctx)return $metadata}.GetNewClosure();ExpectedSid=$sid;ExpectedHelper=$helper;TaskName='LegionTweaks-Z1E';TargetSigner=('A'*40);SourceSigner=('A'*40);ApplySourcePackage=$config.ApplySourcePackage;AssertScope=$config.AssertScope}
$adapter2=New-LtRollbackFromBackup -Configuration $dynamic -Operations $ops
if(-not (& $adapter2.RestoreExternal @{}) -or -not (& $adapter2.VerifyExternal @{})){throw 'No se verificó retorno con metadatos dinámicos.'}
Write-Output 'PASS retorno obtiene metadatos dinámicos y verifica recursos fixture'
