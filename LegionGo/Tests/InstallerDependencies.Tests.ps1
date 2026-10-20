# SPDX-License-Identifier: AGPL-3.0-only
# Test only the dependency selector; do not execute installer or Appx deployment.
$ErrorActionPreference = 'Stop'
$installer = Join-Path $PSScriptRoot '..\scripts\Install-LegionGo.ps1'
$tokens = $null; $parseErrors = $null
$ast = [Management.Automation.Language.Parser]::ParseFile($installer, [ref]$tokens, [ref]$parseErrors)
if ($parseErrors.Count) { throw 'Installer syntax error.' }
$selector = @($ast.FindAll({ param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Select-MissingAppxDependencies' }, $false))
if ($selector.Count -ne 1) { throw 'Expected exactly one dependency selector.' }
Invoke-Expression $selector[0].Extent.Text
function Get-AppxPackage {
    param($Name, $PackageTypeFilter)
    if ($Name -ne 'Microsoft.Test.Framework' -or $PackageTypeFilter -ne 'Framework') { throw 'Selector queried unrelated packages.' }
    $script:Registered
}
$folder = Join-Path ([IO.Path]::GetTempPath()) ('LegionTweaks-dependencies-' + [guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($folder) | Out-Null
try {
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $package = Join-Path $folder 'Framework.appx'
    $zip = [IO.Compression.ZipFile]::Open($package, [IO.Compression.ZipArchiveMode]::Create)
    try {
        $entry = $zip.CreateEntry('AppxManifest.xml')
        $writer = [IO.StreamWriter]::new($entry.Open())
        try { $writer.Write('<Package><Identity Name="Microsoft.Test.Framework" Publisher="CN=Microsoft Test" Version="2.2.3.0" ProcessorArchitecture="x64"/><Properties><Framework>true</Framework></Properties></Package>') }
        finally { $writer.Dispose() }
    } finally { $zip.Dispose() }
    $script:Registered = @()
    if (@(Select-MissingAppxDependencies @($package)).Count -ne 1) { throw 'Fresh install lost its dependency.' }
    foreach ($version in '2.2.3.0','2.2.4.0') {
        $script:Registered = @([pscustomobject]@{ IsFramework=$true; Status='Ok'; Publisher='CN=Microsoft Test'; Architecture='X64'; Version=$version })
        if (@(Select-MissingAppxDependencies @($package)).Count -ne 0) { throw 'Healthy matching framework was redeployed.' }
    }
    foreach ($change in @(@{ Version='2.2.2.0' },@{ Publisher='CN=Other' },@{ Architecture='X86' },@{ Status='Modified' },@{ IsFramework=$false })) {
        $candidate = @{ IsFramework=$true; Status='Ok'; Publisher='CN=Microsoft Test'; Architecture='X64'; Version='2.2.3.0' }
        foreach ($key in $change.Keys) { $candidate[$key] = $change[$key] }
        $script:Registered = @([pscustomobject]$candidate)
        if (@(Select-MissingAppxDependencies @($package)).Count -ne 1) { throw "Invalid installed dependency accepted: $($change.Keys)" }
    }
    $script:Registered = @([pscustomobject]@{ IsFramework=$true; Status='Ok'; Publisher='CN=Microsoft Test'; Architecture='Neutral'; Version='2.2.3.0' })
    if (@(Select-MissingAppxDependencies @($package)).Count -ne 0) { throw 'Neutral compatible framework was redeployed.' }
    if (@(Select-MissingAppxDependencies @()).Count -ne 0) { throw 'Empty dependency input fabricated a package.' }
    Write-Host 'PASS installer framework dependency selection (10 cases). No package was installed.'
} finally {
    # Only files created in this exact per-test directory are removed.
    [IO.File]::Delete((Join-Path $folder 'Framework.appx'))
    [IO.Directory]::Delete($folder)
}
