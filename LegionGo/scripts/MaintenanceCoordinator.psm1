# SPDX-License-Identifier: AGPL-3.0-only
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
foreach($name in @('MaintenanceRuntime','MaintenanceWindowsEffects','MaintenanceStartup','MaintenanceTreePublication','MaintenanceFlow','MaintenanceRollback','MaintenanceCoordinatorLease','MaintenanceTransaction')){
    Import-Module (Join-Path $PSScriptRoot ($name+'.psm1')) -ErrorAction Stop -DisableNameChecking
}

# Composition only: callers must supply the independently implemented backup,
# journal, protection and recovery callbacks. Importing does not run effects.
# This is not yet a standalone deployment entry point.
function New-LtMaintenanceCoordinator {
    [CmdletBinding()]
    param([Parameter(Mandatory)][System.Collections.IDictionary]$Configuration,[System.Collections.IDictionary]$Operations)
    foreach($key in @('Direction','Runtime','WindowsEffects','Startup','Tree','Lease')){if(-not $Configuration.Contains($key) -or $null -eq $Configuration[$key]){throw "Falta configuración de coordinador $key."}}
    $null=Test-LtCoordinatorLease -Lease $Configuration.Lease
    if(-not $Operations){$Operations=@{}}
    $expectedLease=if($Operations.Contains('ExpectedLeasePath')){[string]$Operations.ExpectedLeasePath}else{Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::CommonApplicationData)) 'LegionTweaks\maintenance-v1\coordinator-v1.lock'}
    if(-not [string]::Equals([IO.Path]::GetFullPath($Configuration.Lease.Path),[IO.Path]::GetFullPath($expectedLease),[StringComparison]::OrdinalIgnoreCase)){throw 'La exclusión no es la coordinadora del equipo.'}
    $hasFlow=$Configuration.Contains('Flow');$hasFlowConfiguration=$Configuration.Contains('FlowConfiguration')
    if($Configuration.Direction -notin @('Update','Return49') -or $Configuration.Runtime.Direction -ne $Configuration.Direction -or $hasFlow -eq $hasFlowConfiguration){throw 'Configuración de coordinador inválida.'}
    $current=[Security.Principal.WindowsIdentity]::GetCurrent()
    try{
        if(-not $current.User -or $current.User.Value -ne $Configuration.Runtime.CurrentSid -or $current.User.Value -ne $Configuration.Startup.ExpectedSid -or $current.Name -ne $Configuration.WindowsEffects.User){throw 'Identidades del coordinador no coinciden.'}
    }finally{$current.Dispose()}
    if(-not [string]::Equals([IO.Path]::GetFullPath($Configuration.Tree.Live),[IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($Configuration.WindowsEffects.HelperPath)),[StringComparison]::OrdinalIgnoreCase) -or
       -not [string]::Equals([IO.Path]::GetFullPath($Configuration.Runtime.Target.WidgetPath),[IO.Path]::GetFullPath($Configuration.WindowsEffects.WidgetPath),[StringComparison]::OrdinalIgnoreCase) -or
       $Configuration.Runtime.Target.Signer -ne $Configuration.WindowsEffects.Signer){throw 'Destino, tarea y paquete del coordinador no coinciden.'}
    $injected=@('Runtime','WindowsEffects','Startup')|Where-Object {$Operations.Contains($_)}
    if(@($injected).Count -notin @(0,3) -or (@($injected).Count -eq 0 -and ($Operations.Contains('Rollback') -or $Operations.Contains('ExpectedLeasePath')))){throw 'Inyección parcial de operaciones no admitida.'}
    if(@($injected).Count -eq 0){
        if(-not $hasFlowConfiguration -or -not $Configuration.FlowConfiguration.Contains('RestoreTree') -or -not $Configuration.Contains('RollbackConfiguration') -or -not $Configuration.FlowConfiguration.Contains('RequireExistingTrust') -or $Configuration.FlowConfiguration.RequireExistingTrust -ne $true){throw 'Falta respaldo, confianza preexistente o retorno productivo completo.'}
        $images=@('LegionTweaks.Helper.exe','Qam\LegionTweaks.QamProbe.exe')
        $oldPins=$Configuration.Tree.OldPins;$newPins=$Configuration.Tree.NewPins
        if($oldPins.Count -ne 2 -or $newPins.Count -ne 2 -or -not $oldPins.Contains($images[0]) -or -not $oldPins.Contains($images[1]) -or -not $newPins.Contains($images[0]) -or -not $newPins.Contains($images[1]) -or
           $oldPins[$images[0]] -ne $Configuration.Runtime.Source.HelperSha256 -or $oldPins[$images[1]] -ne $Configuration.Runtime.Source.QamSha256 -or
           $newPins[$images[0]] -ne $Configuration.Runtime.Target.HelperSha256 -or $newPins[$images[1]] -ne $Configuration.Runtime.Target.QamSha256){throw 'Pins de imágenes no corresponden a origen y destino.'}
        $common=[Environment]::GetFolderPath([Environment+SpecialFolder]::CommonApplicationData)
        $transaction=Join-Path (Join-Path $common 'LegionTweaks\maintenance-v1\transactions') $Configuration.FlowConfiguration.Id
        foreach($pair in @(@($Configuration.FlowConfiguration.JournalPath,(Join-Path $transaction 'flow-journal.json')),@($Configuration.FlowConfiguration.BackupDestination,(Join-Path $transaction 'backupset')),@($Configuration.Tree.ReadyPath,(Join-Path $transaction 'publish-ready.json')))){
            if(-not [string]::Equals([IO.Path]::GetFullPath([string]$pair[0]),[IO.Path]::GetFullPath([string]$pair[1]),[StringComparison]::OrdinalIgnoreCase)){throw 'Rutas de transacción fuera del ámbito previsto.'}
        }
        $groups=@($Configuration.FlowConfiguration.Groups)
        $helperGroups=@($groups|Where-Object {$_.Name -ceq 'helper' -and [string]::Equals([IO.Path]::GetFullPath([string]$_.SourceRoot),[IO.Path]::GetFullPath($Configuration.Tree.Live),[StringComparison]::OrdinalIgnoreCase)})
        $dataPath=Join-Path (Join-Path $common 'LegionTweaks') $Configuration.Runtime.CurrentSid
        $dataGroups=@($groups|Where-Object {$_.Name -ceq 'user-data' -and [string]::Equals([IO.Path]::GetFullPath([string]$_.SourceRoot),[IO.Path]::GetFullPath($dataPath),[StringComparison]::OrdinalIgnoreCase) -and @($_.ExcludedFiles).Count -eq 1 -and $_.ExcludedFiles[0] -ceq 'fps-y3-admission-v1.guard'})
        if($helperGroups.Count -ne 1 -or $dataGroups.Count -ne 1){throw 'Faltan grupos obligatorios de respaldo.'}
        $packageData=Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::LocalApplicationData)) 'Packages\MazTema.LegionTweaks_wsckx05r95yk0'
        foreach($pair in @(@('widget-settings','Settings'),@('widget-local-state','LocalState'))){
            $path=Join-Path $packageData $pair[1]
            if([IO.Directory]::Exists($path)){
                $matched=@($groups|Where-Object {$_.Name -ceq $pair[0] -and [string]::Equals([IO.Path]::GetFullPath([string]$_.SourceRoot),[IO.Path]::GetFullPath($path),[StringComparison]::OrdinalIgnoreCase) -and $_.ProtectedCopy -eq $true})
                if($matched.Count -ne 1){throw 'Falta respaldo protegido de datos del widget.'}
            }
        }
    }
    foreach($key in @('Runtime','WindowsEffects','Startup')){if($Operations.Contains($key) -and $null -eq $Operations[$key]){throw "Operaciones vacías: $key."}}
    $runtime=if($Operations.Contains('Runtime')){New-LtRuntimeAdapters -Configuration $Configuration.Runtime -Operations $Operations.Runtime}else{New-LtRuntimeAdapters -Configuration $Configuration.Runtime}
    $effects=if($Operations.Contains('WindowsEffects')){New-LtWindowsEffects -Configuration $Configuration.WindowsEffects -Operations $Operations.WindowsEffects}else{New-LtWindowsEffects -Configuration $Configuration.WindowsEffects}
    $startupConfig=$Configuration.Startup
    $startup=if($Operations.Contains('Startup')){New-LtStartupAdapter -ExpectedSid $startupConfig.ExpectedSid -AssertProtection $startupConfig.AssertProtection -Operation $Operations.Startup}else{New-LtStartupAdapter -ExpectedSid $startupConfig.ExpectedSid -AssertProtection $startupConfig.AssertProtection}
    if($hasFlowConfiguration){
        if($Configuration.FlowConfiguration.Direction -ne $Configuration.Direction -or $Configuration.FlowConfiguration.OldSha256 -ne $Configuration.Runtime.Source.HelperSha256 -or $Configuration.FlowConfiguration.NewSha256 -ne $Configuration.Runtime.Target.HelperSha256 -or $Configuration.FlowConfiguration.CoordinatorSha256 -ne $Configuration.Runtime.Coordinator.HelperSha256){throw 'Pins de respaldo y paquetes no coinciden.'}
        $flowConfiguration=@{};foreach($key in $Configuration.FlowConfiguration.Keys){$flowConfiguration[$key]=$Configuration.FlowConfiguration[$key]}
        $flowConfiguration.GetY3Evidence=$runtime.GetSessionEvidence
        $flowConfiguration.RequireY3BackupMatch=$true
        foreach($key in @('AssertPrerequisites','ConfirmCompensationSafety')){
            if(-not $flowConfiguration.ContainsKey($key) -or $flowConfiguration[$key] -isnot [scriptblock]){throw "Falta condición independiente $key."}
            $independent=$flowConfiguration[$key];$revalidate=$runtime.RevalidateAll;$quiescent=$runtime.AwaitQuiescence
            $flowConfiguration[$key]={param($ctx)
                foreach($check in @($revalidate,$quiescent,$independent)){
                    $values=@(& $check $ctx)
                    if($values.Count -ne 1 -or $values[0] -isnot [bool] -or -not $values[0]){throw 'La sesión, quiescencia o seguridad independiente no se confirmaron.'}
                }
                return $true
            }.GetNewClosure()
        }
        $flowAll=New-LtMaintenanceFlow -Configuration $flowConfiguration
        $Configuration.Tree.AfterOldRename=$flowAll.AfterOldRename;$Configuration.Tree.GetReadyEvidence=$flowAll.GetReadyEvidence;$Configuration.Tree.ConfirmPrerequisites=$flowAll.ConfirmPrerequisites
        if($Configuration.FlowConfiguration.Contains('RestoreTree')){
            if(-not [string]::Equals([IO.Path]::GetFullPath($Configuration.FlowConfiguration.RestoreTree),[IO.Path]::GetFullPath($Configuration.Tree.Restore),[StringComparison]::OrdinalIgnoreCase)){throw 'Árboles de retorno no coinciden.'}
            $Configuration.Tree.VerifyRestore=$flowAll.VerifyRestore
        }
        if($Configuration.Contains('RollbackConfiguration')){
            $rollbackConfig=@{};foreach($key in $Configuration.RollbackConfiguration.Keys){$rollbackConfig[$key]=$Configuration.RollbackConfiguration[$key]}
            $rollbackConfig.GetMetadata=$flowAll.GetBackupMetadata
            $rollback=if($Operations.Contains('Rollback')){New-LtRollbackFromBackup -Configuration $rollbackConfig -Operations $Operations.Rollback}else{New-LtRollbackFromBackup -Configuration $rollbackConfig}
            $Configuration.Tree.RestoreExternal=$rollback.RestoreExternal;$Configuration.Tree.VerifyExternal=$rollback.VerifyExternal
        }
        $flow=@{};foreach($key in @('BackupAndVerify','Checkpoint','Commit','ConfirmCompensationSafety','PreserveBlocked')){$flow[$key]=$flowAll[$key]}
    }else{
        if($Configuration.Flow -isnot [System.Collections.IDictionary]){throw 'Flujo inválido.'};$flow=$Configuration.Flow
    }
    $tree=New-LtTreePublicationAdapters -Configuration $Configuration.Tree
    $combined=@{}
    foreach($part in @($runtime,$effects,$startup,$tree,$flow)){
        foreach($key in $part.Keys){
            if($combined.ContainsKey($key) -or $part[$key] -isnot [scriptblock]){throw "Adaptador duplicado o inválido: $key."}
            $combined[$key]=$part[$key]
        }
    }
    $required=@('ValidateRequest','StageAndVerify','InventoryAndFence','AwaitQuiescence','BeginSessions','RevalidateAll','BackupAndVerify','Checkpoint','ArmBarrier','VerifyBarrier','ApplyCertificate','ApplyAppx','ApplyTask','ApplyStartupSuppression','EnableTask','PreparePublication','PublishTree','VerifyPublished','Commit','ReconcileCommit','ConfirmCompensationSafety','Compensate','VerifyCompensated','ReleaseSessions','PreserveSessions','ReleaseFence','PreserveBlocked')
    foreach($key in $required){if(-not $combined.ContainsKey($key)){throw "Falta adaptador de coordinador $key."}}
    $testLease=Get-Command Test-LtCoordinatorLease -CommandType Function -ErrorAction Stop
    foreach($key in @('ValidateRequest','StageAndVerify','InventoryAndFence','AwaitQuiescence','BeginSessions','RevalidateAll','BackupAndVerify','Checkpoint','ArmBarrier','VerifyBarrier','ApplyCertificate','ApplyAppx','ApplyTask','ApplyStartupSuppression','EnableTask','PreparePublication','PublishTree','VerifyPublished','Commit','ReconcileCommit','InspectPublication','ConfirmCompensationSafety','Compensate','VerifyCompensated')){
        $operation=$combined[$key];$lease=$Configuration.Lease
        $combined[$key]={param($ctx)$null=& $testLease -Lease $lease;return (& $operation $ctx)}.GetNewClosure()
    }
    return [pscustomobject]@{Direction=$Configuration.Direction;Adapters=$combined;AuthorizesMaintenance=$false}
}
function Invoke-LtMaintenanceCoordinator {
    [CmdletBinding()]
    param([Parameter(Mandatory)]$Coordinator,[Parameter(Mandatory)]$Request)
    if($Coordinator.AuthorizesMaintenance -ne $false -or $Coordinator.Direction -notin @('Update','Return49') -or $Coordinator.Adapters -isnot [System.Collections.IDictionary]){throw 'Coordinador inválido.'}
    return (Invoke-LtMaintenanceTransaction -Direction $Coordinator.Direction -Adapter $Coordinator.Adapters -Request $Request)
}
Export-ModuleMember -Function New-LtMaintenanceCoordinator,Invoke-LtMaintenanceCoordinator
