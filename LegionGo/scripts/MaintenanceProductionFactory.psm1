# SPDX-License-Identifier: AGPL-3.0-only
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
foreach($name in @('MaintenanceHardware','MaintenanceSource','MaintenancePackage','MaintenanceScope','MaintenanceProtectionRouter','MaintenanceProductionStage','MaintenanceProductionMetadata','MaintenanceWindowsEvidence','MaintenanceWindowsEffects','MaintenancePayload','MaintenanceTreePublication','MaintenancePublicationDecision','MaintenanceCoordinator','MaintenanceCoordinatorLease')){
    Import-Module (Join-Path $PSScriptRoot ($name+'.psm1')) -ErrorAction Stop -DisableNameChecking
}
function Assert-LtFactoryDecision([scriptblock]$Callback,$Argument){
    $r=@(& $Callback $Argument);if($r.Count -ne 1 -or $r[0] -isnot [bool] -or -not $r[0]){throw 'Condición productiva no confirmada.'}
    return $true
}
function Get-LtFactoryDependencies([string]$Extracted){
    $root=Join-Path $Extracted 'Widget';$paths=[Collections.Generic.List[string]]::new();$pins=@{}
    if([IO.Directory]::Exists($root)){
        foreach($item in Get-ChildItem -LiteralPath $root -Force -Recurse -ErrorAction Stop){
            if($item.Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'Enlace en dependencias del paquete.'}
            if(-not $item.PSIsContainer -and $item.Extension -in @('.appx','.msix') -and $item.FullName -match '(?i)[\\/]Dependencies[\\/](x64|neutral)[\\/]'){
                $paths.Add($item.FullName);$pins[$item.FullName]=(Get-FileHash -LiteralPath $item.FullName -Algorithm SHA256).Hash
            }
        }
    }
    return [pscustomobject]@{Paths=$paths.ToArray();Pins=$pins}
}
function New-LtProductionCoordinator {
    [CmdletBinding()]
    param([Parameter(Mandatory)]$Request,[Parameter(Mandatory)]$Paths,[Parameter(Mandatory)][string]$ExpectedSid,
          [Parameter(Mandatory)]$Lease,[Parameter(Mandatory)]$ScopeGuard,[System.Collections.IDictionary]$Operations)
    if($Request.AuthorizesMaintenance -ne $false -or $Paths.AuthorizesMaintenance -ne $false -or $Request.Direction -cnotin @('Update','Return49')){throw 'Datos productivos inválidos.'}
    $current=[Security.Principal.WindowsIdentity]::GetCurrent();try{if(-not $current.User -or $current.User.Value -ne $ExpectedSid){throw 'Identidad productiva incorrecta.'};$user=$current.Name}finally{$current.Dispose()}
    if(-not $Operations){$Operations=@{}}
    if($Operations.Contains('Hardware')){$hardware=@(& $Operations.Hardware)}else{$hardware=@(Assert-LtSupportedHardware)}
    if($hardware.Count -ne 1 -or $hardware[0] -isnot [bool] -or -not $hardware[0]){throw 'Hardware no confirmado.'}
    $observe=Get-Command Get-LtObservedSource -CommandType Function -ErrorAction Stop
    $testSource=Get-Command Test-LtObservedSource -CommandType Function -ErrorAction Stop
    $scopeTest=Get-Command Test-LtScopeGuard -CommandType Function -ErrorAction Stop
    $leaseTest=Get-Command Test-LtCoordinatorLease -CommandType Function -ErrorAction Stop
    $stageCommand=Get-Command New-LtPreparedTarget -CommandType Function -ErrorAction Stop
    $widgetIdentity=Get-Command Get-LtWidgetPackageIdentity -CommandType Function -ErrorAction Stop
    $targetTest=Get-Command Test-LtDestinationTree -CommandType Function -ErrorAction Stop
    $metadataCommand=Get-Command Get-LtProductionMetadata -CommandType Function -ErrorAction Stop
    $windowsCommand=Get-Command Get-LtWindowsPublicationFingerprint -CommandType Function -ErrorAction Stop
    $effectsCommand=Get-Command New-LtWindowsEffects -CommandType Function -ErrorAction Stop
    $disabledCommand=Get-Command Test-LtDisabledImage -CommandType Function -ErrorAction Stop
    $readReady=Get-Command Read-LtPublishReady -CommandType Function -ErrorAction Stop
    $check=${function:Assert-LtFactoryDecision}
    $observed=@(if($Operations.Contains('ObserveSource')){& $Operations.ObserveSource}else{& $observe})
    if($observed.Count -ne 1 -or -not (& $testSource -Observed $observed[0] -Expected $Request.Source)){throw 'Origen productivo no coincide.'}
    $router=if($Operations.Contains('ProtectionRouter')){$Operations.ProtectionRouter}else{New-LtProtectionRouter -Guard $ScopeGuard -ExpectedSid $ExpectedSid}
    if($router -isnot [scriptblock]){throw 'Protección productiva inválida.'}
    $scope={param($ctx)
        $null=& $scopeTest -Guard $ScopeGuard;$null=& $leaseTest -Lease $Lease
        foreach($path in @($Request.Coordinator.Archive,$Request.Coordinator.Extracted,$Request.SourcePackage.Archive,$Request.SourcePackage.Extracted,$Request.Target.Archive,$Request.Target.Extracted,$observed[0].InstallLocation,$Paths.Live,$Paths.UserData,$Paths.Transaction,$Paths.Backup)){
            $null=& $check $router $path
        }
        return $true
    }.GetNewClosure()
    $treeScope={param($value)if($value -is [string]){return (& $router $value)};return (& $scope $value)}.GetNewClosure()
    $target=$Request.Target;$sourcePackage=$Request.SourcePackage
    $targetWidgetSha=(Get-FileHash -LiteralPath $target.WidgetPath -Algorithm SHA256 -ErrorAction Stop).Hash
    $sourceWidgetSha=(Get-FileHash -LiteralPath $sourcePackage.WidgetPath -Algorithm SHA256 -ErrorAction Stop).Hash
    $helper=Join-Path $Paths.Live 'LegionTweaks.Helper.exe';$qam=Join-Path $Paths.Live 'Qam\LegionTweaks.QamProbe.exe'
    $widgetImage=Join-Path $observed[0].InstallLocation 'LegionTweaks.Widget.exe'
    $runtime=@{Direction=$Request.Direction;Coordinator=$Request.Coordinator;SourcePackage=$sourcePackage;Target=$target;Source=$Request.Source;CurrentSid=$ExpectedSid;
        ExpectedPaths=@($helper,$qam,$widgetImage);TaskSids=@($ExpectedSid);AssertScope=$scope}
    $state=[pscustomobject]@{Coordinator=$null;SourceEffects=$null}
    $safe={param($ctx)
        if(-not $state.Coordinator){throw 'Coordinador no inicializado.'}
        foreach($op in @('RevalidateAll','AwaitQuiescence','VerifyBarrier')){$null=& $check $state.Coordinator.Adapters[$op] $ctx}
        return $true
    }.GetNewClosure()
    $rollbackSafe={param($ctx)
        if(-not $state.Coordinator){throw 'Coordinador no inicializado.'}
        foreach($op in @('RevalidateAll','AwaitQuiescence')){$null=& $check $state.Coordinator.Adapters[$op] $ctx}
        $base=if([IO.Directory]::Exists($Paths.Live)){$Paths.Live}else{$Paths.Previous}
        foreach($pair in @(@('LegionTweaks.Helper.exe',@($Request.Source.HelperSha256,$target.HelperSha256)),@('Qam\LegionTweaks.QamProbe.exe',@($Request.Source.QamSha256,$target.QamSha256)))){
            $null=& $disabledCommand -Image (Join-Path $base $pair[0]) -ExpectedPins $pair[1]
        }
        return (& $scope $ctx)
    }.GetNewClosure()
    $targetEffects=@{CertificatePath=(Join-Path $target.Extracted 'LegionTweaks.cer');Signer=$target.Signer;WidgetPath=$target.WidgetPath;WidgetSha256=$targetWidgetSha;
        PackageName='MazTema.LegionTweaks';PackageFamilyName='MazTema.LegionTweaks_wsckx05r95yk0';Publisher='CN=LegionTweaks Dev';Version=$Request.TargetVersion;TaskName='LegionTweaks-Z1E';User=$user;HelperPath=$helper;AssertSafe=$safe}
    $sourceEffects=@{CertificatePath=(Join-Path $sourcePackage.Extracted 'LegionTweaks.cer');Signer=$sourcePackage.Signer;WidgetPath=$sourcePackage.WidgetPath;WidgetSha256=$sourceWidgetSha;
        PackageName='MazTema.LegionTweaks';PackageFamilyName='MazTema.LegionTweaks_wsckx05r95yk0';Publisher='CN=LegionTweaks Dev';Version=$Request.Source.Version;TaskName='LegionTweaks-Z1E';User=$user;HelperPath=$helper;AssertSafe=$rollbackSafe}
    $targetDeps=Get-LtFactoryDependencies $target.Extracted;$sourceDeps=Get-LtFactoryDependencies $sourcePackage.Extracted
    $targetEffects.DependencyPaths=$targetDeps.Paths;$targetEffects.DependencyPins=$targetDeps.Pins
    $sourceEffects.DependencyPaths=$sourceDeps.Paths;$sourceEffects.DependencyPins=$sourceDeps.Pins
    $state.SourceEffects=if($Operations.Contains('SourceWindowsEffects')){& $effectsCommand -Configuration $sourceEffects -Operations $Operations.SourceWindowsEffects}else{& $effectsCommand -Configuration $sourceEffects}
    $evidenceConfig=@{ExpectedSid=$ExpectedSid;WidgetPath=$target.WidgetPath;WidgetSha256=$targetWidgetSha;Version=$Request.TargetVersion;Signer=$target.Signer;HelperPath=$helper;AssertProtection=$router;RequireStartup=$true}
    $readExternal={param($ctx)
        if($Operations.Contains('WindowsEvidence')){return (& $windowsCommand -Configuration $evidenceConfig -Operations $Operations.WindowsEvidence)}
        return (& $windowsCommand -Configuration $evidenceConfig)
    }.GetNewClosure()
    $metadataConfig=@{ExpectedSid=$ExpectedSid;ExpectedHelper=$helper;SourceSigner=$sourcePackage.Signer;TargetSigner=$target.Signer;SourceVersion=$Request.Source.Version;AssertScope=$scope}
    $getMetadata={param($ctx)
        if($Operations.Contains('Metadata')){return (& $metadataCommand -Configuration $metadataConfig -Operations $Operations.Metadata)}
        return (& $metadataCommand -Configuration $metadataConfig)
    }.GetNewClosure()
    $groups=[Collections.Generic.List[object]]::new()
    $groups.Add([pscustomobject]@{Name='helper';SourceRoot=$Paths.Live})
    $groups.Add([pscustomobject]@{Name='user-data';SourceRoot=$Paths.UserData;ExcludedFiles=@('fps-y3-admission-v1.guard')})
    if([IO.Directory]::Exists($Paths.WidgetSettings)){$groups.Add([pscustomobject]@{Name='widget-settings';SourceRoot=$Paths.WidgetSettings;ProtectedCopy=$true})}
    if([IO.Directory]::Exists($Paths.WidgetLocalState)){$groups.Add([pscustomobject]@{Name='widget-local-state';SourceRoot=$Paths.WidgetLocalState;ProtectedCopy=$true})}
    $verifyTarget={param($root)return (& $targetTest -Extracted $target.Extracted -Tree $root -Family 'MazTema.LegionTweaks_wsckx05r95yk0' -Signer $target.Signer -AssertProtection $router)}.GetNewClosure()
    $commit={param($ctx)
        $null=& $check $verifyTarget $Paths.Live
        $ready=& $readReady -Path $Paths.Ready -AssertProtection $router
        return ((& $readExternal $ctx) -ceq $ready.ExternalSha256)
    }.GetNewClosure()
    $flow=@{Direction=$Request.Direction;Id=$Request.Id;BackupDestination=$Paths.Backup;RestoreTree=$Paths.Restore;Groups=$groups.ToArray();GetMetadata=$getMetadata;JournalPath=$Paths.Journal;
        OldSha256=$Request.Source.HelperSha256;NewSha256=$target.HelperSha256;CoordinatorSha256=$Request.Coordinator.HelperSha256;RequireExistingTrust=$true;AssertProtection=$router;
        AssertGroups={param($items)return (@($items).Count -ge 2)};AssertPrerequisites=$scope;ReadExternalFingerprint=$readExternal;ConfirmPublished=$commit;ConfirmCompensationSafety=$scope;PreserveBlocked={param($ctx)return $true}}
    $pinsOld=@{'LegionTweaks.Helper.exe'=$Request.Source.HelperSha256;'Qam\LegionTweaks.QamProbe.exe'=$Request.Source.QamSha256}
    $pinsNew=@{'LegionTweaks.Helper.exe'=$target.HelperSha256;'Qam\LegionTweaks.QamProbe.exe'=$target.QamSha256}
    $placeholder={param($ctx)throw 'Adaptador productivo no conectado.'}
    $tree=@{Live=$Paths.Live;Staging=$Paths.Staging;Previous=$Paths.Previous;Failed=$Paths.Failed;Restore=$Paths.Restore;OldPins=$pinsOld;NewPins=$pinsNew;
        AssertScope=$treeScope;AfterOldRename=$placeholder;RestoreExternal=$placeholder;VerifyExternal=$placeholder;VerifyTarget=$verifyTarget;VerifyRestore=$placeholder;
        ReadyPath=$Paths.Ready;GetReadyEvidence=$placeholder;ConfirmPrerequisites=$placeholder;ReadExternalFingerprint=$readExternal;ConfirmRecoverySafe={param($r)return $false}}
    $rollback=@{ExpectedSid=$ExpectedSid;ExpectedHelper=$helper;TaskName='LegionTweaks-Z1E';TargetSigner=$target.Signer;SourceSigner=$sourcePackage.Signer;
        ApplySourcePackage={param($ctx)return (& $state.SourceEffects.ApplyAppx $ctx)}.GetNewClosure();AssertScope=$scope}
    $config=@{Direction=$Request.Direction;Runtime=$runtime;WindowsEffects=$targetEffects;Startup=@{ExpectedSid=$ExpectedSid;AssertProtection=$router};Tree=$tree;FlowConfiguration=$flow;RollbackConfiguration=$rollback;Lease=$Lease}
    $coordinator=if($Operations.Contains('Coordinator')){New-LtMaintenanceCoordinator -Configuration $config -Operations $Operations.Coordinator}else{New-LtMaintenanceCoordinator -Configuration $config}
    if($Operations.Contains('AfterImageForTest')){
        if(-not $Operations.Contains('Coordinator') -or -not $Operations.Contains('Stage') -or $Operations.AfterImageForTest -isnot [scriptblock]){throw 'Punto de imagen sin operaciones de fixture.'}
        $tree.AfterImageForTest=$Operations.AfterImageForTest
    }
    if($Operations.Contains('BeforeOldCheckpointForTest') -or $Operations.Contains('AfterOldCheckpointForTest')){
        if(-not $Operations.Contains('Coordinator') -or -not $Operations.Contains('Stage')){throw 'Punto de prueba sin operaciones de fixture.'}
        $oldCheckpoint=$tree.AfterOldRename
        $tree.AfterOldRename={param($ctx)
            if($Operations.Contains('BeforeOldCheckpointForTest')){& $Operations.BeforeOldCheckpointForTest $ctx}
            $null=& $check $oldCheckpoint $ctx
            if($Operations.Contains('AfterOldCheckpointForTest')){& $Operations.AfterOldCheckpointForTest $ctx}
            return $true
        }.GetNewClosure()
    }
    $state.Coordinator=$coordinator;$originalStage=$coordinator.Adapters.StageAndVerify
    $coordinator.Adapters.StageAndVerify={param($ctx)
        $null=& $check $originalStage $ctx;$null=& $leaseTest -Lease $Lease
        foreach($pair in @(@($Request.Coordinator,''),@($Request.SourcePackage,[string]$Request.Source.Version),@($Request.Target,[string]$Request.TargetVersion))){
            $identities=@(if($Operations.Contains('WidgetIdentity')){& $Operations.WidgetIdentity $pair[0]}else{& $widgetIdentity -Path $pair[0].WidgetPath})
            if($identities.Count -ne 1 -or $identities[0].AuthorizesMaintenance -ne $false -or $identities[0].Name -cne 'MazTema.LegionTweaks' -or $identities[0].Publisher -cne 'CN=LegionTweaks Dev' -or $identities[0].Architecture -ine 'x64' -or ($pair[1] -and [version]$identities[0].Version -ne [version]$pair[1])){throw 'Identidad o versión del widget no coincide con la solicitud.'}
        }
        if($Operations.Contains('Stage')){$result=@(& $stageCommand -Target $target -Staging $Paths.Staging -AssertProtection $router -Operations $Operations.Stage)}
        else{$result=@(& $stageCommand -Target $target -Staging $Paths.Staging -AssertProtection $router)}
        if($result.Count -ne 1 -or $result[0] -isnot [bool] -or -not $result[0]){throw 'Staging productivo no verificado.'};return $true
    }.GetNewClosure()
    return $coordinator
}
Export-ModuleMember -Function New-LtProductionCoordinator
