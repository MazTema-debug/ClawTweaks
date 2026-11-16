# SPDX-License-Identifier: AGPL-3.0-only
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
foreach($name in @('MaintenancePackage','MaintenanceInventory','MaintenanceSidCoverage','MaintenanceSession','MaintenanceSource')){Import-Module (Join-Path $PSScriptRoot ($name+'.psm1')) -ErrorAction Stop}
function Invoke-LtFenceFactory([Reflection.MethodInfo]$Method,[string[]]$Sids){
    $list=[Collections.Generic.List[string]]::new();foreach($sid in $Sids){$list.Add([string]$sid)}
    $arguments=[object[]]::new(1);$arguments[0]=$list
    return $Method.Invoke($null,$arguments)
}

# Inventory/session portion of a future production coordinator. It does not
# perform shutdown, backup, trust import, deployment or release by itself.
function New-LtRuntimeAdapters {
    [CmdletBinding()]
    param([Parameter(Mandatory)][System.Collections.IDictionary]$Configuration,[System.Collections.IDictionary]$Operations)
    foreach($k in @('Direction','Coordinator','Target','SourcePackage','Source','CurrentSid','ExpectedPaths','TaskSids','AssertScope')){
        if(-not $Configuration.Contains($k) -or $null -eq $Configuration[$k]){throw "Falta configuración de runtime $k."}
    }
    if($Configuration.AssertScope -isnot [scriptblock] -or $Configuration.Direction -notin @('Update','Return49')){throw 'Dirección o protección inválida.'}
    foreach($role in @('Coordinator','Target','SourcePackage')){
        $p=$Configuration[$role]
        if($p -isnot [System.Collections.IDictionary]){throw "Falta paquete $role."}
        foreach($key in @('Archive','Extracted','ArchiveSha256','HelperPath','QamPath','WidgetPath','HelperSha256','QamSha256','Signer')){if(-not $p.Contains($key) -or -not $p[$key]){throw "Falta pin $role/$key."}}
        if($p.ArchiveSha256 -notmatch '^[0-9A-Fa-f]{64}$' -or $p.HelperSha256 -notmatch '^[0-9A-Fa-f]{64}$' -or $p.QamSha256 -notmatch '^[0-9A-Fa-f]{64}$' -or $p.Signer -notmatch '^[0-9A-Fa-f]{40}$'){throw 'Pin de paquete inválido.'}
        $root=[IO.Path]::GetFullPath([string]$p.Extracted).TrimEnd('\')
        if(-not [string]::Equals([IO.Path]::GetFullPath([string]$p.HelperPath),[IO.Path]::Combine($root,'Helper\LegionTweaks.Helper.exe'),[StringComparison]::OrdinalIgnoreCase) -or
           -not [string]::Equals([IO.Path]::GetFullPath([string]$p.QamPath),[IO.Path]::Combine($root,'Helper\Qam\LegionTweaks.QamProbe.exe'),[StringComparison]::OrdinalIgnoreCase) -or
           -not [IO.Path]::GetFullPath([string]$p.WidgetPath).StartsWith(($root+'\Widget\'),[StringComparison]::OrdinalIgnoreCase)){throw 'Componentes fuera de la extracción fijada.'}
    }
    if($Configuration.SourcePackage.HelperSha256 -ne $Configuration.Source.HelperSha256 -or $Configuration.SourcePackage.QamSha256 -ne $Configuration.Source.QamSha256 -or $Configuration.SourcePackage.Signer -ne $Configuration.Source.Signer){throw 'El paquete de origen no coincide con los pins instalados.'}
    if($Configuration.Direction -eq 'Return49' -and ($Configuration.Target.ArchiveSha256 -ne '5225CA985580E24DB634B022E6C83805CDA5B2514846B6AFB523885BE698B39A' -or $Configuration.Target.HelperSha256 -ne '9D485EF0E4354278BB9FC496110A7536585B735EA898ED94A4CE4BD9BF438B00' -or $Configuration.Target.QamSha256 -ne '0874C28F843071381FC54A406FAA28682980480FA4ECB8B603B5088C4FA3B50C' -or $Configuration.Target.Signer -ne '4AB5A8F9579DFDA51130307D543BEEF06EBF4DA0')){throw 'El destino de retorno no es .49 exacta.'}
    $invokeFence=${function:Invoke-LtFenceFactory}
    if(-not $Operations){$Operations=@{
        ObserveSource={param($c) Get-LtObservedSource}
        Package={param($p)
            $package=Test-LtPinnedPackage -Archive $p.Archive -Extracted $p.Extracted -ExpectedSha256 $p.ArchiveSha256
            if((Get-FileHash -LiteralPath $p.HelperPath -Algorithm SHA256).Hash -ne $p.HelperSha256 -or (Get-FileHash -LiteralPath $p.QamPath -Algorithm SHA256).Hash -ne $p.QamSha256){throw 'Los componentes del paquete no coinciden con sus pins.'}
            $signatures=@(Test-LtPinnedComponentSignatures -Paths @($p.HelperPath,$p.QamPath,$p.WidgetPath) -ExpectedSigner $p.Signer -AllowOnlyUntrustedRoot)
            if($signatures.Count -ne 3){throw 'No se verificaron las tres firmas.'}
            return $package
        }
        LoadHelper={param($p) Import-LtPinnedHelperAssembly -Path $p.HelperPath -ExpectedSha256 $p.HelperSha256 -ExpectedSigner $p.Signer -AllowOnlyUntrustedRoot}
        VerifyWidget={param($s,$p) Test-LtInstalledWidgetContents -PackagePath $p.WidgetPath -InstallLocation $s.InstallLocation}
        Inventory={param($c) @(Get-LtProcessInventory -ExpectedPaths $c.ExpectedPaths)}
        Coverage={param($c,$items) Get-LtSidCoverage -CurrentSid $c.CurrentSid -Processes $items -TaskSids $c.TaskSids}
        Fence={param($c,$h,$sids)
            $type=$h.Assembly.GetType('LegionTweaks.FpsY3LegacyInstanceFence',$true)
            return (& $invokeFence -Method ($type.GetMethod('AcquireVerifiedSids',[Reflection.BindingFlags]'Public,Static')) -Sids $sids)
        }.GetNewClosure()
        OpenSession={param($c,$h) Open-LtMandoSession -VerifiedHelper $h -ExpectedSid $c.CurrentSid}
        Revalidate={param($s) Test-LtMandoSessions -Sessions @($s)}
        CloseSession={param($s) Close-LtMandoSessions -Sessions @($s)}
        PreserveSession={param($s) Preserve-LtMandoSessions -Sessions @($s)}
        CloseFence={param($f) $f.Dispose();return $true}
    }}
    foreach($k in @('ObserveSource','Package','LoadHelper','VerifyWidget','Inventory','Coverage','Fence','OpenSession','Revalidate','CloseSession','PreserveSession','CloseFence')){if(-not $Operations.Contains($k) -or $Operations[$k] -isnot [scriptblock]){throw "Falta operación de runtime $k."}}
    $testSource=Get-Command -Name Test-LtObservedSource -CommandType Function -ErrorAction Stop
    $state=[pscustomobject]@{Source=$null;SourcePackage=$null;CoordinatorPackage=$null;TargetPackage=$null;Helper=$null;Fence=$null;Session=$null;Sids=$null}
    $check={param($ctx)
        $v=@(& $Configuration.AssertScope $ctx)
        if($v.Count -ne 1 -or $v[0] -isnot [bool] -or -not $v[0]){throw 'Ámbito o protección del runtime inciertos.'};return $true
    }.GetNewClosure()
    $validate={param($ctx)
        $null=& $check $ctx
        $source=@(& $Operations.ObserveSource $Configuration)
        if($source.Count -ne 1 -or -not (& $testSource -Observed $source[0] -Expected $Configuration.Source)){throw 'El origen observado no coincide con sus pins.'}
        $state.Source=$source[0]
        $sid=[Security.Principal.SecurityIdentifier]::new([string]$Configuration.CurrentSid).Value
        if($sid -ne $Configuration.CurrentSid){throw 'SID no canónico.'}
        return $true
    }.GetNewClosure()
    $stage={param($ctx)
        $null=& $check $ctx
        $coordinator=@(& $Operations.Package $Configuration.Coordinator)
        if($coordinator.Count -ne 1 -or $coordinator[0].AuthorizesMaintenance -ne $false -or $coordinator[0].ArchiveSha256 -ne $Configuration.Coordinator.ArchiveSha256){throw 'Paquete coordinador no coincide con su pin.'}
        $target=@(& $Operations.Package $Configuration.Target)
        if($target.Count -ne 1 -or $target[0].AuthorizesMaintenance -ne $false -or $target[0].ArchiveSha256 -ne $Configuration.Target.ArchiveSha256){throw 'Paquete destino no coincide con su pin.'}
        $sourcePackage=@(& $Operations.Package $Configuration.SourcePackage)
        if($sourcePackage.Count -ne 1 -or $sourcePackage[0].AuthorizesMaintenance -ne $false -or $sourcePackage[0].ArchiveSha256 -ne $Configuration.SourcePackage.ArchiveSha256){throw 'Paquete de origen no coincide con su pin.'}
        $widget=@(& $Operations.VerifyWidget $state.Source $Configuration.SourcePackage)
        if($widget.Count -ne 1 -or $widget[0] -isnot [bool] -or -not $widget[0]){throw 'Widget instalado no coincide con su paquete de origen.'}
        $state.CoordinatorPackage=$coordinator[0];$state.TargetPackage=$target[0];$state.SourcePackage=$sourcePackage[0];$helpers=@(& $Operations.LoadHelper $Configuration.Coordinator)
        if($helpers.Count -ne 1 -or $helpers[0].AuthorizesMaintenance -ne $false -or $helpers[0].Sha256 -ne $Configuration.Coordinator.HelperSha256 -or $helpers[0].Signer -ne $Configuration.Coordinator.Signer -or $helpers[0].Assembly -isnot [Reflection.Assembly] -or -not $helpers[0].Assembly.GetType('LegionTweaks.FpsY3MaintenanceSession',$false) -or -not $helpers[0].Assembly.GetType('LegionTweaks.FpsY3LegacyInstanceFence',$false)){throw 'Helper coordinador carece de la API fijada.'}
        $state.Helper=$helpers[0];return $true
    }.GetNewClosure()
    $fence={param($ctx)
        $null=& $check $ctx
        $items=@(& $Operations.Inventory $Configuration);$coverage=@(& $Operations.Coverage $Configuration $items)
        if($coverage.Count -ne 1 -or $coverage[0].AuthorizesMaintenance -ne $false -or @($coverage[0].FenceSids).Count -eq 0 -or @($coverage[0].InspectionSids).Count -ne 1 -or $coverage[0].InspectionSids[0] -ne $Configuration.CurrentSid){throw 'Cobertura de identidades incompleta.'}
        $state.Sids=@($coverage[0].FenceSids);$opened=@(& $Operations.Fence $Configuration $state.Helper $state.Sids)
        if($opened.Count -ne 1){throw 'No se confirmó la barrera de identidades.'}
        if($opened[0].SidCount -ne $state.Sids.Count){
            $released=@(& $Operations.CloseFence $opened[0])
            if($released.Count -ne 1 -or $released[0] -isnot [bool] -or -not $released[0]){throw 'Barrera parcial no pudo liberarse.'}
            throw 'No se confirmó la barrera de identidades.'
        }
        $state.Fence=$opened[0];return $true
    }.GetNewClosure()
    $quiet={param($ctx)
        $null=& $check $ctx
        if($ctx -is [System.Collections.IDictionary] -and $ctx.Contains('MayHaveEffects') -and -not [bool]$ctx['MayHaveEffects']){
            $source=@(& $Operations.ObserveSource $Configuration)
            if($source.Count -ne 1 -or -not (& $testSource -Observed $source[0] -Expected $Configuration.Source)){throw 'Cambió el origen antes de la mutación.'}
            $widget=@(& $Operations.VerifyWidget $source[0] $Configuration.SourcePackage)
            if($widget.Count -ne 1 -or $widget[0] -isnot [bool] -or -not $widget[0]){throw 'Cambió el widget antes de la mutación.'}
        }
        $items=@(& $Operations.Inventory $Configuration)
        if($items.Count){throw 'Quedan procesos o workers pertinentes; esperar su salida normal.'}
        $coverage=@(& $Operations.Coverage $Configuration $items)
        if($coverage.Count -ne 1 -or $coverage[0].AuthorizesMaintenance -ne $false -or (@(Compare-Object @($state.Sids) @($coverage[0].FenceSids)).Count -ne 0)){throw 'Cambió la cobertura de identidades.'}
        return $true
    }.GetNewClosure()
    $begin={param($ctx)
        $null=& $check $ctx;$sessions=@(& $Operations.OpenSession $Configuration $state.Helper)
        if($sessions.Count -ne 1 -or $sessions[0].AuthorizesMaintenance -ne $false){throw 'No se confirmó la sesión Y3.'}
        $state.Session=$sessions[0];return $true
    }.GetNewClosure()
    $revalidate={param($ctx)
        $null=& $check $ctx;$values=@(& $Operations.Revalidate $state.Session)
        if($values.Count -ne 1 -or $values[0] -isnot [bool] -or -not $values[0]){throw 'Revalidación Y3 fallida.'};return $true
    }.GetNewClosure()
    $sessionEvidence={param($ctx)
        $values=@(& $Operations.Revalidate $state.Session)
        if($values.Count -ne 1 -or $values[0] -isnot [bool] -or -not $values[0]){throw 'No se confirmó sesión para evidencia Y3.'}
        $s=$state.Session;$classification=[string]$s.Classification;$content=[string]$s.ContentSha256;$fileId=[string]$s.FileIdentitySha256;$protection=[string]$s.ProtectionSha256
        if($s.AuthorizesMaintenance -ne $false -or $classification -notin @('absent','restored_with_original') -or $protection -cnotmatch '^[A-F0-9]{64}$' -or
           ($classification -eq 'absent' -and ($content -ne '' -or $fileId -ne '')) -or ($classification -eq 'restored_with_original' -and ($content -cnotmatch '^[A-F0-9]{64}$' -or $fileId -cnotmatch '^[A-F0-9]{64}$'))){throw 'Snapshot Y3 de sesión inválido.'}
        return [pscustomobject]@{Classification=$classification;ContentSha256=$content;FileIdentitySha256=$fileId;ProtectionSha256=$protection;AuthorizesMaintenance=$false}
    }.GetNewClosure()
    $release={param($ctx)
        $values=@(& $Operations.CloseSession $state.Session)
        if($values.Count -ne 1 -or $values[0] -isnot [bool] -or -not $values[0]){throw 'No se confirmó liberación Y3.'};$state.Session=$null;return $true
    }.GetNewClosure()
    $unfence={param($ctx)
        $values=@(& $Operations.CloseFence $state.Fence)
        if($values.Count -ne 1 -or $values[0] -isnot [bool] -or -not $values[0]){throw 'No se confirmó liberación legada.'};$state.Fence=$null;return $true
    }.GetNewClosure()
    $preserve={param($ctx)
        $values=@(& $Operations.PreserveSession $state.Session)
        if($values.Count -ne 1 -or $values[0] -isnot [bool] -or -not $values[0]){throw 'No se confirmó cierre conservador de sesión.'};$state.Session=$null;return $true
    }.GetNewClosure()
    return @{ValidateRequest=$validate;StageAndVerify=$stage;InventoryAndFence=$fence;AwaitQuiescence=$quiet;BeginSessions=$begin;RevalidateAll=$revalidate;GetSessionEvidence=$sessionEvidence;ReleaseSessions=$release;PreserveSessions=$preserve;ReleaseFence=$unfence}
}
Export-ModuleMember -Function New-LtRuntimeAdapters
