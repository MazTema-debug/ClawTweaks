# SPDX-License-Identifier: AGPL-3.0-only
# Observation only: a synthetic widget starts during a simulated AppX effect.
# No installed package, widget, process or hardware is touched.
$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'MaintenanceTreePublication.Tests.ps1')
$rig=Set-RealTreeAdapter (New-PublicationRig 'none') 'none';$state=$rig.State
$registration=Join-Path $state.Root 'appx-registration.fixture';$observation=Join-Path $state.Root 'widget-observed.fixture';$childScript=Join-Path $state.Root 'synthetic-widget.ps1'
$diary=Join-Path $state.Root 'y3-journal.fixture';$original=Join-Path $state.Root 'y3-original.fixture'
[IO.File]::WriteAllText($diary,'Restored fixture');[IO.File]::WriteAllText($original,'original fixture')
$diaryHash=(Get-FileHash -LiteralPath $diary -Algorithm SHA256).Hash;$originalHash=(Get-FileHash -LiteralPath $original -Algorithm SHA256).Hash
$diaryAcl=(Get-Acl -LiteralPath $diary).Sddl;$originalAcl=(Get-Acl -LiteralPath $original).Sddl
$pipeName='LegionTweaks-fixture-'+[Guid]::NewGuid().ToString('N')
[IO.File]::WriteAllText($childScript,@'
param([string]$Registration,[string]$Helper,[string]$Observation,[string]$Pipe)
$version=[IO.File]::ReadAllText($Registration)
$first=[IO.File]::ReadAllBytes($Helper)[0]
$client=[IO.Pipes.NamedPipeClientStream]::new('.',$Pipe,[IO.Pipes.PipeDirection]::InOut)
try{try{$client.Connect(100);$connection='connected'}catch [TimeoutException]{$connection='disconnected'}}finally{$client.Dispose()}
[IO.File]::WriteAllText($Observation,($version+':'+$first+':'+$connection))
'@)
$rig.Adapter.ApplyAppx={param($ctx)
    [IO.File]::WriteAllText($registration,'target-appx')
    $p=[Diagnostics.ProcessStartInfo]::new();$p.FileName=Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe'
    $p.Arguments='-NoProfile -NonInteractive -ExecutionPolicy Bypass -File "'+$childScript+'" -Registration "'+$registration+'" -Helper "'+(Join-Path $state.Live 'helper.exe')+'" -Observation "'+$observation+'" -Pipe '+$pipeName
    $p.UseShellExecute=$false;$p.CreateNoWindow=$true
    $child=[Diagnostics.Process]::Start($p)
    try{if(-not $child.WaitForExit(10000) -or $child.ExitCode -ne 0){throw 'Widget fixture falló.'}}finally{if(-not $child.HasExited){$child.Kill();$child.WaitForExit()};$child.Dispose()}
    return $true
}.GetNewClosure()
$result=Invoke-LtMaintenanceTransaction -Direction Return49 -Adapter $rig.Adapter -Request @{}
if($result.Stage -ne 'complete' -or [IO.File]::ReadAllText($observation) -cne 'target-appx:0:disconnected'){throw 'No se reprodujo la vista fixture desconectada.'}
if((Get-FileHash -LiteralPath $diary -Algorithm SHA256).Hash -ne $diaryHash -or (Get-FileHash -LiteralPath $original -Algorithm SHA256).Hash -ne $originalHash -or (Get-Acl -LiteralPath $diary).Sddl -ne $diaryAcl -or (Get-Acl -LiteralPath $original).Sddl -ne $originalAcl){throw 'Vista fixture alteró diario u original.'}
Write-Output 'PASS vista fixture transitoria queda sin IPC y conserva bytes y ACL Y3'
