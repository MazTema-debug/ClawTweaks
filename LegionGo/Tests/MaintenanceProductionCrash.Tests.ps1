# SPDX-License-Identifier: AGPL-3.0-only
# Terminates only dedicated fixture child processes; Windows effects are doubles.
param([string]$TargetArchive,[string]$TargetExtracted,[string]$MandoHarness,[string]$MandoHarnessSha256)
$ErrorActionPreference='Stop'
foreach($name in @('MaintenanceCoordinatorLease','MaintenanceBackupSet','MaintenanceWindowsEvidence','MaintenanceReconcile')){Import-Module (Join-Path $PSScriptRoot ('..\scripts\'+$name+'.psm1')) -DisableNameChecking}
$directions=@('Update');if($TargetArchive -or $TargetExtracted){if(-not $TargetArchive -or -not $TargetExtracted){throw 'Falta paquete .49 del fixture.'};$directions+= 'Return49'}
foreach($direction in $directions){
    foreach($boundary in @('after-first-image','after-appx','after-old-rename','after-old-checkpoint','before-rename','after-rename','before-annotation','after-annotation')){
        $control=Join-Path ([IO.Path]::GetTempPath()) ('LegionTweaks-factory-crash-'+[Guid]::NewGuid().ToString('N'))
        $null=New-Item -ItemType Directory -Path $control;[IO.File]::WriteAllText((Join-Path $control 'fixture-only.marker'),'synthetic')
        $script=Join-Path $PSScriptRoot 'MaintenanceProductionFactory.Tests.ps1'
        $args='-NoProfile -NonInteractive -ExecutionPolicy Bypass -File "'+$script+'" -ThroughCli -CrashBoundary '+$boundary+' -ControlRoot "'+$control+'"'
        if($direction -eq 'Return49'){$args+=' -TargetArchive "'+$TargetArchive+'" -TargetExtracted "'+$TargetExtracted+'"'}
        if($MandoHarness){$args+=' -MandoHarness "'+$MandoHarness+'" -MandoHarnessSha256 '+$MandoHarnessSha256}
        $info=[Diagnostics.ProcessStartInfo]::new();$info.FileName=Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe';$info.Arguments=$args
        $info.UseShellExecute=$false;$info.CreateNoWindow=$true;$info.RedirectStandardOutput=$true;$info.RedirectStandardError=$true
        $child=[Diagnostics.Process]::Start($info)
        try{
            $until=[DateTime]::UtcNow.AddSeconds(45)
            while(-not [IO.File]::Exists((Join-Path $control 'child-ready')) -and -not $child.HasExited -and [DateTime]::UtcNow -lt $until){Start-Sleep -Milliseconds 40}
            if(-not [IO.File]::Exists((Join-Path $control 'child-ready'))){throw ('La CLI fixture no alcanzó '+$boundary+': '+$child.StandardError.ReadToEnd())}
            $child.Kill();if(-not $child.WaitForExit(10000)){throw 'Proceso fixture no terminó.'}
        }finally{if(-not $child.HasExited){$child.Kill();$child.WaitForExit()};$child.Dispose()}
        $fixture=Get-Content -LiteralPath (Join-Path $control 'fixture-info.json') -Raw -Encoding UTF8|ConvertFrom-Json
        $protect={param($p)return $true};$cert=[Security.Cryptography.X509Certificates.X509Certificate2]::new((Join-Path $PSScriptRoot 'Fixtures\LegionTweaks49-public.cer'))
        $externalConfig=@{ExpectedSid=$fixture.Sid;WidgetPath=$fixture.WidgetPath;WidgetSha256=$fixture.WidgetSha;Version=$fixture.Version;Signer=$fixture.Signer;HelperPath=$fixture.Helper;AssertProtection=$protect}
        $startupState=[pscustomobject]@{Value='{"Skip":true,"Version":1}'}
        $externalOps=@{Package={return [pscustomobject]@{PackageFamilyName='MazTema.LegionTweaks_wsckx05r95yk0';Publisher='CN=LegionTweaks Dev';Architecture='X64';Version=$fixture.Version;Status='Ok';PackageFullName='target-fixture';InstallLocation='X:\fixture'}}.GetNewClosure();TaskXml={return $fixture.TaskXml}.GetNewClosure();Certificate={param($t)return $cert}.GetNewClosure();VerifyWidget={param($p,$l)return $true};Startup={param($s,$p)return $startupState.Value}.GetNewClosure()}
        $priorProgramFiles=$env:ProgramFiles;$env:ProgramFiles=$fixture.Root
        try{
            $external={param($ready)return (Get-LtWindowsPublicationFingerprint -Configuration $externalConfig -Operations $externalOps)}.GetNewClosure()
            $backup=Join-Path $fixture.Transaction 'backupset'
            $verifyBackup={param($ready)$checked=Test-LtBackupSet -Destination $backup -ExpectedManifestSha256 $ready.BackupSha256 -AssertProtection $protect;return ($checked.AuthorizesMaintenance -eq $false)}.GetNewClosure()
            $diary=Join-Path $fixture.Root 'user-data\fps-y3-trial.json';$original=Join-Path $fixture.Root 'user-data\original.fixture';$marker=Join-Path $fixture.Root 'user-data\fps-y3-admission-v1.guard'
            if($boundary -in @('after-rename','before-annotation','after-annotation')){[IO.File]::WriteAllText($diary,'Preparing:new-original fixture')}
            $diaryHash=(Get-FileHash -LiteralPath $diary -Algorithm SHA256).Hash;$originalHash=(Get-FileHash -LiteralPath $original -Algorithm SHA256).Hash;$diaryAcl=(Get-Acl -LiteralPath $diary).Sddl;$originalAcl=(Get-Acl -LiteralPath $original).Sddl
            $lease=Open-LtCoordinatorLease -Path (Join-Path $fixture.Root 'coordinator-v1.lock') -AssertProtection $protect
            try{
                $exclusive={param($ready)return (Test-LtCoordinatorLease -Lease $lease)}.GetNewClosure()
                $decision=Invoke-LtPublicationReconciliation -ReadyPath (Join-Path $fixture.Transaction 'publish-ready.json') -JournalPath (Join-Path $fixture.Transaction 'flow-journal.json') -AssertProtection $protect -ReadExternalFingerprint $external -ConfirmRecoverySafe {param($ready)return $false} -VerifyBackup $verifyBackup -AssertExclusive $exclusive -Reservation residual -Recovery pending -Finalize
            }finally{$null=Close-LtCoordinatorLease -Lease $lease}
            $expected=if($boundary -in @('after-first-image','after-appx','after-old-rename','after-old-checkpoint','before-rename')){'recovery-required'}else{'published'}
            if($decision.Publication -ne $expected -or -not $decision.ReservationCleanupPending -or -not $decision.RecoveryReviewPending){throw ('Reconciliación CLI incorrecta: '+$direction+'/'+$boundary+'/'+$decision.Publication+'/'+$decision.PublicationReason)}
            if($expected -eq 'published'){
                # Model a legitimate later helper consuming the one-shot marker;
                # the reconciler reads only and must not recreate it.
                $startupState.Value='absent'
                $again=Invoke-LtPublicationReconciliation -ReadyPath (Join-Path $fixture.Transaction 'publish-ready.json') -JournalPath (Join-Path $fixture.Transaction 'flow-journal.json') -AssertProtection $protect -ReadExternalFingerprint $external -ConfirmRecoverySafe {param($ready)return $false} -VerifyBackup $verifyBackup -Reservation residual -Recovery pending
                if($again.Publication -ne 'published' -or $startupState.Value -ne 'absent' -or -not $again.ReservationCleanupPending -or -not $again.RecoveryReviewPending){throw 'Consumo posterior de supresión cambió la decisión o el estado fixture.'}
            }
            if((Get-FileHash -LiteralPath $diary -Algorithm SHA256).Hash -ne $diaryHash -or (Get-FileHash -LiteralPath $original -Algorithm SHA256).Hash -ne $originalHash -or (Get-Acl -LiteralPath $diary).Sddl -ne $diaryAcl -or (Get-Acl -LiteralPath $original).Sddl -ne $originalAcl -or -not (Test-Path -LiteralPath $marker)){throw 'Reconciliación alteró evidencia Y3 fixture.'}
            if($MandoHarness -and -not (Test-Path -LiteralPath (Join-Path $fixture.Root 'mando-session\fps-y3-admission-v1.guard'))){throw 'Se perdió la reserva sintética de Mando.'}
            Write-Output ('PASS caída CLI y fábrica '+$direction+' '+$boundary+' -> '+$decision.Publication)
        }finally{$env:ProgramFiles=$priorProgramFiles}
    }
}
