# SPDX-License-Identifier: AGPL-3.0-only
# Actual coordinator, runtime, tree and effects adapters; external operations are doubles.
$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'MaintenanceRuntime.Tests.ps1')
$runtimeConfig=$config;$runtimeOps=$ops;$runtimeState=$state;$runtimeAssembly=$mando
. (Join-Path $PSScriptRoot 'MaintenanceWindowsEffects.Tests.ps1')
$windowsConfig=$config;$windowsOps=$ops
. (Join-Path $PSScriptRoot 'MaintenanceTreePublication.Tests.ps1')
Import-Module (Join-Path $PSScriptRoot '..\scripts\MaintenanceCoordinator.psm1') -Force -DisableNameChecking
Import-Module (Join-Path $PSScriptRoot '..\scripts\MaintenanceCoordinatorLease.psm1') -DisableNameChecking
$originalProgramFiles=$env:ProgramFiles;$guardStream=$null
try{
    $rig=New-PublicationRig 'none';$env:ProgramFiles=$rig.State.Root
    $fixtureLive=Join-Path $rig.State.Root 'LegionTweaks-Z1E'
    Move-Item -LiteralPath $rig.State.Live -Destination $fixtureLive
    $rig.State.Live=$fixtureLive
    $rig=Set-RealTreeAdapter $rig 'none'
    $lease=Open-LtCoordinatorLease -Path (Join-Path $rig.State.Root 'coordinator-v1.lock') -AssertProtection {param($p)return $true}
    $current=[Security.Principal.WindowsIdentity]::GetCurrent()
    try{$currentSid=$current.User.Value}finally{$current.Dispose()}
    $runtimeConfig.CurrentSid=$currentSid;$runtimeState.CoverageSid=$currentSid;$runtimeState.Items=@();$runtimeState.CountOverride=1;$runtimeState.Assembly=$runtimeAssembly;$runtimeState.SourceHash=$runtimeConfig.Source.HelperSha256
    $windowsConfig.HelperPath=Join-Path $fixtureLive 'LegionTweaks.Helper.exe';$script:helper=$windowsConfig.HelperPath
    $packageRoot=Join-Path $rig.State.Root 'target-package';$null=New-Item -ItemType Directory -Path (Join-Path $packageRoot 'Widget') -Force
    $widget=Join-Path $packageRoot 'Widget\fixture.msix';Copy-Item -LiteralPath $windowsConfig.WidgetPath -Destination $widget
    $windowsConfig.WidgetPath=$widget;$windowsConfig.WidgetSha256=(Get-FileHash -LiteralPath $widget -Algorithm SHA256).Hash
    $runtimeConfig.Target.Extracted=$packageRoot;$runtimeConfig.Target.HelperPath=Join-Path $packageRoot 'Helper\LegionTweaks.Helper.exe';$runtimeConfig.Target.QamPath=Join-Path $packageRoot 'Helper\Qam\LegionTweaks.QamProbe.exe';$runtimeConfig.Target.WidgetPath=$widget
    $flow=@{};foreach($name in @('BackupAndVerify','Checkpoint','Commit','ConfirmCompensationSafety','PreserveBlocked')){$flow[$name]=$rig.Adapter[$name]}
    $startup=@{ExpectedSid=$currentSid;AssertProtection={param($p)return $true}}
    $startupOperation={param($sid,$check)return [pscustomobject]@{AlreadyPresent=$false;Version=1;Skip=$true;AuthorizesMaintenance=$false}}
    $configuration=@{Direction='Return49';Runtime=$runtimeConfig;WindowsEffects=$windowsConfig;Startup=$startup;Tree=$rig.TreeConfig;Flow=$flow;Lease=$lease}
    $operations=@{Runtime=$runtimeOps;WindowsEffects=$windowsOps;Startup=$startupOperation;ExpectedLeasePath=$lease.Path}
    $coordinator=New-LtMaintenanceCoordinator -Configuration $configuration -Operations $operations
    $result=Invoke-LtMaintenanceCoordinator -Coordinator $coordinator -Request @{}
    if($result.Stage -ne 'complete' -or -not $result.Committed -or [IO.File]::ReadAllText((Join-Path $rig.State.Live 'version.txt')) -ne 'new'){throw ('Coordinador integrado falló: '+$result.Stage+' / '+$result.Error+' / '+$result.CleanupError)}
    Write-Output 'PASS composición real ejecuta retorno fixture con efectos Windows simulados'
    $configuration.Startup.ExpectedSid='S-1-5-18';$blocked=$false
    try{New-LtMaintenanceCoordinator -Configuration $configuration -Operations $operations|Out-Null}catch{$blocked=$true}
    if(-not $blocked){throw 'Coordinador aceptó identidades inconsistentes.'}
    Write-Output 'PASS composición bloquea identidades inconsistentes'
    $null=Close-LtCoordinatorLease -Lease $lease;$lease=$null
    $rig2=New-PublicationRig 'none';$env:ProgramFiles=$rig2.State.Root
    $live2=Join-Path $rig2.State.Root 'LegionTweaks-Z1E';Move-Item -LiteralPath $rig2.State.Live -Destination $live2;$rig2.State.Live=$live2
    $rig2=Set-RealTreeAdapter $rig2 'none'
    $lease=Open-LtCoordinatorLease -Path (Join-Path $rig2.State.Root 'coordinator-v1.lock') -AssertProtection {param($p)return $true}
    $operations.ExpectedLeasePath=$lease.Path
    $windowsConfig.HelperPath=Join-Path $live2 'LegionTweaks.Helper.exe';$script:helper=$windowsConfig.HelperPath
    $startup.ExpectedSid=$currentSid
    $metadata=@{TaskXml='<fixture/>';TaskWasEnabled=$true;PackageFullName='fixture';TargetCertificateWasPresent=$true;SourceCertificateWasPresent=$true}
    $data2=Join-Path $rig2.State.Root 'user-data';$null=New-Item -ItemType Directory -Path $data2
    $groups=@([pscustomobject]@{Name='helper';SourceRoot=$live2},[pscustomobject]@{Name='user-data';SourceRoot=$data2})
    $flowConfig=@{Direction='Return49';Id=[Guid]::NewGuid().ToString('N');BackupDestination=(Join-Path $rig2.State.Root 'backupset');RestoreTree=$rig2.State.Backup;Groups=$groups;
        GetMetadata={param($ctx)return $metadata}.GetNewClosure();JournalPath=(Join-Path $rig2.State.Root 'real-flow-journal.json');OldSha256=$runtimeConfig.Source.HelperSha256;NewSha256=$runtimeConfig.Target.HelperSha256;CoordinatorSha256=$runtimeConfig.Coordinator.HelperSha256;
        AssertProtection={param($a,$b,$c)return $true};AssertGroups={param($g)return $true};AssertPrerequisites={param($ctx)return $true};ReadExternalFingerprint={param($ctx)return ('A'*64)};
        ConfirmPublished={param($ctx)return $true};ConfirmCompensationSafety={param($ctx)return $true};PreserveBlocked={param($ctx)return $true}}
    $configuration=@{Direction='Return49';Runtime=$runtimeConfig;WindowsEffects=$windowsConfig;Startup=$startup;Tree=$rig2.TreeConfig;FlowConfiguration=$flowConfig;Lease=$lease}
    $coordinator=New-LtMaintenanceCoordinator -Configuration $configuration -Operations $operations
    $result=Invoke-LtMaintenanceCoordinator -Coordinator $coordinator -Request @{}
    if($result.Stage -ne 'complete' -or -not $result.Committed){throw ('Composición con flujo real falló: '+$result.Stage+' / '+$result.Error+' / '+$result.CleanupError)}
    if(-not [IO.File]::Exists((Join-Path $rig2.State.Root 'backupset\manifest.json'))){throw 'Falta manifiesto real de respaldo.'}
    Write-Output 'PASS coordinador integra flujo real de respaldo, diario y publicación en fixture'
    $flowConfig.CoordinatorSha256='0'*64;$blocked=$false
    try{New-LtMaintenanceCoordinator -Configuration $configuration -Operations $operations|Out-Null}catch{$blocked=$true}
    if(-not $blocked){throw 'Pins de flujo inconsistentes aceptados.'}
    Write-Output 'PASS composición bloquea pins de flujo inconsistentes'
    $null=Close-LtCoordinatorLease -Lease $lease;$lease=$null
    $rig3=New-PublicationRig 'none';$env:ProgramFiles=$rig3.State.Root
    $live3=Join-Path $rig3.State.Root 'LegionTweaks-Z1E';Move-Item -LiteralPath $rig3.State.Live -Destination $live3;$rig3.State.Live=$live3
    $rig3=Set-RealTreeAdapter $rig3 'none'
    $lease=Open-LtCoordinatorLease -Path (Join-Path $rig3.State.Root 'coordinator-v1.lock') -AssertProtection {param($p)return $true};$operations.ExpectedLeasePath=$lease.Path
    $windowsConfig.HelperPath=Join-Path $live3 'LegionTweaks.Helper.exe';$script:helper=$windowsConfig.HelperPath
    $work=Split-Path -Parent $windowsConfig.HelperPath
    $savedXml=@"
<Task xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task"><Triggers><LogonTrigger><UserId>$currentSid</UserId></LogonTrigger></Triggers><Principals><Principal><UserId>$currentSid</UserId><LogonType>InteractiveToken</LogonType><RunLevel>HighestAvailable</RunLevel></Principal></Principals><Settings><Enabled>true</Enabled></Settings><Actions><Exec><Command>$($windowsConfig.HelperPath)</Command><WorkingDirectory>$work</WorkingDirectory></Exec></Actions></Task>
"@
    $rollbackState=[pscustomobject]@{Task='';SourceRestored=$false}
    $metadata3=@{TaskXml=$savedXml;TaskWasEnabled=$true;PackageFullName='fixture';TargetCertificateWasPresent=$true;SourceCertificateWasPresent=$true}
    $dataRoot=Join-Path $rig3.State.Root 'user-data';$null=New-Item -ItemType Directory -Path $dataRoot
    $diary=Join-Path $dataRoot 'fps-y3-trial.json';$original=Join-Path $dataRoot 'original.fixture';$guard=Join-Path $dataRoot 'fps-y3-admission-v1.guard'
    [IO.File]::WriteAllText($diary,'Restored fixture');[IO.File]::WriteAllText($original,'original fixture')
    $diaryHash=(Get-FileHash -LiteralPath $diary -Algorithm SHA256).Hash;$originalHash=(Get-FileHash -LiteralPath $original -Algorithm SHA256).Hash
    $journalStream=[IO.FileStream]::new($diary,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read)
    try{$fileId=[LegionTweaks.MaintenanceNative.FileIdentity]::new();if(-not [LegionTweaks.MaintenanceNative.BackupApi]::GetFileInformationByHandle($journalStream.SafeFileHandle,[ref]$fileId)){throw 'Identidad fixture no disponible.'}}finally{$journalStream.Dispose()}
    $idText=('{0:X8}{1:X8}{2:X8}' -f $fileId.Volume,$fileId.IndexHigh,$fileId.IndexLow);$sha=[Security.Cryptography.SHA256]::Create()
    try{$diaryId=([BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::ASCII.GetBytes($idText)))).Replace('-','')}finally{$sha.Dispose()}
    $runtimeOps.OpenSession={param($c,$h)return [pscustomobject]@{AuthorizesMaintenance=$false;Classification='restored_with_original';ContentSha256=$diaryHash;FileIdentitySha256=$diaryId;ProtectionSha256=('D'*64)}}.GetNewClosure()
    $diaryAcl=(Get-Acl -LiteralPath $diary).Sddl;$originalAcl=(Get-Acl -LiteralPath $original).Sddl
    $guardStream=[IO.FileStream]::new($guard,[IO.FileMode]::CreateNew,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None)
    $flow3=@{Direction='Return49';Id=[Guid]::NewGuid().ToString('N');BackupDestination=(Join-Path $rig3.State.Root 'backupset');RestoreTree=$rig3.State.Backup;
        Groups=@([pscustomobject]@{Name='helper';SourceRoot=$live3},[pscustomobject]@{Name='user-data';SourceRoot=$dataRoot;ExcludedFiles=@('fps-y3-admission-v1.guard')});GetMetadata={param($ctx)return $metadata3}.GetNewClosure();JournalPath=(Join-Path $rig3.State.Root 'flow-journal.json');
        OldSha256=$runtimeConfig.Source.HelperSha256;NewSha256=$runtimeConfig.Target.HelperSha256;CoordinatorSha256=$runtimeConfig.Coordinator.HelperSha256;
        AssertProtection={param($a,$b,$c)return $true};AssertGroups={param($g)return $true};AssertPrerequisites={param($ctx)return $true};ReadExternalFingerprint={param($ctx)return ('A'*64)};
        ConfirmPublished={param($ctx)return $true};ConfirmCompensationSafety={param($ctx)return $true};PreserveBlocked={param($ctx)return $true}}
    $rollbackConfig=@{ExpectedSid=$currentSid;ExpectedHelper=$windowsConfig.HelperPath;TaskName='LegionTweaks-Z1E';TargetSigner=$cert.Thumbprint;SourceSigner=$cert.Thumbprint;
        ApplySourcePackage={param($ctx)$rollbackState.SourceRestored=$true;return $true}.GetNewClosure();AssertScope={param($ctx)return $true}}
    $rollbackOps=@{RegisterTask={param($name,$value)$rollbackState.Task=$value}.GetNewClosure();ReadTask={param($name)return $rollbackState.Task}.GetNewClosure();
        TargetCertificate={param($thumb)return $cert}.GetNewClosure();VerifySourcePackage={param($c)return $rollbackState.SourceRestored}.GetNewClosure()}
    $oldAdd=$windowsOps.AddPackage;$windowsOps.AddPackage={param($file,$dependencies)throw 'AppX fixture failure'};$operations.Rollback=$rollbackOps
    $configuration=@{Direction='Return49';Runtime=$runtimeConfig;WindowsEffects=$windowsConfig;Startup=$startup;Tree=$rig3.TreeConfig;FlowConfiguration=$flow3;RollbackConfiguration=$rollbackConfig;Lease=$lease}
    $coordinator=New-LtMaintenanceCoordinator -Configuration $configuration -Operations $operations
    $result=Invoke-LtMaintenanceCoordinator -Coordinator $coordinator -Request @{}
    if($result.Stage -ne 'aborted' -or -not $result.CompensationVerified -or -not $rollbackState.SourceRestored -or $rollbackState.Task -cne $savedXml -or [IO.File]::ReadAllText((Join-Path $live3 'version.txt')) -cne 'old'){throw ('Compensación integrada falló: '+$result.Stage+' / '+$result.Error+' / '+$result.CleanupError)}
    if((Get-FileHash -LiteralPath $diary -Algorithm SHA256).Hash -ne $diaryHash -or (Get-FileHash -LiteralPath $original -Algorithm SHA256).Hash -ne $originalHash -or (Get-Acl -LiteralPath $diary).Sddl -ne $diaryAcl -or (Get-Acl -LiteralPath $original).Sddl -ne $originalAcl -or -not (Test-Path -LiteralPath $guard)){throw 'La compensación alteró Y3 fixture.'}
    $windowsOps.AddPackage=$oldAdd
    Write-Output 'PASS AppX simulado fallido restaura árbol y recursos externos con respaldo real'
}finally{if($guardStream){$guardStream.Dispose()};if($lease){$null=Close-LtCoordinatorLease -Lease $lease};$env:ProgramFiles=$originalProgramFiles}
