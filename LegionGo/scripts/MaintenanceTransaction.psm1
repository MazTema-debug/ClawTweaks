# SPDX-License-Identifier: AGPL-3.0-only
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# Product-independent orchestration. System effects are supplied by an adapter;
# importing this module never accesses the installed application or hardware.
# This is provisional isolated source, not a deployment entry point.
function Invoke-LtMaintenanceTransaction {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][ValidateSet('Update','Return49')][string]$Direction,
        [Parameter(Mandatory)][System.Collections.IDictionary]$Adapter,
        [Parameter(Mandatory)][object]$Request
    )
    $names=@('ValidateRequest','StageAndVerify','InventoryAndFence','AwaitQuiescence',
        'BeginSessions','RevalidateAll','BackupAndVerify','Checkpoint','ArmBarrier',
        'VerifyBarrier','ApplyCertificate','ApplyAppx','ApplyTask','ApplyStartupSuppression','EnableTask','PreparePublication','PublishTree',
        'VerifyPublished','Commit','ReconcileCommit','ConfirmCompensationSafety','Compensate','VerifyCompensated',
        'ReleaseSessions','PreserveSessions','ReleaseFence','PreserveBlocked')
    foreach($name in $names) {
        if (-not $Adapter.Contains($name) -or $Adapter[$name] -isnot [scriptblock]) { throw "Falta adaptador obligatorio: $name" }
    }
    $context=[ordered]@{Direction=$Direction; Request=$Request; Stage='initial'; Operation=$null; Fenced=$false; Sessions=$false;
        MayHaveEffects=$false; Published=$false; CommitAttempted=$false; Committed=$false; CompensationVerified=$false;
        ReservationMayRemain=$false; PublicationIntegrity='not-checked'; PublicationObservation=$null; ErrorReason=$null; Error=$null; CleanupError=$null; SessionCleanupError=$null; FenceCleanupError=$null}
    $cleanupStarted=$false
    $commitUncertain=$false
    function Call-Required([string]$name) {
        $context.Operation=$name
        $answers=@(& $Adapter[$name] $context)
        if ($answers.Count -ne 1 -or $answers[0] -isnot [bool] -or -not $answers[0]) {throw "Adaptador $name no confirmó su condición."}
        $context.Operation=$null
    }
    function Checkpoint([string]$stage) { $context.Stage=$stage; Call-Required 'Checkpoint' }
    function Require-Current {
        Call-Required 'RevalidateAll'
        Call-Required 'AwaitQuiescence'
        if ($context.MayHaveEffects) {Call-Required 'VerifyBarrier'}
    }
    function Record-Failure([Exception]$failure,[string]$operation){
        $cursor=$failure;$knownResidual=$false
        while($cursor){
            if($cursor.Data -and $cursor.Data.Contains('LegionTweaksReason')){$context.ErrorReason=[string]$cursor.Data['LegionTweaksReason']}
            if($cursor.Data -and $cursor.Data.Contains('ReservationMayRemain')){$context.ReservationMayRemain=[bool]$cursor.Data['ReservationMayRemain'];$knownResidual=$true}
            $property=$cursor.GetType().GetProperty('Reason',[Reflection.BindingFlags]'Public,Instance')
            if($property -and -not $context.ErrorReason){$context.ErrorReason=[string]$property.GetValue($cursor,$null)}
            $cursor=$cursor.InnerException
        }
        if($context.ErrorReason -eq 'abandoned_mutex'){$context.ReservationMayRemain=$true}
        elseif($operation -eq 'BeginSessions' -and -not $knownResidual){$context.ReservationMayRemain=$true}
    }
    function Invoke-SafeCleanup([bool]$preserveSession=$false) {
        if($context.Sessions){
            if($preserveSession){
                $context.ReservationMayRemain=$true
                try{Call-Required 'PreserveSessions';$context.Sessions=$false}
                catch{$context.SessionCleanupError=$_.Exception.Message;Record-Failure $_.Exception 'PreserveSessions'}
            }else{
                try{Call-Required 'ReleaseSessions';$context.Sessions=$false;$context.ReservationMayRemain=$false}
                catch{$context.SessionCleanupError=$_.Exception.Message;$context.ReservationMayRemain=$true;Record-Failure $_.Exception 'ReleaseSessions'}
            }
        }
        if($context.Fenced){
            try{Call-Required 'ReleaseFence';$context.Fenced=$false}
            catch{$context.FenceCleanupError=$_.Exception.Message}
        }
        $errors=@($context.SessionCleanupError,$context.FenceCleanupError | Where-Object {$_})
        if($errors.Count){$context.CleanupError=$errors -join ' | '}
    }
    try {
        Call-Required 'ValidateRequest'
        Call-Required 'StageAndVerify'
        Call-Required 'InventoryAndFence'; $context.Fenced=$true
        Call-Required 'AwaitQuiescence'
        Call-Required 'BeginSessions'; $context.Sessions=$true
        Call-Required 'RevalidateAll'
        Call-Required 'BackupAndVerify'
        Checkpoint 'backup-verified'
        Require-Current
        # Intent must survive failure even if the effect fails halfway through.
        Checkpoint 'arm-barrier-intent'; $context.MayHaveEffects=$true
        Call-Required 'ArmBarrier'; Call-Required 'VerifyBarrier'; Checkpoint 'barrier-armed'
        foreach($pair in @(@('certificate','ApplyCertificate'),@('appx','ApplyAppx'),@('task','ApplyTask'),@('startup','ApplyStartupSuppression'),@('enable-task','EnableTask'))) {
            Require-Current; Checkpoint ($pair[0]+'-intent'); Call-Required $pair[1]; Checkpoint ($pair[0]+'-done')
        }
        Require-Current; Checkpoint 'publish-intent'; Call-Required 'PreparePublication'; Checkpoint 'publish-ready'
        # PublishTree performs the final rename. Once attempted, failure or a
        # lost response must be reconciled from durable evidence, never memory.
        $context.CommitAttempted=$true;Call-Required 'PublishTree';$context.Published=$true;$context.Committed=$true
        Call-Required 'VerifyPublished'; Call-Required 'RevalidateAll'; Call-Required 'AwaitQuiescence'
        Call-Required 'Commit'
        $context.PublicationIntegrity='verified'
        Checkpoint 'committed'
        # No component mutation after durable commit. Release gates separately.
        $cleanupStarted=$true; Invoke-SafeCleanup
        $context.Stage=if($context.CleanupError -or $context.ReservationMayRemain){'committed-cleanup-incomplete'}else{'complete'}
    } catch {
        $failure=$_.Exception;$failedOperation=$context.Operation;$context.Error=$failure.Message
        Record-Failure $failure $failedOperation
        if ($context.CommitAttempted -and -not $context.Committed) {
            try {
                $decision=@(& $Adapter['ReconcileCommit'] $context)
                if ($decision.Count -ne 1 -or $decision[0] -notin @('committed','not-committed','uncertain')) {throw 'Resultado de reconciliación inválido.'}
                if ($decision[0] -eq 'committed') {$context.Committed=$true;$context.Published=$true}
                elseif ($decision[0] -eq 'uncertain') {$commitUncertain=$true}
            } catch { $commitUncertain=$true; $context.CleanupError=$_.Exception.Message }
        }
        if ($context.MayHaveEffects -and -not $context.Committed -and -not $commitUncertain) {
            try {
                # Adapter must independently re-establish all exclusion/recovery
                # prerequisites; it may not perform unsafe compensation blindly.
                Checkpoint 'recovery-required'
                Call-Required 'ConfirmCompensationSafety'
                Call-Required 'Compensate'; Call-Required 'VerifyCompensated'
                Checkpoint 'compensated'
                $context.CompensationVerified=$true
            } catch { $context.CleanupError=$_.Exception.Message }
        }
        $reviewPublished=($context.Committed -and $context.PublicationIntegrity -ne 'verified')
        if($reviewPublished){
            # A rename decision cannot be rolled back blindly. A subsequent
            # read failure is not proof of alteration; an independent mismatch
            # is reported separately. Neither clears the Y3 reservation.
            $context.PublicationIntegrity='unverified'
            if($Adapter.Contains('InspectPublication') -and $Adapter.InspectPublication -is [scriptblock]){
                try{
                    $observed=@(& $Adapter.InspectPublication $context)
                    if($observed.Count -eq 1 -and $observed[0].Reason){
                        $context.PublicationObservation=[string]$observed[0].Reason
                        if($context.PublicationObservation -in @('live-tree-unknown','external-changed')){$context.PublicationIntegrity='mismatch'}
                    }
                }catch{$context.PublicationObservation='inspection-unavailable'}
            }
        }
        if ((-not $context.MayHaveEffects -or $context.Committed -or $context.CompensationVerified) -and -not $cleanupStarted) {
            $cleanupStarted=$true
            $preserveSession=$reviewPublished
            Invoke-SafeCleanup $preserveSession
        } elseif (-not $context.Committed -and $context.MayHaveEffects -and -not $context.CompensationVerified) {
            try {Call-Required 'PreserveBlocked'} catch { $context.CleanupError=$_.Exception.Message }
        }
        $context.Stage=if ($reviewPublished) {'published-review-required'} elseif ($context.Committed -and $context.PublicationIntegrity -eq 'verified' -and $failedOperation -eq 'Checkpoint') {'published-annotation-pending'} elseif ($context.Committed) {'committed-cleanup-incomplete'} elseif ($commitUncertain -or ($context.MayHaveEffects -and -not $context.CompensationVerified) -or $context.CleanupError -or $context.ReservationMayRemain) {'recovery-required'} else {'aborted'}
    }
    return [pscustomobject]$context
}
Export-ModuleMember -Function Invoke-LtMaintenanceTransaction
