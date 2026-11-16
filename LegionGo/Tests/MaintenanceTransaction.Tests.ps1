# SPDX-License-Identifier: AGPL-3.0-only
# The real isolated engine runs against injected effects only; no system effects.
$ErrorActionPreference='Stop'
Import-Module (Join-Path $PSScriptRoot '..\scripts\MaintenanceTransaction.psm1') -Force
$names=@('ValidateRequest','StageAndVerify','InventoryAndFence','AwaitQuiescence','BeginSessions',
'RevalidateAll','BackupAndVerify','Checkpoint','ArmBarrier','VerifyBarrier','ApplyCertificate',
'ApplyAppx','ApplyTask','ApplyStartupSuppression','EnableTask','PreparePublication','PublishTree','VerifyPublished','Commit','ReconcileCommit','ConfirmCompensationSafety','Compensate','VerifyCompensated',
'ReleaseSessions','PreserveSessions','ReleaseFence','PreserveBlocked')
function New-Rig([string]$failure='', [string]$stage='', [int]$occurrence=1, [string]$secondFailure='') {
    $state=[pscustomobject]@{Events=[Collections.Generic.List[string]]::new(); Counts=@{}; Failure=$failure; Stage=$stage; Occurrence=$occurrence; SecondFailure=$secondFailure}
    $adapter=@{}
    foreach($n in $names){
        $name=$n
        $adapter[$name]={param($c)
            $state.Events.Add($name+'|'+$c.Stage)
            if(-not $state.Counts.ContainsKey($name)){$state.Counts[$name]=0};$state.Counts[$name]++
            if(($name -eq $state.Failure -and $state.Counts[$name] -eq $state.Occurrence -and (-not $state.Stage -or $c.Stage -eq $state.Stage)) -or $name -eq $state.SecondFailure){return $false}
            if($name -eq 'ReconcileCommit'){return 'not-committed'}
            return $true
        }.GetNewClosure()
    }
    return @{Adapter=$adapter; State=$state}
}
function Assert([bool]$condition,[string]$message){if(-not $condition){throw $message}}
function Count-Event($rig,[string]$prefix){return @($rig.State.Events | Where-Object {$_ -like ($prefix+'|*')}).Count}
function Test-Case([string]$label,[scriptblock]$test){& $test;Write-Output ('PASS '+$label)}
Test-Case 'update and return49 commit ordered' {
    foreach($direction in @('Update','Return49')){
        $rig=New-Rig; $r=Invoke-LtMaintenanceTransaction -Direction $direction -Adapter $rig.Adapter -Request @{}
        Assert ($r.Stage -eq 'complete' -and $r.Committed -and -not $r.Fenced -and -not $r.Sessions) "$direction did not commit"
        $events=@($rig.State.Events);$publish=[array]::FindIndex($events,[Predicate[string]]{param($x)$x -like 'PublishTree|*'});$verify=[array]::FindIndex($events,[Predicate[string]]{param($x)$x -like 'VerifyPublished|*'});$commit=[array]::FindIndex($events,[Predicate[string]]{param($x)$x -like 'Commit|*'});$release=[array]::FindIndex($events,[Predicate[string]]{param($x)$x -like 'ReleaseSessions|*'})
        Assert ($publish -ge 0 -and $publish -lt $verify -and $verify -lt $commit -and $commit -lt $release) 'incorrect publication order'
        Assert ((Count-Event $rig 'RevalidateAll') -ge 6 -and (Count-Event $rig 'VerifyBarrier') -ge 4) 'missing revalidation'
    }
}
Test-Case 'preflight, inventory, sessions and backup fail before effects' {
    foreach($op in @('ValidateRequest','StageAndVerify','InventoryAndFence','AwaitQuiescence','BeginSessions','RevalidateAll','BackupAndVerify')){
        $rig=New-Rig $op; $r=Invoke-LtMaintenanceTransaction -Direction Update -Adapter $rig.Adapter -Request @{}
        Assert (-not $r.Committed -and -not $r.MayHaveEffects -and (Count-Event $rig 'ArmBarrier') -eq 0 -and (Count-Event $rig 'ApplyAppx') -eq 0) "unsafe preflight $op"
    }
}
Test-Case 'every mutation failure compensates before releasing' {
    foreach($op in @('ArmBarrier','VerifyBarrier','ApplyCertificate','ApplyAppx','ApplyTask','ApplyStartupSuppression','EnableTask','PreparePublication','PublishTree')){
        $rig=New-Rig $op; $r=Invoke-LtMaintenanceTransaction -Direction Return49 -Adapter $rig.Adapter -Request @{}
        Assert (-not $r.Committed -and $r.CompensationVerified -and $r.Stage -eq 'aborted') "compensation missing for $op"
        Assert ((Count-Event $rig 'Compensate') -eq 1 -and (Count-Event $rig 'VerifyCompensated') -eq 1 -and (Count-Event $rig 'ReleaseFence') -eq 1) "incorrect cleanup for $op"
    }
}
Test-Case 'failure after publication never triggers compensation' {
    foreach($op in @('VerifyPublished','Commit')){
        $rig=New-Rig $op;$r=Invoke-LtMaintenanceTransaction -Direction Return49 -Adapter $rig.Adapter -Request @{}
        Assert ($r.Committed -and $r.Stage -eq 'published-review-required' -and $r.PublicationIntegrity -eq 'unverified' -and $r.ReservationMayRemain -and (Count-Event $rig 'PreserveSessions') -eq 1 -and (Count-Event $rig 'ReleaseSessions') -eq 0 -and (Count-Event $rig 'Compensate') -eq 0) "published tree or reservation mishandled after $op"
    }
}
Test-Case 'post-publication mismatch differs from unavailable inspection' {
    foreach($case in @(@('live-tree-unknown','mismatch'),@('external-changed','mismatch'),@('live-tree-unverifiable','unverified'),@('external-unverifiable','unverified'),@('tree-and-external-match','unverified'))){
        $rig=New-Rig 'Commit';$reason=$case[0];$rig.Adapter.InspectPublication={param($c)[pscustomobject]@{Decision='fixture';Reason=$reason}}.GetNewClosure()
        $r=Invoke-LtMaintenanceTransaction -Direction Return49 -Adapter $rig.Adapter -Request @{}
        Assert ($r.PublicationIntegrity -eq $case[1] -and $r.PublicationObservation -eq $reason -and $r.ReservationMayRemain -and (Count-Event $rig 'ReleaseSessions') -eq 0) "incorrect inspection: $reason"
    }
    $rig=New-Rig 'Commit';$rig.Adapter.InspectPublication={param($c)throw 'fixture read failed'}
    $r=Invoke-LtMaintenanceTransaction -Direction Update -Adapter $rig.Adapter -Request @{}
    Assert ($r.PublicationIntegrity -eq 'unverified' -and $r.PublicationObservation -eq 'inspection-unavailable' -and $r.ReservationMayRemain) 'inspection failure incorrectly confirmed integrity'
}
Test-Case 'failed annotation follows successful integrity checks' {
    $rig=New-Rig;$original=$rig.Adapter.Checkpoint;$rig.Adapter.Checkpoint={param($c)if($c.Stage -eq 'committed'){return $false};return (& $original $c)}.GetNewClosure();$r=Invoke-LtMaintenanceTransaction -Direction Return49 -Adapter $rig.Adapter -Request @{}
    Assert ($r.Stage -eq 'published-annotation-pending' -and $r.PublicationIntegrity -eq 'verified' -and -not $r.ReservationMayRemain -and (Count-Event $rig 'ReleaseSessions') -eq 1 -and (Count-Event $rig 'Compensate') -eq 0) 'annotation failure confused with integrity failure'
}
Test-Case 'failed compensation preserves blocked state' {
    $rig=New-Rig 'ApplyAppx' '' 1 'Compensate'; $r=Invoke-LtMaintenanceTransaction -Direction Update -Adapter $rig.Adapter -Request @{}
    Assert ($r.Stage -eq 'recovery-required' -and -not $r.CompensationVerified -and $r.Fenced -and $r.Sessions) 'unsafe failure state'
    Assert ((Count-Event $rig 'PreserveBlocked') -eq 1 -and (Count-Event $rig 'ReleaseSessions') -eq 0 -and (Count-Event $rig 'ReleaseFence') -eq 0) 'released protection after failed compensation'
}
Test-Case 'failure to journal recovery blocks compensation' {
    $rig=New-Rig 'ApplyAppx';$rig.Adapter.Checkpoint={param($c) return ($c.Stage -ne 'recovery-required')}
    $r=Invoke-LtMaintenanceTransaction -Direction Update -Adapter $rig.Adapter -Request @{}
    Assert ($r.Stage -eq 'recovery-required' -and (Count-Event $rig 'Compensate') -eq 0 -and (Count-Event $rig 'ReleaseFence') -eq 0) 'recovery proceeded without a durable intention'
}
Test-Case 'revalidation failure stops before appx' {
    $rig=New-Rig 'RevalidateAll' '' 4; $r=Invoke-LtMaintenanceTransaction -Direction Update -Adapter $rig.Adapter -Request @{}
    Assert (-not $r.Committed -and $r.CompensationVerified -and (Count-Event $rig 'ApplyAppx') -eq 0) 'appx applied after failed revalidation'
}
Test-Case 'cleanup failure after durable commit is not reported complete' {
    $rig=New-Rig 'ReleaseSessions'; $r=Invoke-LtMaintenanceTransaction -Direction Update -Adapter $rig.Adapter -Request @{}
    Assert ($r.Committed -and $r.Stage -eq 'committed-cleanup-incomplete' -and (Count-Event $rig 'Compensate') -eq 0 -and (Count-Event $rig 'ReleaseSessions') -eq 1 -and (Count-Event $rig 'ReleaseFence') -eq 1 -and -not $r.Fenced -and $r.ReservationMayRemain) 'wrong postcommit handling'
}
Test-Case 'independent fence cleanup even when session release fails before effects' {
    $rig=New-Rig 'BackupAndVerify' '' 1 'ReleaseSessions';$r=Invoke-LtMaintenanceTransaction -Direction Update -Adapter $rig.Adapter -Request @{}
    Assert ($r.Stage -eq 'recovery-required' -and (Count-Event $rig 'ReleaseFence') -eq 1 -and -not $r.Fenced -and $r.ReservationMayRemain) 'safe fence cleanup was skipped'
}
Test-Case 'begin failure preserves abandonment and possible residual marker' {
    $rig=New-Rig;$rig.Adapter.BeginSessions={param($ctx)$inner=[InvalidOperationException]::new('fixture');$inner.Data['LegionTweaksReason']='abandoned_mutex';$inner.Data['ReservationMayRemain']=$true;throw [Reflection.TargetInvocationException]::new($inner)}
    $r=Invoke-LtMaintenanceTransaction -Direction Update -Adapter $rig.Adapter -Request @{}
    Assert ($r.Stage -eq 'recovery-required' -and $r.ErrorReason -eq 'abandoned_mutex' -and $r.ReservationMayRemain -and -not $r.Fenced -and (Count-Event $rig 'ReleaseFence') -eq 1) 'abandonment was hidden or fence retained'
}
Test-Case 'unproven independent compensation blocks after effect' {
    $rig=New-Rig 'RevalidateAll' '' 4 'ConfirmCompensationSafety'
    $r=Invoke-LtMaintenanceTransaction -Direction Update -Adapter $rig.Adapter -Request @{}
    Assert ($r.Stage -eq 'recovery-required' -and (Count-Event $rig 'Compensate') -eq 0 -and (Count-Event $rig 'ReleaseFence') -eq 0 -and (Count-Event $rig 'PreserveBlocked') -eq 1) 'unproven compensation proceeded'
}
Test-Case 'uncertain commit retains barriers without compensation' {
    $rig=New-Rig 'PublishTree';$rig.Adapter['ReconcileCommit']={param($c) 'uncertain'}
    $r=Invoke-LtMaintenanceTransaction -Direction Update -Adapter $rig.Adapter -Request @{}
    Assert ($r.Stage -eq 'recovery-required' -and -not $r.Committed -and (Count-Event $rig 'Compensate') -eq 0 -and (Count-Event $rig 'PreserveBlocked') -eq 1 -and (Count-Event $rig 'ReleaseFence') -eq 0) 'uncertain commit did not retain barriers'
}
Test-Case 'verified commit after lost response does not roll back' {
    $rig=New-Rig 'PublishTree';$rig.Adapter['ReconcileCommit']={param($c) 'committed'}
    $r=Invoke-LtMaintenanceTransaction -Direction Update -Adapter $rig.Adapter -Request @{}
    Assert ($r.Stage -eq 'published-review-required' -and $r.Committed -and $r.PublicationIntegrity -eq 'unverified' -and $r.ReservationMayRemain -and (Count-Event $rig 'Compensate') -eq 0) 'verified commit was rolled back or reservation cleared'
}
Test-Case 'missing mandatory adapter fails closed' {
    $rig=New-Rig;$rig.Adapter.Remove('VerifyBarrier');$failed=$false
    try {Invoke-LtMaintenanceTransaction -Direction Update -Adapter $rig.Adapter -Request @{} | Out-Null}catch{$failed=$true}
    Assert ($failed -and $rig.State.Events.Count -eq 0) 'missing adapter accepted'
}
