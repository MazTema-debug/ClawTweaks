# SPDX-License-Identifier: AGPL-3.0-only
# Real engine, image barrier, checkpoints and directory rename on synthetic files.
$ErrorActionPreference='Stop'
foreach($name in @('MaintenanceTransaction','MaintenanceNative','MaintenanceBackup','MaintenanceImageBarrier')){Import-Module (Join-Path $PSScriptRoot ('..\scripts\'+$name+'.psm1')) -Force}
$names=@('ValidateRequest','StageAndVerify','InventoryAndFence','AwaitQuiescence','BeginSessions','RevalidateAll',
    'BackupAndVerify','Checkpoint','ArmBarrier','VerifyBarrier','ApplyCertificate','ApplyAppx','ApplyTask','ApplyStartupSuppression','EnableTask','PreparePublication','PublishTree',
    'VerifyPublished','Commit','ReconcileCommit','ConfirmCompensationSafety','Compensate','VerifyCompensated','ReleaseSessions','PreserveSessions','ReleaseFence','PreserveBlocked')
function Assert([bool]$ok,[string]$message){if(-not $ok){throw $message}}
function Create-Tree([string]$root,[string]$exe,[string]$version){
    $null=New-Item -ItemType Directory -Path (Join-Path $root 'Qam') -Force
    Copy-Item -LiteralPath $exe -Destination (Join-Path $root 'helper.exe')
    Copy-Item -LiteralPath $exe -Destination (Join-Path $root 'Qam\qam.exe')
    [IO.File]::WriteAllText((Join-Path $root 'version.txt'),$version)
}
function New-PublicationRig([string]$fail){
    $root=Join-Path ([IO.Path]::GetTempPath()) ('LegionTweaks-publication-fixture-'+[Guid]::NewGuid().ToString('N'))
    $null=New-Item -ItemType Directory -Path $root
    $exe=Join-Path $root 'synthetic.exe'
    Add-Type -TypeDefinition 'public static class FixtureProgram { public static int Main() { return 0; } }' -OutputAssembly $exe -OutputType ConsoleApplication
    $state=[pscustomobject]@{Root=$root;Live=(Join-Path $root 'live');Old=(Join-Path $root 'old');Target=(Join-Path $root 'target');Backup=(Join-Path $root 'backup');Failed=(Join-Path $root 'failed');Exe=$exe;Hash=(Get-FileHash -LiteralPath $exe -Algorithm SHA256).Hash;Fail=$fail;Events=[Collections.Generic.List[string]]::new()}
    Create-Tree $state.Live $exe 'old';Create-Tree $state.Target $exe 'new'
    $adapter=@{}
    foreach($n in $names){$name=$n;$adapter[$name]={param($c) $state.Events.Add($name);if($name -eq 'ReconcileCommit'){return 'not-committed'};return $true}.GetNewClosure()}
    $adapter.BackupAndVerify={param($c)
        $records=@(Copy-LtProtectedBackup -SourceRoot $state.Live -DestinationRoot $state.Backup -RelativeFiles @('helper.exe','Qam\qam.exe','version.txt') -AssertProtection {param($a,$b)return $true})
        return ($records.Count -eq 3)
    }.GetNewClosure()
    $adapter.Checkpoint={param($c) $state.Events.Add('Checkpoint:'+ $c.Stage);return (Write-LtCheckpointWriteThrough -Path (Join-Path $state.Root 'transaction.json') -Content ('{"stage":"'+$c.Stage+'"}'))}.GetNewClosure()
    $adapter.ArmBarrier={param($c)
        $state.Events.Add('ArmBarrier')
        foreach($relative in @('helper.exe','Qam\qam.exe')){
            Disable-LtImageForPublication -Image (Join-Path $state.Live $relative) -ExpectedSha256 $state.Hash -AssertScope {param($p)return $true}|Out-Null
            if($state.Fail -eq 'one-image'){return $false}
        }
        return $true
    }.GetNewClosure()
    $adapter.VerifyBarrier={param($c)
        foreach($relative in @('helper.exe','Qam\qam.exe')){if([IO.File]::ReadAllBytes((Join-Path $state.Live $relative))[0] -ne 0){return $false}}
        return $true
    }.GetNewClosure()
    $adapter.ApplyAppx={param($c) $state.Events.Add('ApplyAppx');return ($state.Fail -ne 'appx')}.GetNewClosure()
    $adapter.PreparePublication={param($c)
        $state.Events.Add('PreparePublication');Move-LtDirectorySameParent -Source $state.Live -Destination $state.Old|Out-Null
        if($state.Fail -eq 'between-renames'){return $false}
        return $true
    }.GetNewClosure()
    $adapter.PublishTree={param($c)
        $state.Events.Add('PublishTree')
        Move-LtDirectorySameParent -Source $state.Target -Destination $state.Live|Out-Null
        if($state.Fail -eq 'after-publication'){return $false};return $true
    }.GetNewClosure()
    $adapter.ReconcileCommit={param($c)if([IO.Directory]::Exists($state.Live) -and [IO.File]::ReadAllText((Join-Path $state.Live 'version.txt')) -eq 'new'){return 'committed'};return 'not-committed'}.GetNewClosure()
    $adapter.VerifyPublished={param($c)
        if(-not [IO.Directory]::Exists($state.Live) -or [IO.File]::ReadAllText((Join-Path $state.Live 'version.txt')) -ne 'new'){return $false}
        foreach($relative in @('helper.exe','Qam\qam.exe')){if((Get-FileHash -LiteralPath (Join-Path $state.Live $relative) -Algorithm SHA256).Hash -ne $state.Hash){return $false}}
        return $true
    }.GetNewClosure()
    $adapter.Commit={param($c) return (Write-LtCheckpointWriteThrough -Path (Join-Path $state.Root 'commit.json') -Content '{"committed":true}')}.GetNewClosure()
    $adapter.Compensate={param($c)
        $state.Events.Add('Compensate')
        if([IO.Directory]::Exists($state.Live)){Move-LtDirectorySameParent -Source $state.Live -Destination $state.Failed|Out-Null}
        Move-LtDirectorySameParent -Source $state.Backup -Destination $state.Live|Out-Null
        return $true
    }.GetNewClosure()
    $adapter.VerifyCompensated={param($c)
        if([IO.File]::ReadAllText((Join-Path $state.Live 'version.txt')) -ne 'old'){return $false}
        foreach($relative in @('helper.exe','Qam\qam.exe')){if((Get-FileHash -LiteralPath (Join-Path $state.Live $relative) -Algorithm SHA256).Hash -ne $state.Hash){return $false}}
        return $true
    }.GetNewClosure()
    return @{Adapter=$adapter;State=$state}
}
foreach($case in @('none','one-image','appx','between-renames','after-publication')){
    $rig=New-PublicationRig $case;$r=Invoke-LtMaintenanceTransaction -Direction Return49 -Adapter $rig.Adapter -Request @{}
    if($case -eq 'none'){
        Assert ($r.Stage -eq 'complete' -and [IO.File]::ReadAllText((Join-Path $rig.State.Live 'version.txt')) -eq 'new') 'Publicación fixture falló.'
    }elseif($case -eq 'after-publication'){
        Assert ($r.Stage -eq 'published-review-required' -and $r.PublicationIntegrity -eq 'unverified' -and $r.Committed -and -not $r.CompensationVerified -and [IO.File]::ReadAllText((Join-Path $rig.State.Live 'version.txt')) -eq 'new') 'Publicación con respuesta perdida no quedó en revisión o se revirtió.'
    }else{
        Assert ($r.Stage -eq 'aborted' -and $r.CompensationVerified -and [IO.File]::ReadAllText((Join-Path $rig.State.Live 'version.txt')) -eq 'old') 'Compensación fixture falló.'
    }
    Write-Output ('PASS publicación y compensación fixture: '+$case)
}
