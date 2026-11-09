# Only the uninstall QAM preflight is evaluated with synthetic process inventory.
$ErrorActionPreference = 'Stop'
$uninstallPath = Join-Path $PSScriptRoot '..\scripts\Uninstall-LegionGo.ps1'
$tokens = $null; $errors = $null
$ast = [Management.Automation.Language.Parser]::ParseFile((Resolve-Path $uninstallPath).Path, [ref]$tokens, [ref]$errors)
if ($errors.Count) { throw 'Uninstall script did not parse.' }
$assignment = @($ast.FindAll({param($n) $n -is [Management.Automation.Language.AssignmentStatementAst] -and $n.Left.Extent.Text -eq '$runningQam'}, $false))
$guard = @($ast.FindAll({param($n) $n -is [Management.Automation.Language.IfStatementAst] -and $n.Clauses[0].Item1.Extent.Text -eq '$runningQam.Count'}, $false))
if ($assignment.Count -ne 1 -or $guard.Count -ne 1) { throw 'Missing bounded uninstall QAM preflight.' }
$firstMutation = @($ast.FindAll({param($n) $n -is [Management.Automation.Language.CommandAst] -and $n.GetCommandName() -in 'Stop-ScheduledTask','Stop-Process','Remove-Item','Remove-AppxPackage'}, $false) | Sort-Object {$_.Extent.StartOffset})[0]
if ($guard[0].Extent.EndOffset -ge $firstMutation.Extent.StartOffset) { throw 'QAM preflight follows uninstall mutation.' }
$installDir = 'X:\Synthetic\LegionTweaks-Z1E'
function Get-CimInstance { param($ClassName, $Filter)
    if ($ClassName -ne 'Win32_Process' -or $Filter -ne "Name='LegionTweaks.QamProbe.exe'") {throw 'Unexpected inventory query.'}
    $script:QamUninstallProcesses
}
foreach ($case in @(@{Paths=@();Block=$false}, @{Paths=@("$installDir\Qam\LegionTweaks.QamProbe.exe");Block=$true}, @{Paths=@('X:\Other\LegionTweaks.QamProbe.exe');Block=$false})) {
    $script:QamUninstallProcesses = @($case.Paths | ForEach-Object {[pscustomobject]@{ExecutablePath=$_}})
    Invoke-Expression $assignment[0].Extent.Text
    $blocked = $false
    try {Invoke-Expression $guard[0].Extent.Text} catch {if ($_.Exception.Message -notmatch 'Close the installed QAM') {throw}; $blocked=$true}
    if ($blocked -ne $case.Block) {throw 'Uninstall QAM ownership filter failed.'}
}
Write-Host 'PASS uninstall QAM preflight: 3 synthetic inventories; no task, process or file mutations.'
$y3Guards = @($ast.FindAll({param($n)
    $n -is [Management.Automation.Language.IfStatementAst] -and
    $n.Clauses[0].Item1.Extent.Text -eq 'Test-Path -LiteralPath $fpsY3Recovery'
}, $false))
if ($y3Guards.Count -ne 2 -or $y3Guards[0].Extent.EndOffset -ge $firstMutation.Extent.StartOffset) { throw 'Missing Y3 entry/replacement guards.' }
$fpsY3Recovery = 'X:\Synthetic\fps-y3-trial.json'
function Test-Path { param($LiteralPath)
    if ($LiteralPath -ne $fpsY3Recovery) { throw 'Unexpected recovery path.' }
    $script:UninstallY3Journal
}
$y3Cases = 0
foreach ($y3Guard in $y3Guards) {
    foreach ($present in @($false, $true)) {
        $script:UninstallY3Journal = $present; $blocked = $false
        try { Invoke-Expression $y3Guard.Extent.Text }
        catch { if ($_.Exception.Message -notmatch 'FPS Y3') { throw }; $blocked = $true }
        if ($blocked -ne $present) { throw 'Uninstall did not preserve pending Y3 recovery.' }
        $y3Cases++
    }
}
$stopFilters = @($ast.FindAll({param($n)
    $n -is [Management.Automation.Language.ScriptBlockExpressionAst] -and
    $n.Extent.Text -match '\$_\.CommandLine -notmatch' -and $n.Extent.Text -match 'fps-y3-'
}, $false))
if ($stopFilters.Count -ne 1) { throw 'Missing bounded helper stop filter.' }
$stopFilter = Invoke-Expression $stopFilters[0].Extent.Text
foreach ($case in @(
    @{Path="$installDir\LegionTweaks.Helper.exe";Args='helper.exe';Stop=$true},
    @{Path="$installDir\LegionTweaks.Helper.exe";Args='helper.exe --display-trial token';Stop=$false},
    @{Path="$installDir\LegionTweaks.Helper.exe";Args='helper.exe --display-recover token';Stop=$false},
    @{Path="$installDir\LegionTweaks.Helper.exe";Args='helper.exe --fps-y3-watch recover token';Stop=$false},
    @{Path="$installDir\LegionTweaks.Helper.exe";Args='helper.exe --fps-y3-io restore token';Stop=$false},
    @{Path="$installDir\LegionTweaks.Helper.exe";Args='helper.exe --fps-y3-io verify-ready token';Stop=$false},
    @{Path='X:\Other\LegionTweaks.Helper.exe';Args='helper.exe';Stop=$false}
)) {
    $process = [pscustomobject]@{ExecutablePath=$case.Path;CommandLine=$case.Args}
    if ((@($process | Where-Object $stopFilter).Count -eq 1) -ne $case.Stop) { throw 'Uninstall selected recovery workers or an unrelated process.' }
    $y3Cases++
}
Write-Host "PASS uninstall Y3 recovery safety ($y3Cases cases). Synthetic paths/inventory only; no task, process or file mutations."
