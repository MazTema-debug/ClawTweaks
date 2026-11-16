# SPDX-License-Identifier: AGPL-3.0-only
# Terminates only child PowerShell processes that operate on isolated fixtures.
param([ValidateSet('Parent','Child')][string]$Mode='Parent',[string]$ControlRoot,[string]$Boundary,[string]$Direction)
$ErrorActionPreference='Stop'
if($Mode -eq 'Child'){
    $full=[IO.Path]::GetFullPath($ControlRoot)
    $prefix=[IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\')+'\LegionTweaks-coordinator-crash-'
    if(-not $full.StartsWith($prefix,[StringComparison]::OrdinalIgnoreCase) -or -not [IO.File]::Exists((Join-Path $full 'fixture-only.marker'))){throw 'Control de fixture inválido.'}
    . (Join-Path $PSScriptRoot 'MaintenanceCoordinator.Tests.ps1')
    $runtimeOps.OpenSession={param($c,$h)return [pscustomobject]@{AuthorizesMaintenance=$false;Classification='absent';ContentSha256='';FileIdentitySha256='';ProtectionSha256=('D'*64)}}
    $rig=New-PublicationRig 'none';$env:ProgramFiles=$rig.State.Root
    $live=Join-Path $rig.State.Root 'LegionTweaks-Z1E';Move-Item -LiteralPath $rig.State.Live -Destination $live;$rig.State.Live=$live
    $rig=Set-RealTreeAdapter $rig 'none'
    $lease=Open-LtCoordinatorLease -Path (Join-Path $rig.State.Root 'coordinator-v1.lock') -AssertProtection {param($p)return $true}
    $identity=[Security.Principal.WindowsIdentity]::GetCurrent();try{$sid=$identity.User.Value}finally{$identity.Dispose()}
    $runtimeConfig.Direction=$Direction;$runtimeConfig.CurrentSid=$sid;$runtimeState.CoverageSid=$sid;$runtimeState.Items=@();$runtimeState.CountOverride=1;$runtimeState.Assembly=$runtimeAssembly;$runtimeState.SourceHash=$runtimeConfig.Source.HelperSha256
    $windowsConfig.HelperPath=Join-Path $live 'LegionTweaks.Helper.exe';$script:helper=$windowsConfig.HelperPath
    $packageRoot=Join-Path $rig.State.Root 'target-package';$null=New-Item -ItemType Directory -Path (Join-Path $packageRoot 'Widget') -Force
    $widget=Join-Path $packageRoot 'Widget\fixture.msix';Copy-Item -LiteralPath $windowsConfig.WidgetPath -Destination $widget
    $windowsConfig.WidgetPath=$widget;$windowsConfig.WidgetSha256=(Get-FileHash -LiteralPath $widget -Algorithm SHA256).Hash
    $runtimeConfig.Target.Extracted=$packageRoot;$runtimeConfig.Target.HelperPath=Join-Path $packageRoot 'Helper\LegionTweaks.Helper.exe';$runtimeConfig.Target.QamPath=Join-Path $packageRoot 'Helper\Qam\LegionTweaks.QamProbe.exe';$runtimeConfig.Target.WidgetPath=$widget
    $metadata=@{TaskXml='<fixture/>';TaskWasEnabled=$true;PackageFullName='fixture';TargetCertificateWasPresent=$true;SourceCertificateWasPresent=$true}
    $data=Join-Path $rig.State.Root 'user-data';$null=New-Item -ItemType Directory -Path $data
    $groups=@([pscustomobject]@{Name='helper';SourceRoot=$live},[pscustomobject]@{Name='user-data';SourceRoot=$data})
    $flowConfig=@{Direction=$Direction;Id=[Guid]::NewGuid().ToString('N');BackupDestination=(Join-Path $rig.State.Root 'backupset');Groups=$groups;
        GetMetadata={param($ctx)return $metadata}.GetNewClosure();JournalPath=(Join-Path $rig.State.Root 'flow-journal.json');OldSha256=$runtimeConfig.Source.HelperSha256;NewSha256=$runtimeConfig.Target.HelperSha256;CoordinatorSha256=$runtimeConfig.Coordinator.HelperSha256;
        AssertProtection={param($a,$b,$c)return $true};AssertGroups={param($g)return $true};AssertPrerequisites={param($ctx)return $true};ReadExternalFingerprint={param($ctx)return ('A'*64)};
        ConfirmPublished={param($ctx)return $true};ConfirmCompensationSafety={param($ctx)return $true};PreserveBlocked={param($ctx)return $true}}
    $start=@{ExpectedSid=$sid;AssertProtection={param($p)return $true}}
    $startup={param($sid,$check)return [pscustomobject]@{AlreadyPresent=$false;Version=1;Skip=$true;AuthorizesMaintenance=$false}}
    $cfg=@{Direction=$Direction;Runtime=$runtimeConfig;WindowsEffects=$windowsConfig;Startup=$start;Tree=$rig.TreeConfig;FlowConfiguration=$flowConfig;Lease=$lease}
    $op=@{Runtime=$runtimeOps;WindowsEffects=$windowsOps;Startup=$startup;ExpectedLeasePath=$lease.Path}
    $coordinator=New-LtMaintenanceCoordinator -Configuration $cfg -Operations $op
    [IO.File]::WriteAllText((Join-Path $ControlRoot 'fixture-root.txt'),$rig.State.Root)
    $pause={param($where)if($where -eq $Boundary){[IO.File]::WriteAllText((Join-Path $ControlRoot 'child-ready'),$where);while($true){Start-Sleep -Milliseconds 100}}}.GetNewClosure()
    $realPublish=$coordinator.Adapters.PublishTree
    $coordinator.Adapters.PublishTree={param($ctx)& $pause 'before-rename';$result=& $realPublish $ctx;& $pause 'after-rename';return $result}.GetNewClosure()
    $realCheckpoint=$coordinator.Adapters.Checkpoint
    $coordinator.Adapters.Checkpoint={param($ctx)if($ctx.Stage -eq 'committed'){& $pause 'before-annotation'};$result=& $realCheckpoint $ctx;if($ctx.Stage -eq 'committed'){& $pause 'after-annotation'};return $result}.GetNewClosure()
    Invoke-LtMaintenanceCoordinator -Coordinator $coordinator -Request @{}|Out-Null
    throw 'No se alcanzó la frontera solicitada.'
}
Import-Module (Join-Path $PSScriptRoot '..\scripts\MaintenancePublicationDecision.psm1') -Force
Import-Module (Join-Path $PSScriptRoot '..\scripts\MaintenanceReconcile.psm1') -Force
Import-Module (Join-Path $PSScriptRoot '..\scripts\MaintenanceBackupSet.psm1') -Force
Import-Module (Join-Path $PSScriptRoot '..\scripts\MaintenanceCoordinatorLease.psm1') -DisableNameChecking
foreach($directionCase in @('Update','Return49')){
    foreach($boundaryCase in @('before-rename','after-rename','before-annotation','after-annotation')){
        $control=Join-Path ([IO.Path]::GetTempPath()) ('LegionTweaks-coordinator-crash-'+[Guid]::NewGuid().ToString('N'))
        $null=New-Item -ItemType Directory -Path $control;[IO.File]::WriteAllText((Join-Path $control 'fixture-only.marker'),'synthetic')
        $info=[Diagnostics.ProcessStartInfo]::new();$info.FileName=Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe'
        $info.Arguments='-NoProfile -NonInteractive -ExecutionPolicy Bypass -File "'+$PSCommandPath+'" -Mode Child -ControlRoot "'+$control+'" -Boundary '+$boundaryCase+' -Direction '+$directionCase
        $info.UseShellExecute=$false;$info.CreateNoWindow=$true;$info.RedirectStandardError=$true;$info.RedirectStandardOutput=$true
        $child=[Diagnostics.Process]::Start($info)
        try{
            $until=[DateTime]::UtcNow.AddSeconds(30)
            while(-not [IO.File]::Exists((Join-Path $control 'child-ready')) -and -not $child.HasExited -and [DateTime]::UtcNow -lt $until){Start-Sleep -Milliseconds 40}
            if(-not [IO.File]::Exists((Join-Path $control 'child-ready'))){throw ('Coordinador fixture no alcanzó '+$boundaryCase+': '+$child.StandardError.ReadToEnd())}
            $child.Kill();if(-not $child.WaitForExit(10000)){throw 'Coordinador fixture no terminó.'}
        }finally{if(-not $child.HasExited){$child.Kill();$child.WaitForExit()};$child.Dispose()}
        $root=[IO.File]::ReadAllText((Join-Path $control 'fixture-root.txt'))
        $protect={param($p)return $true};$external={param($r)return ('A'*64)}
        $recover={param($r)
            foreach($relative in @('helper.exe','Qam\qam.exe')){if([IO.File]::ReadAllBytes((Join-Path $r.Previous $relative))[0] -ne 0){return $false}}
            $null=Test-LtBackupSet -Destination (Join-Path $root 'backupset') -ExpectedManifestSha256 $r.BackupSha256 -AssertProtection $protect
            return $true
        }.GetNewClosure()
        $verifyBackup={param($r)$null=Test-LtBackupSet -Destination (Join-Path $root 'backupset') -ExpectedManifestSha256 $r.BackupSha256 -AssertProtection $protect;return $true}.GetNewClosure()
        $journal=Join-Path $root 'y3-journal.fixture';$oldBackup=Join-Path $root 'y3-old-backup.fixture';$marker=Join-Path $root 'y3-marker.fixture'
        [IO.File]::WriteAllText($journal,'Preparing:new-original');[IO.File]::WriteAllText($oldBackup,'Restored:old-original');[IO.File]::WriteAllText($marker,'residual')
        $journalHash=(Get-FileHash -LiteralPath $journal -Algorithm SHA256).Hash;$backupHash=(Get-FileHash -LiteralPath $oldBackup -Algorithm SHA256).Hash;$markerHash=(Get-FileHash -LiteralPath $marker -Algorithm SHA256).Hash
        $lease=Open-LtCoordinatorLease -Path (Join-Path $root 'coordinator-v1.lock') -AssertProtection $protect
        try{
            $exclusive={param($r)return (Test-LtCoordinatorLease -Lease $lease)}.GetNewClosure()
            $decision=Invoke-LtPublicationReconciliation -ReadyPath (Join-Path $root 'publish-ready.json') -JournalPath (Join-Path $root 'flow-journal.json') -AssertProtection $protect -ReadExternalFingerprint $external -ConfirmRecoverySafe $recover -VerifyBackup $verifyBackup -AssertExclusive $exclusive -Reservation residual -Recovery pending -Finalize
        }finally{$null=Close-LtCoordinatorLease -Lease $lease}
        $expected=if($boundaryCase -eq 'before-rename'){'compensation-permitted'}else{'published'}
        if($decision.Publication -ne $expected){throw ('Reconciliación integral incorrecta: '+$directionCase+'/'+$boundaryCase+'/'+$decision.Publication+'/'+$decision.PublicationReason)}
        if($expected -eq 'published' -and ($decision.JournalStage -ne 'committed' -or -not $decision.ReservationCleanupPending -or -not $decision.RecoveryReviewPending)){throw 'Anotación y recuperación no quedaron separadas.'}
        if((Get-FileHash -LiteralPath $journal -Algorithm SHA256).Hash -ne $journalHash -or (Get-FileHash -LiteralPath $oldBackup -Algorithm SHA256).Hash -ne $backupHash -or (Get-FileHash -LiteralPath $marker -Algorithm SHA256).Hash -ne $markerHash){throw 'Se modificó evidencia Y3 fixture.'}
        Write-Output ('PASS caída de coordinador integral '+$directionCase+' '+$boundaryCase+' -> '+$decision.Publication)
    }
}
