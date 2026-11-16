# SPDX-License-Identifier: AGPL-3.0-only
# All Windows operations are doubles. The bundled .49 certificate contains only
# the public certificate; no trust store or private key is used.
$ErrorActionPreference='Stop'
Import-Module (Join-Path $PSScriptRoot '..\scripts\MaintenanceWindowsEffects.psm1') -Force
$certificatePath=Join-Path $PSScriptRoot 'Fixtures\LegionTweaks49-public.cer'
$cert=[Security.Cryptography.X509Certificates.X509Certificate2]::new($certificatePath)
if($cert.HasPrivateKey -or $cert.Thumbprint -ne '4AB5A8F9579DFDA51130307D543BEEF06EBF4DA0'){throw 'Certificado público de fixture inesperado.'}
$dir=Join-Path ([IO.Path]::GetTempPath()) ('LegionTweaks-effects-fixture-'+[Guid]::NewGuid().ToString('N'))
$null=New-Item -ItemType Directory -Path $dir
$widgetPath=Join-Path $dir 'fixture.msix';[IO.File]::WriteAllText($widgetPath,'Paquete sintético: operaciones y firma simuladas.')
$safe=$true;$calls=[Collections.Generic.List[string]]::new();$taskState=[pscustomobject]@{Enabled=$false}
$helper=Join-Path $env:ProgramFiles 'LegionTweaks-Z1E\LegionTweaks.Helper.exe'
$userSid=[Security.Principal.WindowsIdentity]::GetCurrent().User.Value
$config=@{CertificatePath=$certificatePath;Signer=$cert.Thumbprint;WidgetPath=$widgetPath;
    WidgetSha256=(Get-FileHash -LiteralPath $widgetPath -Algorithm SHA256).Hash;PackageName='MazTema.LegionTweaks';PackageFamilyName='MazTema.LegionTweaks_wsckx05r95yk0';
    Publisher='CN=LegionTweaks Dev';Version='0.1.49.0';TaskName='LegionTweaks-Z1E';User=[Security.Principal.WindowsIdentity]::GetCurrent().Name;
    HelperPath=$helper;AssertSafe={param($ctx) return $script:safe}}
$ops=@{
    GetCertificate={param($thumb)$script:calls.Add('GetCertificate');return $script:cert}
    ImportCertificate={param($file)throw 'No debe importar el certificado de fixture.'}
    GetSignature={param($file)return [pscustomobject]@{Status='Valid';SignerCertificate=$script:cert}}
    GetFrameworks={param($name)return @()}
    AddPackage={param($file,$deps)$script:calls.Add('AddPackage')}
    GetPackage={param($name)return [pscustomobject]@{Publisher='CN=LegionTweaks Dev';PackageFamilyName='MazTema.LegionTweaks_wsckx05r95yk0';Architecture='X64';Version='0.1.49.0';Status='Ok'}}
    RegisterTask={param($c)$script:calls.Add('RegisterTask');$script:taskState.Enabled=$false}
    EnableTask={param($name)$script:calls.Add('EnableTask');$script:taskState.Enabled=$true}
    GetTask={param($name)return [pscustomobject]@{
        Settings=[pscustomobject]@{Enabled=$script:taskState.Enabled;DisallowStartIfOnBatteries=$false;StopIfGoingOnBatteries=$false;RestartCount=3;RestartInterval='PT1M';ExecutionTimeLimit='PT0S';MultipleInstances='IgnoreNew'};
        Actions=@([pscustomobject]@{Execute=$script:helper;WorkingDirectory=(Split-Path -Parent $script:helper)});
        Principal=[pscustomobject]@{UserId=$script:userSid;LogonType='Interactive';RunLevel='Highest'};
        Triggers=@([pscustomobject]@{CimClass=[pscustomobject]@{CimClassName='MSFT_TaskLogonTrigger'};UserId=$script:userSid})}}
}
function Assert([bool]$condition,[string]$message){if(-not $condition){throw $message}}
function Reject([scriptblock]$action){$blocked=$false;try{& $action|Out-Null}catch{$blocked=$true};Assert $blocked 'Efecto inválido aceptado.'}
$adapter=New-LtWindowsEffects -Configuration $config -Operations $ops
$ctx=[ordered]@{}
Assert (& $adapter.ApplyCertificate $ctx) 'Certificado falló.'
Assert ($ctx.CertificateWasPresent -and $calls -notcontains 'ImportCertificate') 'No preservó confianza existente.'
Assert (& $adapter.ApplyAppx $ctx) 'Appx simulado falló.'
Assert (& $adapter.ApplyTask $ctx) 'Tarea simulada falló.'
Assert (& $adapter.EnableTask $ctx) 'Habilitación de tarea simulada falló.'
Write-Output 'PASS efectos Appx, certificado y tarea con dobles'
$script:safe=$false;$before=$calls.Count
Reject {& $adapter.ApplyAppx $ctx}
Assert ($calls.Count -eq $before) 'Hubo efecto pese a una barrera inválida.'
Write-Output 'PASS barrera inválida impide efectos'
$config.AssertSafe={param($ctx) return $true,$false};$before=$calls.Count
Reject {& $adapter.ApplyAppx $ctx}
Assert ($calls.Count -eq $before) 'Una barrera ambigua permitió un efecto.'
Write-Output 'PASS barrera ambigua impide efectos'
$config.AssertSafe={param($ctx) return $script:safe}
$script:safe=$true;$config.WidgetSha256='0'*64;$before=$calls.Count
Reject {& $adapter.ApplyAppx $ctx}
Assert ($calls.Count -eq $before) 'Se invocó Appx con hash incorrecto.'
Write-Output 'PASS paquete cambiado bloquea Appx'
$config.WidgetSha256=(Get-FileHash -LiteralPath $widgetPath -Algorithm SHA256).Hash
Import-Module (Join-Path $PSScriptRoot '..\scripts\MaintenanceTransaction.psm1') -Force
$names=@('ValidateRequest','StageAndVerify','InventoryAndFence','AwaitQuiescence','BeginSessions','RevalidateAll',
    'BackupAndVerify','Checkpoint','ArmBarrier','VerifyBarrier','ApplyCertificate','ApplyAppx','ApplyTask','ApplyStartupSuppression','EnableTask','PreparePublication','PublishTree',
    'VerifyPublished','Commit','ReconcileCommit','ConfirmCompensationSafety','Compensate','VerifyCompensated','ReleaseSessions','PreserveSessions','ReleaseFence','PreserveBlocked')
$transaction=@{};foreach($name in $names){$transaction[$name]={param($ctx) return $true}}
$transaction.ReconcileCommit={param($ctx) return 'not-committed'}
$effects=New-LtWindowsEffects -Configuration $config -Operations $ops
foreach($name in @('ApplyCertificate','ApplyAppx','ApplyTask','EnableTask')){$transaction[$name]=$effects[$name]}
$result=Invoke-LtMaintenanceTransaction -Direction Return49 -Adapter $transaction -Request @{}
Assert ($result.Stage -eq 'complete' -and $result.Committed) 'El motor no conectó los efectos Windows simulados.'
Write-Output 'PASS motor ejecuta adaptadores reales con operaciones Windows simuladas'
