# SPDX-License-Identifier: AGPL-3.0-only
# v1: explicitly prepared maintenance or read-only reconciliation/bookkeeping.
[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateSet('Update','Return49','Inspect','Finalize')][string]$Action,
    [Parameter(Mandatory)][ValidatePattern('^[a-f0-9]{32}$')][string]$TransactionId,
    [string]$WidgetPath,
    [string]$WidgetSha256,
    [version]$PackageVersion,
    [string]$Signer,
    [string]$RequestPath,
    [string]$RequestSha256,
    [switch]$Prepared
)
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
if($Action -in @('Update','Return49')){
    if(-not $Prepared -or -not $RequestPath -or -not [IO.Path]::IsPathRooted($RequestPath) -or $RequestSha256 -cnotmatch '^[A-F0-9]{64}$'){throw 'Faltan confirmación de preparación o solicitud fijada.'}
}elseif(-not $WidgetPath -or -not [IO.Path]::IsPathRooted($WidgetPath) -or $WidgetSha256 -cnotmatch '^[A-F0-9]{64}$' -or -not $PackageVersion -or $Signer -cnotmatch '^[A-F0-9]{40}$'){throw 'Faltan pins para reconciliación.'}
foreach($name in @('MaintenanceCliEnvironment','MaintenanceHardware','MaintenanceProtection','MaintenanceCoordinatorLease','MaintenanceBackupSet','MaintenanceWindowsEvidence','MaintenancePublicationDecision','MaintenanceJournal','MaintenanceReconcile','MaintenanceProductionInputs','MaintenanceProductionPaths','MaintenanceTransactionDirectory','MaintenanceScope','MaintenanceProductionFactory','MaintenanceCoordinator')){
    Import-Module (Join-Path $PSScriptRoot ($name+'.psm1')) -ErrorAction Stop -DisableNameChecking
}
$identity=Get-LtCliIdentity;$sid=$identity.Sid;$locations=Get-LtCliLocations;$common=$locations.CommonData
$base=Join-Path $common 'LegionTweaks\maintenance-v1'
$root=Join-Path (Join-Path $base 'transactions') $TransactionId
$ready=Join-Path $root 'publish-ready.json';$journal=Join-Path $root 'flow-journal.json';$backup=Join-Path $root 'backupset'
$protect={param($path)
    $snapshot=Get-LtProtectedSnapshot -Roots @([IO.Path]::GetFullPath([string]$path))
    return ($snapshot.AuthorizesMaintenance -eq $false -and $snapshot.Items -gt 0)
}
$protectLock={param($path)
    $snapshot=Get-LtProtectedSnapshot -Roots @([IO.Path]::GetFullPath([string]$path)) -WithoutDescendants
    return ($snapshot.AuthorizesMaintenance -eq $false -and $snapshot.Items -gt 0)
}
if($Action -in @('Update','Return49')){$null=Assert-LtSupportedHardware}
$null=& $protectLock $base
$lease=Open-LtCoordinatorLease -Path (Join-Path $base 'coordinator-v1.lock') -AssertProtection $protectLock
try{
    if($Action -in @('Update','Return49')){
        $requestFile=[IO.Path]::GetFullPath($RequestPath);$requestRoot=[IO.Path]::GetFullPath((Join-Path $base 'requests')).TrimEnd('\')
        if(-not $requestFile.StartsWith($requestRoot+'\',[StringComparison]::OrdinalIgnoreCase)){throw 'Solicitud fuera del directorio protegido.'}
        $request=Read-LtMaintenanceRequest -Path $requestFile -ExpectedSha256 $RequestSha256 -AssertProtection $protect -PackageStore (Join-Path $base 'packages')
        if($request.Id -cne $TransactionId -or $request.Direction -cne $Action){throw 'La acción o identidad de solicitud no coinciden.'}
        $paths=Get-LtProductionPaths -Request $request -ExpectedSid $sid
        $null=Assert-LtProductionVolumes -Paths $paths
        $transactions=Split-Path -Parent $paths.Transaction
        $null=Test-LtPriorTransactions -Directory $transactions -AssertProtection $protect
        foreach($path in @($paths.Staging,$paths.Previous,$paths.Failed,$paths.Restore)){
            if([IO.File]::Exists($path) -or [IO.Directory]::Exists($path)){throw 'Ya existe una ruta reservada de la transacción.'}
        }
        $null=& $protectLock $locations.ProgramFiles;$null=& $protect $paths.UserData;$null=& $protectLock $paths.PackageStore
        $anchors=@($base,$paths.PackageStore,$transactions,$locations.ProgramFiles,$paths.UserData)
        $guard=New-LtScopeGuard -Anchors $anchors -AllowedRoots @($base,$locations.ProgramFiles,$paths.UserData)
        $created=New-LtTransactionDirectory -Directory $transactions -Id $TransactionId -AssertProtection $protect
        if(-not [string]::Equals($created,$paths.Transaction,[StringComparison]::OrdinalIgnoreCase)){throw 'Directorio de transacción no coincide.'}
        $coordinator=New-LtProductionCoordinator -Request $request -Paths $paths -ExpectedSid $sid -Lease $lease -ScopeGuard $guard
        $result=Invoke-LtMaintenanceCoordinator -Coordinator $coordinator -Request $request
        $result
        return
    }
    $null=& $protect $root
    $journalBody=Read-LtTransactionJournal -Path $journal -AssertProtection $protect
    if($journalBody.Id -cne $TransactionId){throw 'El directorio y la identidad de transacción no coinciden.'}
    $windows=@{ExpectedSid=$sid;WidgetPath=[IO.Path]::GetFullPath($WidgetPath);WidgetSha256=$WidgetSha256;Version=$PackageVersion.ToString();Signer=$Signer;
        HelperPath=(Join-Path $locations.ProgramFiles 'LegionTweaks-Z1E\LegionTweaks.Helper.exe');AssertProtection=$protect}
    $startupObservation=[pscustomobject]@{Value='unknown'}
    $external={param($r)$observed=Get-LtWindowsPublicationFingerprint -Configuration $windows -IncludeStartupState;$startupObservation.Value=$observed.StartupState;return $observed.Sha256}.GetNewClosure()
    $verifyBackup={param($r)
        $checked=Test-LtBackupSet -Destination $backup -ExpectedManifestSha256 $r.BackupSha256 -AssertProtection $protect
        return ($checked.AuthorizesMaintenance -eq $false -and $checked.Groups -gt 0)
    }.GetNewClosure()
    # A missing canonical tree requires independent physical/Y3 recovery
    # analysis, not automatic rollback from this inspection-only entry point.
    $safeRecovery={param($r)return $false}
    $exclusive={param($r)return (Test-LtCoordinatorLease -Lease $lease)}.GetNewClosure()
    $options=@{ReadyPath=$ready;JournalPath=$journal;AssertProtection=$protect;ReadExternalFingerprint=$external;ConfirmRecoverySafe=$safeRecovery;
        VerifyBackup=$verifyBackup;AssertExclusive=$exclusive;Reservation='unknown';Recovery='unknown'}
    if($Action -eq 'Finalize'){$options.Finalize=$true}
    $result=Invoke-LtPublicationReconciliation @options
    if($result.MaintenanceAuthorized -ne $false){throw 'Resultado inesperado de reconciliación.'}
    $result | Add-Member -NotePropertyName StartupObservation -NotePropertyValue $startupObservation.Value -PassThru
}finally{$null=Close-LtCoordinatorLease -Lease $lease}
