# SPDX-License-Identifier: AGPL-3.0-only
# Exercises the real tree adapter; external state is mocked and files are fixtures.
$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'MaintenancePublication.Tests.ps1')
Import-Module (Join-Path $PSScriptRoot '..\scripts\MaintenanceTreePublication.psm1') -Force
Import-Module (Join-Path $PSScriptRoot '..\scripts\MaintenanceJournal.psm1') -Force
function Set-RealTreeAdapter($rig,[string]$case){
    $state=$rig.State
    $journal=[pscustomobject]@{Path=(Join-Path $state.Root 'journal.json');Stage='prepared';Sequence=0;Body=[ordered]@{Version=1;Id=[Guid]::NewGuid().ToString('N');Direction='Return49';Stage='prepared';Sequence=0;OldSha256=$state.Hash;NewSha256=$state.Hash;CoordinatorSha256=$state.Hash;BackupManifestSha256=$state.Hash}}
    $protect={param($p)return $true}
    $null=Write-LtTransactionJournal -Path $journal.Path -Body $journal.Body -AssertProtection $protect
    $record={param($stage)
        $journal.Body.Stage=$stage;$journal.Body.Sequence=[long]($journal.Sequence+1)
        $null=Write-LtTransactionJournal -Path $journal.Path -Body $journal.Body -AssertProtection $protect -ExpectedStage $journal.Stage
        $journal.Stage=$stage;$journal.Sequence++;return $true
    }.GetNewClosure()
    $rig.Adapter.Checkpoint={param($ctx)return (& $record $ctx.Stage)}.GetNewClosure()
    $pins=@{'helper.exe'=$state.Hash;'Qam\qam.exe'=$state.Hash}
    $target={param($root)
        if(-not [IO.Directory]::Exists($root) -or [IO.File]::ReadAllText((Join-Path $root 'version.txt')) -ne 'new'){return $false}
        foreach($r in @('helper.exe','Qam\qam.exe')){if((Get-FileHash -LiteralPath (Join-Path $root $r) -Algorithm SHA256).Hash -ne $state.Hash){return $false}};return $true
    }.GetNewClosure()
    $restore={param($root)
        if(-not [IO.Directory]::Exists($root) -or [IO.File]::ReadAllText((Join-Path $root 'version.txt')) -ne 'old'){return $false}
        foreach($r in @('helper.exe','Qam\qam.exe')){if((Get-FileHash -LiteralPath (Join-Path $root $r) -Algorithm SHA256).Hash -ne $state.Hash){return $false}};return $true
    }.GetNewClosure()
    $afterOld={param($ctx)
        $null=& $record 'old-renamed'
        if($state.Fail -eq 'between-renames'){return $false}
        return $true
    }.GetNewClosure()
    $config=@{Live=$state.Live;Staging=$state.Target;Previous=$state.Old;Failed=$state.Failed;Restore=$state.Backup;
        OldPins=$pins;NewPins=$pins;AssertScope={param($ctx)return $true};AfterOldRename=$afterOld;
        RestoreExternal={param($ctx)return $true};VerifyExternal={param($ctx)return $true};VerifyTarget=$target;VerifyRestore=$restore;
        ReadyPath=(Join-Path $state.Root 'publish-ready.json');GetReadyEvidence={param($ctx)return @{Id=[Guid]::NewGuid().ToString('N');ExternalSha256=('A'*64);JournalSha256=('B'*64);BackupSha256=('C'*64);Y3Classification='absent';Y3ContentSha256='';Y3IdentitySha256='';Y3ProtectionSha256=('D'*64)}};
        ConfirmPrerequisites={param($ready)return $true};ReadExternalFingerprint={param($ready)return ('A'*64)};ConfirmRecoverySafe={param($ready)return $true}}
    $tree=New-LtTreePublicationAdapters -Configuration $config
    foreach($name in @('ArmBarrier','VerifyBarrier','PreparePublication','PublishTree','ReconcileCommit','InspectPublication','VerifyPublished','Compensate','VerifyCompensated')){$rig.Adapter[$name]=$tree[$name]}
    if($case -eq 'after-publication'){
        $realPublish=$tree.PublishTree
        $rig.Adapter.PublishTree={param($ctx)$null=& $realPublish $ctx;return $false}.GetNewClosure()
    }
    $rig['Journal']=$journal
    $rig['TreeConfig']=$config
    return $rig
}
foreach($case in @('none','appx','between-renames','after-publication')){
    $rig=Set-RealTreeAdapter (New-PublicationRig $case) $case
    $result=Invoke-LtMaintenanceTransaction -Direction Return49 -Adapter $rig.Adapter -Request @{}
    if($case -eq 'none'){Assert ($result.Stage -eq 'complete' -and $result.Committed) ('Adaptador real no publicó: '+$result.Stage+' / '+$result.Error+' / '+$result.CleanupError)}
    elseif($case -eq 'after-publication'){Assert ($result.Stage -eq 'published-review-required' -and $result.PublicationIntegrity -eq 'unverified' -and $result.Committed -and -not $result.CompensationVerified) ('Publicación no fue reconciliada: '+$result.Error+' / '+$result.CleanupError)}
    else{Assert ($result.Stage -eq 'aborted' -and $result.CompensationVerified) ('Adaptador real no compensó: '+$case+' / '+$result.Error+' / '+$result.CleanupError)}
    $finalJournal=Read-LtTransactionJournal -Path $rig.Journal.Path -AssertProtection {param($p)return $true}
    if($case -eq 'none'){Assert ($finalJournal.Stage -eq 'committed') 'El registro no alcanzó commit.'}
    elseif($case -eq 'after-publication'){Assert ($finalJournal.Stage -eq 'publish-ready') 'El registro no conservó la frontera anterior a publicación.'}
    else{Assert ($finalJournal.Stage -eq 'compensated') 'El registro no confirmó compensación.'}
    Write-Output ('PASS adaptador real de árbol con fixture: '+$case)
}
