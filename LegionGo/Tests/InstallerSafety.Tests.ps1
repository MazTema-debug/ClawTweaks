# SPDX-License-Identifier: AGPL-3.0-only
# Execute only extracted guards/filters with synthetic paths and mocked inventory.
# Never execute the installer, deployment, process termination, or recovery workers.
$ErrorActionPreference = 'Stop'
$installer = Join-Path $PSScriptRoot '..\scripts\Install-LegionGo.ps1'
$tokens = $null; $parseErrors = $null
$ast = [Management.Automation.Language.Parser]::ParseFile($installer, [ref]$tokens, [ref]$parseErrors)
if ($parseErrors.Count) { throw 'Installer syntax error.' }
$guards = @($ast.FindAll({ param($node)
    $node -is [Management.Automation.Language.IfStatementAst] -and
    $node.Clauses[0].Item1.Extent.Text -eq 'Test-Path -LiteralPath $fpsY3Recovery'
}, $false))
if ($guards.Count -ne 3) { throw 'Expected Y3 guards at entry, before stopping, and before replacing the helper.' }
$fpsY3Recovery = 'X:\Synthetic\fps-y3-trial.json'
function Test-Path {
    param($LiteralPath)
    if ($LiteralPath -ne $fpsY3Recovery) { throw 'Guard queried an unrelated path.' }
    $script:JournalExists
}
$cases = 0
foreach ($guard in $guards) {
    foreach ($present in @($false, $true)) {
        $script:JournalExists = $present; $blocked = $false
        try { Invoke-Expression $guard.Extent.Text }
        catch { if ($_.Exception.Message -notmatch 'FPS Y3') { throw }; $blocked = $true }
        if ($blocked -ne $present) { throw 'Y3 guard did not fail closed for a pending trial.' }
        $cases++
    }
}
$filters = @($ast.FindAll({ param($node)
    $node -is [Management.Automation.Language.ScriptBlockExpressionAst] -and
    $node.Extent.Text -match '\$_\.CommandLine -notmatch' -and $node.Extent.Text -match 'fps-y3-'
}, $false))
if ($filters.Count -ne 1) { throw 'Expected one helper stop filter preserving recovery workers.' }
$filter = Invoke-Expression $filters[0].Extent.Text
$installDir = 'X:\Synthetic\LegionTweaks-Z1E'
$helperPath = "$installDir\LegionTweaks.Helper.exe"
foreach ($case in @(
    @{ Path=$helperPath; Args='helper.exe'; Stop=$true },
    @{ Path=$helperPath; Args='helper.exe --display-trial token'; Stop=$false },
    @{ Path=$helperPath; Args='helper.exe --display-recover token'; Stop=$false },
    @{ Path=$helperPath; Args='helper.exe --fps-y3-watch recover token'; Stop=$false },
    @{ Path=$helperPath; Args='helper.exe --fps-y3-io restore token'; Stop=$false },
    @{ Path='X:\Other\LegionTweaks.Helper.exe'; Args='helper.exe'; Stop=$false }
)) {
    $process = [pscustomobject]@{ ExecutablePath=$case.Path; CommandLine=$case.Args }
    if ((@($process | Where-Object $filter).Count -eq 1) -ne $case.Stop) { throw 'Helper stop filter selected a recovery worker or an unrelated process.' }
    $cases++
}
$qamAssignments = @($ast.FindAll({ param($node)
    $node -is [Management.Automation.Language.AssignmentStatementAst] -and $node.Left.Extent.Text -eq '$runningQam'
}, $false))
$qamGuards = @($ast.FindAll({ param($node)
    $node -is [Management.Automation.Language.IfStatementAst] -and $node.Clauses[0].Item1.Extent.Text -eq '$runningQam.Count'
}, $false))
if ($qamAssignments.Count -ne 1 -or $qamGuards.Count -ne 1) { throw 'Expected one installed-QAM preflight.' }
function Get-CimInstance {
    param($ClassName, $Filter)
    if ($ClassName -ne 'Win32_Process' -or $Filter -ne "Name='LegionTweaks.QamProbe.exe'") { throw 'Unexpected process inventory query.' }
    $script:Processes
}
foreach ($case in @(
    @{ Paths=@(); Block=$false },
    @{ Paths=@("$installDir\Qam\LegionTweaks.QamProbe.exe"); Block=$true },
    @{ Paths=@('X:\Other\LegionTweaks.QamProbe.exe'); Block=$false }
)) {
    $script:Processes = @($case.Paths | ForEach-Object { [pscustomobject]@{ ExecutablePath=$_ } })
    Invoke-Expression $qamAssignments[0].Extent.Text
    $blocked = $false
    try { Invoke-Expression $qamGuards[0].Extent.Text }
    catch { if ($_.Exception.Message -notmatch 'Close the installed QAM') { throw }; $blocked = $true }
    if ($blocked -ne $case.Block) { throw 'Installed-QAM preflight selected an unrelated process or allowed a locked component.' }
    $cases++
}
Write-Host "PASS installer Y3/QAM safety ($cases cases). Synthetic paths/inventory only; no installation or process operation."
