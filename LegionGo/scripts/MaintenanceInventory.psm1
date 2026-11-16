# SPDX-License-Identifier: AGPL-3.0-only
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'

# Inventories only explicitly identified product image paths/names; callers must
# separately prove the set covers all legitimate entries, SIDs and sessions.
# Never stops a process or reads a hardware/diary state.
function Get-LtProcessInventory {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string[]]$ExpectedPaths,
          [scriptblock]$ListProcesses,
          [scriptblock]$ReadIdentity)
    if(-not $ExpectedPaths -or $ExpectedPaths.Count -eq 0){throw 'Falta el conjunto de rutas identificadas.'}
    $paths=[Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $names=[Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach($path in $ExpectedPaths){
        if(-not [IO.Path]::IsPathRooted($path)){throw 'Ruta de imagen no absoluta.'}
        $full=[IO.Path]::GetFullPath($path);if(-not $paths.Add($full)){throw 'Ruta de imagen duplicada.'};$null=$names.Add([IO.Path]::GetFileName($full))
    }
    if(-not $ListProcesses){$ListProcesses={Get-CimInstance -ClassName Win32_Process -ErrorAction Stop}}
    if(-not $ReadIdentity){$ReadIdentity={param($process)
        $owner=Invoke-CimMethod -InputObject $process -MethodName GetOwnerSid -ErrorAction Stop
        if($owner.ReturnValue -ne 0 -or -not $owner.Sid){throw 'No se pudo identificar el propietario del proceso.'}
        $p=Get-Process -Id $process.ProcessId -ErrorAction Stop
        try{[pscustomobject]@{Sid=[string]$owner.Sid;StartUtcTicks=$p.StartTime.ToUniversalTime().Ticks}}finally{$p.Dispose()}
    }}
    $results=[Collections.Generic.List[object]]::new()
    foreach($process in @(& $ListProcesses)){
        if(-not $process.Name){throw 'Inventario de procesos incompleto.'}
        if(-not $names.Contains([string]$process.Name)){continue}
        if(-not $process.ExecutablePath){throw 'No se pudo identificar la ruta de un proceso pertinente.'}
        $path=[IO.Path]::GetFullPath([string]$process.ExecutablePath)
        if(-not $paths.Contains($path)){throw 'Hay una copia o ruta pertinente no identificada.'}
        if(-not $process.CommandLine -or -not $process.ProcessId -or $null -eq $process.SessionId -or $process.CreationDate -isnot [DateTime]){throw 'La identidad o argumentos de un proceso pertinente son incompletos.'}
        $prefix='^\s*(?:"'+[regex]::Escape($path)+'"|'+[regex]::Escape($path)+')(?:\s+(.*?))?\s*$'
        if([string]$process.CommandLine -notmatch $prefix){throw 'La línea del proceso no corresponde a su imagen.'}
        $arguments=$Matches[1];$role='interface'
        if([IO.Path]::GetFileName($path) -ieq 'LegionTweaks.Helper.exe'){
            if(-not $arguments){$role='main'}
            elseif($arguments -match '^--(fps-y3-watch|fps-y3-io|controller-watchdog|controller-mode-watch|controller-mode-worker|controller-device-watch|controller-device-worker|display-trial|display-recover)(?:\s|$)'){$role=$Matches[1]}
            else{throw 'Hay un rol del helper desconocido o no cubierto.'}
        }
        $identity=& $ReadIdentity $process
        if(-not $identity -or -not $identity.Sid -or $identity.StartUtcTicks -le 0){throw 'SID u hora de creación de proceso inciertos.'}
        $snapshotTicks=$process.CreationDate.ToUniversalTime().Ticks
        if([Math]::Abs([long]$identity.StartUtcTicks-[long]$snapshotTicks) -gt 10){throw 'El PID cambió entre el inventario y la lectura de identidad.'}
        try{$sid=[Security.Principal.SecurityIdentifier]::new([string]$identity.Sid).Value}catch{throw 'SID de proceso inválido.'}
        if($sid -ne [string]$identity.Sid){throw 'SID de proceso no canónico.'}
        $results.Add([pscustomobject]@{Path=$path;Role=$role;Pid=[int]$process.ProcessId;StartedUtcTicks=[long]$identity.StartUtcTicks;Sid=$sid;SessionId=[int]$process.SessionId})
    }
    return $results.ToArray()
}
function Assert-LtInventoryQuiescent {
    [CmdletBinding()]
    param([AllowEmptyCollection()][object[]]$Inventory)
    if(@($Inventory).Count -ne 0){throw 'Hay procesos o workers pertinentes; esperar cierre normal.'}
    return $true
}
Export-ModuleMember -Function Get-LtProcessInventory,Assert-LtInventoryQuiescent
