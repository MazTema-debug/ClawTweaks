# SPDX-License-Identifier: AGPL-3.0-only
# Runs production composition against isolated files and injected Windows effects.
param([switch]$FailAppx,[switch]$MissingTrust,[switch]$WrongWidgetVersion,[switch]$MismatchY3Snapshot,[switch]$ActiveWorker,[switch]$ExtraSid,[switch]$ChangeProtectionAfterAppx,[ValidateSet('','target-read','external-read','external-mismatch','annotation')][string]$PostPublishCase='',[ValidateSet('','busy','abandoned_mutex')][string]$BlockedSession='',[switch]$ThroughCli,[string]$TargetArchive,[string]$TargetExtracted,[string]$MandoHarness,[string]$MandoHarnessSha256,
      [ValidateSet('','after-first-image','after-appx','after-old-rename','after-old-checkpoint','before-rename','after-rename','before-annotation','after-annotation')][string]$CrashBoundary='',[string]$ControlRoot)
$ErrorActionPreference='Stop'
foreach($name in @('MaintenanceScope','MaintenanceCoordinatorLease','MaintenanceSession','MaintenanceProductionFactory')){Import-Module (Join-Path $PSScriptRoot ('..\scripts\'+$name+'.psm1')) -DisableNameChecking}
Import-Module (Join-Path $PSScriptRoot '..\scripts\MaintenanceCoordinator.psm1') -DisableNameChecking
Add-Type -TypeDefinition @'
namespace LegionTweaks { public sealed class FpsY3MaintenanceSession { } public sealed class FpsY3LegacyInstanceFence { } }
'@ -WarningAction SilentlyContinue
$assembly=[LegionTweaks.FpsY3MaintenanceSession].Assembly
$root=Join-Path ([IO.Path]::GetTempPath()) ('LegionTweaks-production-fixture-'+[Guid]::NewGuid().ToString('N'));$null=New-Item -ItemType Directory -Path $root
$originalProgramFiles=$env:ProgramFiles;$env:ProgramFiles=$root;$markerStream=$null;$lease=$null;$mandoState=[pscustomobject]@{Session=$null}
try{
    $sid=[Security.Principal.WindowsIdentity]::GetCurrent().User.Value
    $live=Join-Path $root 'LegionTweaks-Z1E';$userData=Join-Path $root 'user-data';$base=Join-Path $root 'LegionTweaks\maintenance-v1'
    $transaction=if($ThroughCli){Join-Path (Join-Path $base 'transactions') ('a'*32)}else{Join-Path $root 'transaction'}
    foreach($path in @((Join-Path $live 'Qam'),$userData,$transaction)){$null=New-Item -ItemType Directory -Path $path -Force}
    $exe=Join-Path $root 'synthetic.exe';Add-Type -TypeDefinition 'public static class SyntheticProgram { public static int Main(){return 0;} }' -OutputAssembly $exe -OutputType ConsoleApplication
    Copy-Item -LiteralPath $exe -Destination (Join-Path $live 'LegionTweaks.Helper.exe');Copy-Item -LiteralPath $exe -Destination (Join-Path $live 'Qam\LegionTweaks.QamProbe.exe')
    [IO.File]::WriteAllText((Join-Path $live 'Uninstall-LegionGo.ps1'),'old fixture')
    [IO.File]::WriteAllText((Join-Path $userData 'fps-y3-trial.json'),'Restored fixture');[IO.File]::WriteAllText((Join-Path $userData 'original.fixture'),'original fixture')
    $journalHash=(Get-FileHash -LiteralPath (Join-Path $userData 'fps-y3-trial.json') -Algorithm SHA256).Hash;$originalHash=(Get-FileHash -LiteralPath (Join-Path $userData 'original.fixture') -Algorithm SHA256).Hash
    $journalStream=[IO.FileStream]::new((Join-Path $userData 'fps-y3-trial.json'),[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read)
    try{$fileId=[LegionTweaks.MaintenanceNative.FileIdentity]::new();if(-not [LegionTweaks.MaintenanceNative.BackupApi]::GetFileInformationByHandle($journalStream.SafeFileHandle,[ref]$fileId)){throw 'Identidad fixture no disponible.'}}finally{$journalStream.Dispose()}
    $idText=('{0:X8}{1:X8}{2:X8}' -f $fileId.Volume,$fileId.IndexHigh,$fileId.IndexLow);$sha=[Security.Cryptography.SHA256]::Create()
    try{$journalIdHash=([BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::ASCII.GetBytes($idText)))).Replace('-','')}finally{$sha.Dispose()}
    $markerStream=[IO.FileStream]::new((Join-Path $userData 'fps-y3-admission-v1.guard'),[IO.FileMode]::CreateNew,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None)
    $certPath=Join-Path $PSScriptRoot 'Fixtures\LegionTweaks49-public.cer';$cert=[Security.Cryptography.X509Certificates.X509Certificate2]::new($certPath)
    function Make-Package([string]$name){
        $dir=Join-Path $root $name;foreach($p in @((Join-Path $dir 'Helper\Qam'),(Join-Path $dir 'Widget'))){$null=New-Item -ItemType Directory -Path $p -Force}
        Copy-Item -LiteralPath $exe -Destination (Join-Path $dir 'Helper\LegionTweaks.Helper.exe');Copy-Item -LiteralPath $exe -Destination (Join-Path $dir 'Helper\Qam\LegionTweaks.QamProbe.exe')
        Copy-Item -LiteralPath $certPath -Destination (Join-Path $dir 'LegionTweaks.cer')
        [IO.File]::WriteAllText((Join-Path $dir 'Widget\fixture.msix'),'fixture widget');[IO.File]::WriteAllText((Join-Path $dir 'Uninstall-LegionGo.ps1'),'target uninstall fixture')
        return @{Archive=(Join-Path $root ($name+'.zip'));Extracted=$dir;ArchiveSha256=('A'*64);HelperPath=(Join-Path $dir 'Helper\LegionTweaks.Helper.exe');QamPath=(Join-Path $dir 'Helper\Qam\LegionTweaks.QamProbe.exe');WidgetPath=(Join-Path $dir 'Widget\fixture.msix');HelperSha256=(Get-FileHash -LiteralPath $exe -Algorithm SHA256).Hash;QamSha256=(Get-FileHash -LiteralPath $exe -Algorithm SHA256).Hash;Signer=$cert.Thumbprint}
    }
    $source=Make-Package 'source';$target=Make-Package 'target';$coordinator=Make-Package 'coordinator'
    $direction='Update';$targetVersion='0.1.50.0'
    if($TargetArchive -or $TargetExtracted){
        if(-not $TargetArchive -or -not $TargetExtracted){throw 'Falta archivo o extracción de retorno.'}
        $widgets=@(Get-ChildItem -LiteralPath (Join-Path $TargetExtracted 'Widget') -Recurse -Filter '*.msix'|Where-Object {$_.FullName -notmatch '[\\/]Dependencies[\\/]'})
        if($widgets.Count -ne 1){throw 'Widget histórico ambiguo.'}
        $target=@{Archive=$TargetArchive;Extracted=$TargetExtracted;ArchiveSha256='5225CA985580E24DB634B022E6C83805CDA5B2514846B6AFB523885BE698B39A';
            HelperPath=(Join-Path $TargetExtracted 'Helper\LegionTweaks.Helper.exe');QamPath=(Join-Path $TargetExtracted 'Helper\Qam\LegionTweaks.QamProbe.exe');WidgetPath=$widgets[0].FullName;
            HelperSha256='9D485EF0E4354278BB9FC496110A7536585B735EA898ED94A4CE4BD9BF438B00';QamSha256='0874C28F843071381FC54A406FAA28682980480FA4ECB8B603B5088C4FA3B50C';Signer='4AB5A8F9579DFDA51130307D543BEEF06EBF4DA0'}
        $direction='Return49';$targetVersion='0.1.49.0'
    }
    $sourceVersion=if($TargetArchive){'0.1.50.0'}else{'0.1.49.0'}
    $sourceIdentity=@{HelperSha256=$source.HelperSha256;QamSha256=$source.QamSha256;Signer=$source.Signer;PackageFamilyName='MazTema.LegionTweaks_wsckx05r95yk0';Publisher='CN=LegionTweaks Dev';Version=$sourceVersion}
    $request=[pscustomobject]@{Direction=$direction;Id=('a'*32);Coordinator=$coordinator;SourcePackage=$source;Target=$target;Source=$sourceIdentity;TargetVersion=$targetVersion;AuthorizesMaintenance=$false}
    $paths=[pscustomobject]@{Base=$base;PackageStore=(Join-Path $base 'packages');Live=$live;UserData=$userData;Transaction=$transaction;Backup=(Join-Path $transaction 'backupset');Journal=(Join-Path $transaction 'flow-journal.json');Ready=(Join-Path $transaction 'publish-ready.json');
        Staging=(Join-Path $root 'staging');Previous=(Join-Path $root 'previous');Failed=(Join-Path $root 'failed');Restore=(Join-Path $root 'restore');WidgetSettings=(Join-Path $root 'missing-settings');WidgetLocalState=(Join-Path $root 'missing-local');AuthorizesMaintenance=$false}
    $observed=[pscustomobject]@{HelperSha256=$source.HelperSha256;QamSha256=$source.QamSha256;HelperSigner=$cert.Thumbprint;QamSigner=$cert.Thumbprint;PackageFamilyName=$sourceIdentity.PackageFamilyName;Publisher=$sourceIdentity.Publisher;Version=$sourceIdentity.Version;PackageFullName='source-fixture';InstallLocation=(Join-Path $root 'installed-widget');AuthorizesMaintenance=$false}
    $state=[pscustomobject]@{Enabled=$false;Fence=0;SourceRestored=$false;SavedTask='';TargetDependencyCount=-1;AfterPublication=$false;Released=0;Preserved=0;FenceReleased=0}
    $runtimeOps=@{ObserveSource={param($c)return $observed}.GetNewClosure();Package={param($p)return [pscustomobject]@{ArchiveSha256=$p.ArchiveSha256;AuthorizesMaintenance=$false}};
        LoadHelper={param($p)return [pscustomobject]@{Sha256=$p.HelperSha256;Signer=$p.Signer;Assembly=$assembly;AuthorizesMaintenance=$false}}.GetNewClosure();VerifyWidget={param($s,$p)return $true};Inventory={param($c)return @()};
        Coverage={param($c,$i)return [pscustomobject]@{FenceSids=@($sid);InspectionSids=@($sid);AuthorizesMaintenance=$false}}.GetNewClosure();Fence={param($c,$h,$s)return [pscustomobject]@{SidCount=1}};
        OpenSession={param($c,$h)$digest=if($MismatchY3Snapshot){'0'*64}else{$journalHash};return [pscustomobject]@{AuthorizesMaintenance=$false;Classification='restored_with_original';ContentSha256=$digest;FileIdentitySha256=$journalIdHash;ProtectionSha256=('E'*64)}}.GetNewClosure();Revalidate={param($s)return $true};CloseSession={param($s)return $true};PreserveSession={param($s)return $true};CloseFence={param($f)return $true}}
    if($MandoHarness -or $MandoHarnessSha256){
        if(-not $MandoHarness -or $MandoHarnessSha256 -cnotmatch '^[A-F0-9]{64}$' -or (Get-FileHash -LiteralPath $MandoHarness -Algorithm SHA256).Hash -cne $MandoHarnessSha256){throw 'Captura fixture de Mando no coincide.'}
        $mando=[Reflection.Assembly]::LoadFrom($MandoHarness);$flags=[Reflection.BindingFlags]'Instance,NonPublic'
        $admissionType=$mando.GetType('Program+FixtureAdmission',$true);$environmentType=$mando.GetType('Program+FixtureMaintenanceEnvironment',$true)
        $sessionType=$mando.GetType('LegionTweaks.FpsY3MaintenanceSession',$true);$begin=$sessionType.GetMethod('Begin',[Reflection.BindingFlags]'Static,NonPublic')
        $gateRoot=Join-Path $root 'mando-session';$null=New-Item -ItemType Directory -Path $gateRoot
        $a=[object[]]::new(2);$a[0]=[string]$gateRoot;$a[1]=[Action]{}
        $admission=$admissionType.GetConstructors($flags)[0].Invoke($a);$environment=$environmentType.GetConstructors($flags)[0].Invoke([object[]]@($admission))
        $inspection=$environmentType.GetField('Current',$flags).GetValue($environment)
        $propertyFlags=[Reflection.BindingFlags]'Instance,NonPublic,Public'
        $inspection.GetType().GetProperty('ContentSha256',$propertyFlags).SetValue($inspection,[string]$journalHash,$null)
        $inspection.GetType().GetProperty('FileIdentitySha256',$propertyFlags).SetValue($inspection,[string]$journalIdHash,$null)
        $sessionModule=Get-Module MaintenanceSession
        $sessionTest=& $sessionModule {Get-Command Test-LtMandoSessions -CommandType Function};$sessionClose=& $sessionModule {Get-Command Close-LtMandoSessions -CommandType Function};$sessionPreserve=& $sessionModule {Get-Command Preserve-LtMandoSessions -CommandType Function}
        $runtimeOps.LoadHelper={param($p)return [pscustomobject]@{Sha256=$p.HelperSha256;Signer=$p.Signer;Assembly=$mando;AuthorizesMaintenance=$false}}.GetNewClosure()
        $runtimeOps.OpenSession={param($c,$h)
            $s=$begin.Invoke($null,@($environment));$mandoState.Session=$s;& $sessionModule {param($owned)$null=$script:ownedSessions.Add($owned)} $s;return $s
        }.GetNewClosure()
        $runtimeOps.Revalidate={param($s)return (& $sessionTest -Sessions @($s))}.GetNewClosure()
        $runtimeOps.CloseSession={param($s)return (& $sessionClose -Sessions @($s))}.GetNewClosure()
        $runtimeOps.PreserveSession={param($s)return (& $sessionPreserve -Sessions @($s))}.GetNewClosure()
    }
    if($ActiveWorker){$runtimeOps.Inventory={param($c)return [pscustomobject]@{Role='fps-y3-io';Sid=$sid}}.GetNewClosure()}
    if($ExtraSid){
        Import-Module (Join-Path $PSScriptRoot '..\scripts\MaintenanceSidCoverage.psm1') -DisableNameChecking
        $coverageFunction=Get-Command Get-LtSidCoverage -CommandType Function
        $readComputer={return [pscustomobject]@{PartOfDomain=$false}};$readAccounts={return [pscustomobject]@{SID=$sid}}.GetNewClosure()
        $readDirs={return @([pscustomobject]@{Name=$sid;Attributes=[IO.FileAttributes]::Directory},[pscustomobject]@{Name='S-1-5-21-1-2-3-1002';Attributes=[IO.FileAttributes]::Directory},[pscustomobject]@{Name='maintenance-v1';Attributes=[IO.FileAttributes]::Directory})}.GetNewClosure()
        $validateDir={param($d)return $true}
        $runtimeOps.Coverage={param($c,$items)& $coverageFunction -CurrentSid $c.CurrentSid -Processes $items -TaskSids @() -ReadComputer $readComputer -ReadLocalAccounts $readAccounts -ReadDataDirectories $readDirs -ValidateMaintenanceDirectory $validateDir}.GetNewClosure()
    }
    if($BlockedSession){$runtimeOps.OpenSession={param($c,$h)$e=[InvalidOperationException]::new('Bloqueo fixture');$e.Data['LegionTweaksReason']=$BlockedSession;$e.Data['ReservationMayRemain']=$true;throw $e}.GetNewClosure()}
    $helper=Join-Path $live 'LegionTweaks.Helper.exe';$work=Split-Path -Parent $helper
    $xml=@"
<Task xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task"><Triggers><LogonTrigger><UserId>$sid</UserId></LogonTrigger></Triggers><Principals><Principal><UserId>$sid</UserId><LogonType>InteractiveToken</LogonType><RunLevel>HighestAvailable</RunLevel></Principal></Principals><Settings><Enabled>true</Enabled></Settings><Actions><Exec><Command>$helper</Command><WorkingDirectory>$work</WorkingDirectory></Exec></Actions></Task>
"@
    $task={param($name)return [pscustomobject]@{Settings=[pscustomobject]@{Enabled=$state.Enabled;DisallowStartIfOnBatteries=$false;StopIfGoingOnBatteries=$false;RestartCount=3;RestartInterval='PT1M';ExecutionTimeLimit='PT0S';MultipleInstances='IgnoreNew'};Actions=@([pscustomobject]@{Execute=$helper;WorkingDirectory=$work});Principal=[pscustomobject]@{UserId=$sid;LogonType='Interactive';RunLevel='Highest'};Triggers=@([pscustomobject]@{CimClass=[pscustomobject]@{CimClassName='MSFT_TaskLogonTrigger'};UserId=$sid})}}.GetNewClosure()
    $win=@{GetCertificate={param($t)return $cert}.GetNewClosure();ImportCertificate={param($f)throw 'No se debe importar certificado'};GetSignature={param($f)return [pscustomobject]@{Status='Valid';SignerCertificate=$cert}}.GetNewClosure();GetFrameworks={param($n)return @()};AddPackage={param($f,$d)$state.TargetDependencyCount=@($d).Count}.GetNewClosure();
        GetPackage={param($n)return [pscustomobject]@{Publisher='CN=LegionTweaks Dev';PackageFamilyName='MazTema.LegionTweaks_wsckx05r95yk0';Architecture='X64';Version=$targetVersion;Status='Ok'}}.GetNewClosure();
        RegisterTask={param($c)$state.Enabled=$false}.GetNewClosure();EnableTask={param($n)$state.Enabled=$true}.GetNewClosure();GetTask=$task}
    $sourceWin=@{};foreach($key in $win.Keys){$sourceWin[$key]=$win[$key]}
    $sourceWin.AddPackage={param($f,$d)$state.SourceRestored=$true}.GetNewClosure()
    $sourceWin.GetPackage={param($n)return [pscustomobject]@{Publisher='CN=LegionTweaks Dev';PackageFamilyName='MazTema.LegionTweaks_wsckx05r95yk0';Architecture='X64';Version=$sourceVersion;Status='Ok'}}.GetNewClosure()
    if($FailAppx){$win.AddPackage={param($f,$d)throw 'AppX fixture en uso'}}
    if($ChangeProtectionAfterAppx){
        if(-not $MandoHarness){throw 'La variación requiere la sesión fixture de Mando.'}
        $win.AddPackage={param($f,$d)$state.TargetDependencyCount=@($d).Count;$environmentType.GetField('Acl',$flags).SetValue($environment,('B'*64))}.GetNewClosure()
    }
    $meta=@{Package={return [pscustomobject]@{PackageFamilyName='MazTema.LegionTweaks_wsckx05r95yk0';Publisher='CN=LegionTweaks Dev';Architecture='X64';Version=$sourceVersion;Status='Ok';PackageFullName='source-fixture'}}.GetNewClosure();
        Task={return [pscustomobject]@{Settings=[pscustomobject]@{Enabled=$true}}};TaskXml={return $xml}.GetNewClosure();Certificate={param($t)return $cert}.GetNewClosure()}
    if($MissingTrust){$meta.Certificate={param($t)return @()}}
    $evidence=@{Package={return [pscustomobject]@{PackageFamilyName='MazTema.LegionTweaks_wsckx05r95yk0';Publisher='CN=LegionTweaks Dev';Architecture='X64';Version=$targetVersion;Status='Ok';PackageFullName='target-fixture';InstallLocation='X:\fixture'}}.GetNewClosure();
        TaskXml={return $xml}.GetNewClosure();Certificate={param($t)return $cert}.GetNewClosure();VerifyWidget={param($p,$l)return $true};Startup={param($s,$p)return '{"Skip":true,"Version":1}'}}
    if($PostPublishCase -in @('external-read','external-mismatch')){
        $evidence.TaskXml={if($state.AfterPublication -and $PostPublishCase -eq 'external-read'){throw 'Lectura externa fixture falló'};if($state.AfterPublication -and $PostPublishCase -eq 'external-mismatch'){return $xml.Replace('</Task>','<!-- cambio fixture --></Task>')};return $xml}.GetNewClosure()
    }
    if($PostPublishCase){
        if($ThroughCli){throw 'El caso pospublicación usa el coordinador directo y operaciones fixture.'}
        $closeSession=$runtimeOps.CloseSession;$preserveSession=$runtimeOps.PreserveSession;$closeFence=$runtimeOps.CloseFence
        $runtimeOps.CloseSession={param($s)$state.Released++;return (& $closeSession $s)}.GetNewClosure()
        $runtimeOps.PreserveSession={param($s)$state.Preserved++;return (& $preserveSession $s)}.GetNewClosure()
        $runtimeOps.CloseFence={param($s)$state.FenceReleased++;return (& $closeFence $s)}.GetNewClosure()
    }
    $snapshot={param($p,$s)return [pscustomobject]@{ProtectionSha256=('E'*64);AuthorizesMaintenance=$false}}
    $guard=New-LtScopeGuard -Anchors @($root) -AllowedRoots @($root) -Snapshot $snapshot
    $lease=Open-LtCoordinatorLease -Path (Join-Path $root 'coordinator-v1.lock') -AssertProtection {param($p)return $true}
    $rollbackOps=@{RegisterTask={param($n,$value)$state.SavedTask=$value}.GetNewClosure();ReadTask={param($n)return $state.SavedTask}.GetNewClosure();TargetCertificate={param($t)return $cert}.GetNewClosure();VerifySourcePackage={param($c)return $state.SourceRestored}.GetNewClosure()}
    $coordinatorOps=@{Runtime=$runtimeOps;WindowsEffects=$win;Startup={param($s,$c)return [pscustomobject]@{AlreadyPresent=$false;Version=1;Skip=$true;AuthorizesMaintenance=$false}};ExpectedLeasePath=$lease.Path;Rollback=$rollbackOps}
    $stagePackage={param($p)return $true}
    if($TargetArchive){
        Import-Module (Join-Path $PSScriptRoot '..\scripts\MaintenancePackage.psm1') -DisableNameChecking
        $stagePackage={param($p)
            $v=Test-LtPinnedPackage -Archive $p.Archive -Extracted $p.Extracted -ExpectedSha256 $p.ArchiveSha256
            $s=@(Test-LtPinnedComponentSignatures -Paths @($p.HelperPath,$p.QamPath,$p.WidgetPath) -ExpectedSigner $p.Signer -AllowOnlyUntrustedRoot)
            return ($v.AuthorizesMaintenance -eq $false -and $s.Count -eq 3 -and (Get-FileHash -LiteralPath $p.HelperPath -Algorithm SHA256).Hash -eq $p.HelperSha256 -and (Get-FileHash -LiteralPath $p.QamPath -Algorithm SHA256).Hash -eq $p.QamSha256)
        }
    }
    $realWidgetIdentity=if($TargetArchive){(Get-Command Get-LtWidgetPackageIdentity -CommandType Function).ScriptBlock}else{$null}
    $widgetIdentity={param($package)
        if($realWidgetIdentity -and $package.WidgetPath -eq $target.WidgetPath){return (& $realWidgetIdentity -Path $package.WidgetPath)}
        $version=if($package.WidgetPath -eq $target.WidgetPath){if($WrongWidgetVersion){'9.9.9.9'}else{$targetVersion}}else{$sourceVersion}
        return [pscustomobject]@{Name='MazTema.LegionTweaks';Publisher='CN=LegionTweaks Dev';Architecture='x64';Version=$version;AuthorizesMaintenance=$false}
    }.GetNewClosure()
    $ops=@{Hardware={return $true};ObserveSource={return $observed}.GetNewClosure();ProtectionRouter={param($a,$b,$c)return $true};SourceWindowsEffects=$sourceWin;WindowsEvidence=$evidence;Metadata=$meta;Stage=@{Package=$stagePackage;ApplyAcl={param($p)return $true}};WidgetIdentity=$widgetIdentity;Coordinator=$coordinatorOps}
    if($ThroughCli){
        $global:LtFixtureCrashBoundary=$CrashBoundary
        if($CrashBoundary){
            $control=[IO.Path]::GetFullPath($ControlRoot);$prefix=[IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\')+'\LegionTweaks-factory-crash-'
            if(-not $control.StartsWith($prefix,[StringComparison]::OrdinalIgnoreCase) -or -not [IO.File]::Exists((Join-Path $control 'fixture-only.marker'))){throw 'Control de interrupción fuera de fixture.'}
            $report=@{Root=$root;Direction=$direction;Sid=$sid;Helper=$helper;TaskXml=$xml;WidgetPath=$target.WidgetPath;WidgetSha=(Get-FileHash -LiteralPath $target.WidgetPath -Algorithm SHA256).Hash;Version=$targetVersion;Signer=$cert.Thumbprint;Transaction=$transaction}
            [IO.File]::WriteAllText((Join-Path $control 'fixture-info.json'),(ConvertTo-Json -InputObject $report -Compress),[Text.UTF8Encoding]::new($false))
            $global:LtFixtureCrashBoundary=$CrashBoundary;$global:LtFixtureControl=$control
            $global:LtFixturePause={param($where)if($where -eq $global:LtFixtureCrashBoundary){[IO.File]::WriteAllText((Join-Path $global:LtFixtureControl 'child-ready'),$where);while($true){Start-Sleep -Milliseconds 100}}}
            if($CrashBoundary -eq 'after-old-rename'){$ops.BeforeOldCheckpointForTest={param($ctx)& $global:LtFixturePause 'after-old-rename'}}
            if($CrashBoundary -eq 'after-old-checkpoint'){$ops.AfterOldCheckpointForTest={param($ctx)& $global:LtFixturePause 'after-old-checkpoint'}}
            if($CrashBoundary -eq 'after-first-image'){$ops.AfterImageForTest={param($relative)& $global:LtFixturePause 'after-first-image';return $true}}
            if($CrashBoundary -eq 'after-appx'){$originalAdd=$win.AddPackage;$win.AddPackage={param($f,$d)& $originalAdd $f $d;& $global:LtFixturePause 'after-appx'}.GetNewClosure()}
        }
        $global:LtFixtureFactory=(Get-Command New-LtProductionCoordinator -CommandType Function).ScriptBlock
        $global:LtFixtureInvoke=(Get-Command Invoke-LtMaintenanceCoordinator -CommandType Function).ScriptBlock
        $global:LtFixtureRequest=$request;$global:LtFixturePaths=$paths;$global:LtFixtureLease=$lease;$global:LtFixtureGuard=$guard;$global:LtFixtureOperations=$ops;$global:LtFixtureSid=$sid;$global:LtFixtureRoot=$root
        $cliRoot=Join-Path $root 'cli';$null=New-Item -ItemType Directory -Path $cliRoot
        Copy-Item -LiteralPath (Join-Path $PSScriptRoot '..\scripts\LegionTweaks-Maintenance-v1.ps1') -Destination (Join-Path $cliRoot 'LegionTweaks-Maintenance-v1.ps1')
        $stubs=@{
            MaintenanceCliEnvironment='function Get-LtCliIdentity { [pscustomobject]@{Sid=$global:LtFixtureSid} }; function Get-LtCliLocations { [pscustomobject]@{CommonData=$global:LtFixtureRoot;ProgramFiles=$global:LtFixtureRoot} }; Export-ModuleMember -Function Get-LtCliIdentity,Get-LtCliLocations'
            MaintenanceHardware='function Assert-LtSupportedHardware { return $true }; Export-ModuleMember -Function Assert-LtSupportedHardware'
            MaintenanceProtection='function Get-LtProtectedSnapshot { param($Roots,[switch]$WithoutDescendants) [pscustomobject]@{Items=1;AuthorizesMaintenance=$false} }; Export-ModuleMember -Function Get-LtProtectedSnapshot'
            MaintenanceCoordinatorLease='function Open-LtCoordinatorLease { param($Path,$AssertProtection) return $global:LtFixtureLease }; function Close-LtCoordinatorLease { param($Lease) return $true }; Export-ModuleMember -Function Open-LtCoordinatorLease,Close-LtCoordinatorLease'
            MaintenanceBackupSet='Export-ModuleMember -Function @()'
            MaintenanceWindowsEvidence='Export-ModuleMember -Function @()'
            MaintenancePublicationDecision='Export-ModuleMember -Function @()'
            MaintenanceJournal='Export-ModuleMember -Function @()'
            MaintenanceReconcile='Export-ModuleMember -Function @()'
            MaintenanceProductionInputs='function Read-LtMaintenanceRequest { param($Path,$ExpectedSha256,$AssertProtection,$PackageStore) return $global:LtFixtureRequest }; Export-ModuleMember -Function Read-LtMaintenanceRequest'
            MaintenanceProductionPaths='function Get-LtProductionPaths { param($Request,$ExpectedSid) return $global:LtFixturePaths }; function Assert-LtProductionVolumes { param($Paths) return $true }; Export-ModuleMember -Function Get-LtProductionPaths,Assert-LtProductionVolumes'
            MaintenanceTransactionDirectory='function Test-LtPriorTransactions { param($Directory,$AssertProtection) return $true }; function New-LtTransactionDirectory { param($Directory,$Id,$AssertProtection) return $global:LtFixturePaths.Transaction }; Export-ModuleMember -Function Test-LtPriorTransactions,New-LtTransactionDirectory'
            MaintenanceScope='function New-LtScopeGuard { param($Anchors,$AllowedRoots) return $global:LtFixtureGuard }; Export-ModuleMember -Function New-LtScopeGuard'
            MaintenanceProductionFactory=@'
function New-LtProductionCoordinator {
 param($Request,$Paths,$ExpectedSid,$Lease,$ScopeGuard)
 $c=& $global:LtFixtureFactory -Request $Request -Paths $Paths -ExpectedSid $ExpectedSid -Lease $Lease -ScopeGuard $ScopeGuard -Operations $global:LtFixtureOperations
 if($global:LtFixtureCrashBoundary){
   $publish=$c.Adapters.PublishTree
   $c.Adapters.PublishTree={param($ctx)& $global:LtFixturePause 'before-rename';$answer=& $publish $ctx;& $global:LtFixturePause 'after-rename';return $answer}.GetNewClosure()
   $checkpoint=$c.Adapters.Checkpoint
   $c.Adapters.Checkpoint={param($ctx)if($ctx.Stage -eq 'committed'){& $global:LtFixturePause 'before-annotation'};$answer=& $checkpoint $ctx;if($ctx.Stage -eq 'committed'){& $global:LtFixturePause 'after-annotation'};return $answer}.GetNewClosure()
 }
 return $c
}
Export-ModuleMember -Function New-LtProductionCoordinator
'@
            MaintenanceCoordinator='function Invoke-LtMaintenanceCoordinator { param($Coordinator,$Request) & $global:LtFixtureInvoke -Coordinator $Coordinator -Request $Request }; Export-ModuleMember -Function Invoke-LtMaintenanceCoordinator'
        }
        foreach($name in $stubs.Keys){[IO.File]::WriteAllText((Join-Path $cliRoot ($name+'.psm1')),[string]$stubs[$name],[Text.UTF8Encoding]::new($false))}
        $requestFile=Join-Path $base 'requests\fixture.json'
        $result=& (Join-Path $cliRoot 'LegionTweaks-Maintenance-v1.ps1') -Action $direction -TransactionId $request.Id -RequestPath $requestFile -RequestSha256 ('A'*64) -Prepared
    }else{
        $coordinatorInstance=New-LtProductionCoordinator -Request $request -Paths $paths -ExpectedSid $sid -Lease $lease -ScopeGuard $guard -Operations $ops
        if($PostPublishCase){
            $publish=$coordinatorInstance.Adapters.PublishTree
            $coordinatorInstance.Adapters.PublishTree={param($ctx)$answer=& $publish $ctx;$state.AfterPublication=$true;return $answer}.GetNewClosure()
            if($PostPublishCase -eq 'target-read'){$verify=$coordinatorInstance.Adapters.VerifyPublished;$coordinatorInstance.Adapters.VerifyPublished={param($ctx)$null=& $verify $ctx;throw 'Lectura de destino fixture falló'}.GetNewClosure()}
            if($PostPublishCase -eq 'annotation'){$checkpoint=$coordinatorInstance.Adapters.Checkpoint;$coordinatorInstance.Adapters.Checkpoint={param($ctx)if($ctx.Stage -eq 'committed'){throw 'Anotación fixture falló'};return (& $checkpoint $ctx)}.GetNewClosure()}
        }
        $result=Invoke-LtMaintenanceCoordinator -Coordinator $coordinatorInstance -Request $request
    }
    if($PostPublishCase){
        if(-not $result.Committed -or $result.CompensationVerified -or $state.FenceReleased -ne 1){throw 'Publicación o fence fixture no se clasificó correctamente.'}
        if($PostPublishCase -eq 'annotation'){
            if($result.Stage -ne 'published-annotation-pending' -or $result.PublicationIntegrity -ne 'verified' -or $state.Released -ne 1 -or $state.Preserved -ne 0){throw 'Anotación fixture clasificada incorrectamente.'}
        }else{
            $expected=if($PostPublishCase -eq 'external-mismatch'){'mismatch'}else{'unverified'}
            if($result.Stage -ne 'published-review-required' -or $result.PublicationIntegrity -ne $expected -or -not $result.ReservationMayRemain -or $state.Preserved -ne 1 -or $state.Released -ne 0){throw ('Revisión fixture incorrecta: '+$result.Stage+'/'+$result.PublicationIntegrity+'/'+$result.PublicationObservation)}
            if($MandoHarness -and -not (Test-Path -LiteralPath (Join-Path $gateRoot 'fps-y3-admission-v1.guard'))){throw 'La sesión fixture de Mando perdió el marcador residual.'}
        }
        Write-Output ('PASS fábrica pospublicación '+$direction+' '+$PostPublishCase+' -> '+$result.Stage+'/'+$result.PublicationIntegrity+'/'+$result.PublicationObservation)
    }elseif($MissingTrust){
        if($result.Stage -ne 'aborted' -or $result.MayHaveEffects -or (Test-Path -LiteralPath $paths.Backup) -or $state.TargetDependencyCount -ne -1){throw 'Confianza ausente no bloqueó antes de efectos.'}
        Write-Output 'PASS confianza ausente bloquea antes de respaldo y efectos'
    }elseif($MismatchY3Snapshot){
        if($result.Stage -ne 'aborted' -or $result.MayHaveEffects -or $state.TargetDependencyCount -ne -1){throw 'Discrepancia Y3 no bloqueó antes de efectos.'}
        Write-Output 'PASS discrepancia entre reserva y respaldo Y3 bloquea antes de efectos'
    }elseif($ChangeProtectionAfterAppx){
        if($result.Stage -ne 'recovery-required' -or $result.CompensationVerified -or -not (Test-Path -LiteralPath (Join-Path $gateRoot 'fps-y3-admission-v1.guard'))){throw 'Cambio de protección no bloqueó compensación ni conservó reserva.'}
        Write-Output 'PASS cambio de protección tras AppX bloquea compensación insegura y conserva reserva'
    }elseif($ActiveWorker -or $ExtraSid){
        if($result.Stage -ne 'aborted' -or $result.MayHaveEffects -or (Test-Path -LiteralPath $paths.Backup) -or $state.TargetDependencyCount -ne -1){throw 'Proceso o identidad adicional no bloqueó antes de efectos.'}
        Write-Output 'PASS worker o SID adicional bloquea la CLI antes de respaldo y efectos'
    }elseif($BlockedSession){
        if($result.Stage -ne 'recovery-required' -or $result.MayHaveEffects -or -not $result.ReservationMayRemain -or $result.ErrorReason -ne $BlockedSession -or (Test-Path -LiteralPath $paths.Backup)){throw 'Bloqueo de sesión no conservó motivo y reserva.'}
        Write-Output ('PASS sesión '+$BlockedSession+' bloquea la CLI y conserva posible reserva')
    }elseif($WrongWidgetVersion){
        if($result.Stage -ne 'aborted' -or $result.MayHaveEffects -or (Test-Path -LiteralPath $paths.Staging) -or $state.TargetDependencyCount -ne -1){throw 'Versión de widget incorrecta no bloqueó antes de efectos.'}
        Write-Output 'PASS versión de widget incorrecta bloquea antes de staging y efectos'
    }elseif($FailAppx){
        if($result.Stage -ne 'aborted' -or -not $result.CompensationVerified -or -not $state.SourceRestored -or $state.SavedTask -cne $xml -or [IO.File]::ReadAllText((Join-Path $live 'Uninstall-LegionGo.ps1')) -cne 'old fixture'){throw ('Compensación de fábrica falló: '+$result.Stage+' / '+$result.Error+' / '+$result.CleanupError)}
        Write-Output 'PASS fábrica productiva compensa AppX en uso con árbol y tarea anteriores'
    }else{
        if($result.Stage -ne 'complete' -or -not $result.Committed -or -not (Test-Path -LiteralPath (Join-Path $live 'allowed-family.txt'))){throw ('Fábrica productiva fixture falló: '+$result.Stage+' / '+$result.Error+' / '+$result.CleanupError)}
        if($TargetArchive -and $state.TargetDependencyCount -le 0){throw 'Dependencias x64 .49 no se entregaron al instalador simulado.'}
        $ready=Get-Content -LiteralPath $paths.Ready -Raw -Encoding UTF8|ConvertFrom-Json
        if($ready.Body.Y3Classification -cne 'restored_with_original' -or $ready.Body.Y3ContentSha256 -cne $journalHash -or $ready.Body.Y3IdentitySha256 -cne $journalIdHash -or $ready.Body.Y3ProtectionSha256 -cnotmatch '^[A-F0-9]{64}$'){throw 'Evidencia Y3 no quedó fijada en publish-ready.'}
        Write-Output ('PASS fábrica productiva compone '+$direction+' con respaldo real y efectos simulados')
    }
    if((Get-FileHash -LiteralPath (Join-Path $userData 'fps-y3-trial.json') -Algorithm SHA256).Hash -ne $journalHash -or (Get-FileHash -LiteralPath (Join-Path $userData 'original.fixture') -Algorithm SHA256).Hash -ne $originalHash){throw 'Fábrica modificó diario u original de fixture.'}
    if($TargetArchive -and ((Get-FileHash -LiteralPath $TargetArchive -Algorithm SHA256).Hash -cne $target.ArchiveSha256 -or (Get-FileHash -LiteralPath $target.HelperPath -Algorithm SHA256).Hash -cne $target.HelperSha256 -or (Get-FileHash -LiteralPath $target.QamPath -Algorithm SHA256).Hash -cne $target.QamSha256)){throw 'La referencia histórica .49 cambió.'}
}finally{if($ChangeProtectionAfterAppx -and $mandoState.Session){$mandoState.Session.Dispose()};if($markerStream){$markerStream.Dispose()};if($lease){$null=Close-LtCoordinatorLease -Lease $lease};$env:ProgramFiles=$originalProgramFiles}
