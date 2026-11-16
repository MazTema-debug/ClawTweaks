[CmdletBinding()]
param([string]$OutputDirectory = (Join-Path $PSScriptRoot '..\..\artifacts\LegionTweaks-Z1E'),
      [string]$ValidationDirectory = (Join-Path $PSScriptRoot '..\..\artifacts\validation'),
      [Parameter(Mandatory=$true)][string]$Return49Archive,
      [Parameter(Mandatory=$true)][string]$Return49Extracted, [switch]$Unsigned)
$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSEdition -ne 'Desktop' -or $PSVersionTable.PSVersion.Major -ne 5 -or -not [Environment]::Is64BitProcess) { throw 'Build requires Windows PowerShell 5.1 x64.' }
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
$ValidationDirectory = [IO.Path]::GetFullPath($ValidationDirectory)
if ((Test-Path -LiteralPath $OutputDirectory) -and @(Get-ChildItem -LiteralPath $OutputDirectory -Force).Count) { throw 'Use an empty package output directory; preserve previous candidates.' }
if ($ValidationDirectory.StartsWith($OutputDirectory.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase) -or $ValidationDirectory -eq $OutputDirectory) { throw 'Validation outputs must remain outside the shipped package.' }
New-Item -ItemType Directory -Force $OutputDirectory | Out-Null
New-Item -ItemType Directory -Force $ValidationDirectory | Out-Null
$inventory = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'Build-Inventory.json') -Raw | ConvertFrom-Json
$modules = @(Get-ChildItem -LiteralPath $PSScriptRoot -File -Filter 'Maintenance*.psm1' | Select-Object -ExpandProperty Name)
if (@(Compare-Object @($inventory.MaintenanceModules) $modules).Count) { throw 'Maintenance source inventory differs.' }
foreach ($fixture in $inventory.RequiredTestFixtures) { if (-not (Test-Path -LiteralPath (Join-Path "$root\Tests" $fixture) -PathType Leaf)) { throw "Required test fixture missing: $fixture" } }
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
if (-not (Test-Path $vswhere)) { throw 'Install Visual Studio 2022 with Universal Windows Platform development and Windows SDK 10.0.26100.0.' }
$msbuild = & $vswhere -latest -products '*' -find 'MSBuild\**\Bin\MSBuild.exe' | Select-Object -First 1
if (-not $msbuild) { throw 'MSBuild was not found.' }
& dotnet run --project "$root\Tests\LegionTweaks.Tests.csproj" --configuration Release
if ($LASTEXITCODE) { throw 'Core tests failed.' }
& dotnet build "$root\Tests\MaintenanceHarness\LegionTweaks.MaintenanceHarness.csproj" -c Release -o "$ValidationDirectory\MandoHarness"
if ($LASTEXITCODE) { throw 'Mando integration fixture build failed.' }
$harness = "$ValidationDirectory\MandoHarness\LegionTweaks.MaintenanceHarness.exe"
$harnessHash = (Get-FileHash -LiteralPath $harness -Algorithm SHA256).Hash
& $harness
if ($LASTEXITCODE) { throw 'Mando integration fixture tests failed.' }
& "$root\Tests\Run-PowerShellTests.ps1" -TargetArchive $Return49Archive -TargetExtracted $Return49Extracted -MandoHarness $harness -MandoHarnessSha256 $harnessHash
if (-not $?) { throw 'PowerShell test inventory failed.' }
@{ MandoHarnessSha256=$harnessHash; Return49Sha256=(Get-FileHash -LiteralPath $Return49Archive -Algorithm SHA256).Hash; FixtureOnly=$true } | ConvertTo-Json | Set-Content -LiteralPath "$ValidationDirectory\fixture-pins.json" -Encoding UTF8
& dotnet build "$root\Helper\LegionTweaks.Helper.csproj" -c Release -o "$OutputDirectory\Helper"
if ($LASTEXITCODE) { throw 'Helper build failed.' }
$readonlyIntermediate = Join-Path $root 'Helper\obj\readonly'
& dotnet build "$root\Helper\LegionTweaks.Helper.csproj" -c Release -p:QamReadOnlyHost=true "-p:MSBuildProjectExtensionsPath=$readonlyIntermediate\" "-p:IntermediateOutputPath=$readonlyIntermediate\Release\net48\" -o "$ValidationDirectory\ReadonlyHost"
if ($LASTEXITCODE) { throw 'SDK readonly helper build failed.' }
& powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File "$root\Tests\Test-CompiledHostGuard.ps1" -ReadOnlyHost "$ValidationDirectory\ReadonlyHost\LegionTweaks.QamReadOnly.exe"
if ($LASTEXITCODE) { throw 'SDK readonly helper guard checks failed.' }
& dotnet build "$root\Tools\QamProbe\LegionTweaks.QamProbe.csproj" -c Release -o "$OutputDirectory\Helper\Qam"
if ($LASTEXITCODE) { throw 'QAM build failed.' }
# Pinned official SDL Windows x64 runtime. Native controller input stays outside the UWP package.
$sdlZip = Join-Path $env:TEMP ('LegionTweaks-SDL-' + [Guid]::NewGuid().ToString('N') + '.zip')
$sdlDir = $sdlZip + '.dir'
try {
    Invoke-WebRequest -UseBasicParsing -Uri 'https://github.com/libsdl-org/SDL/releases/download/release-2.32.10/SDL2-2.32.10-win32-x64.zip' -OutFile $sdlZip
    if ((Get-FileHash $sdlZip -Algorithm SHA256).Hash -ne '6CF9706EEFD0A4A06DC764007934D428AFAF029FABDD408A9E646048C91E18FB') { throw 'SDL runtime checksum mismatch.' }
    Expand-Archive $sdlZip $sdlDir
    $sdl = @(Get-ChildItem $sdlDir -Recurse -File -Filter SDL2.dll)
    if ($sdl.Count -ne 1) { throw 'Expected one SDL2 x64 runtime.' }
    Copy-Item $sdl[0].FullName "$OutputDirectory\Helper\SDL2.dll"
} finally {
    Remove-Item $sdlZip -Force -ErrorAction SilentlyContinue
    if (-not ([IO.Path]::GetFullPath($sdlDir)).StartsWith(([IO.Path]::GetFullPath($env:TEMP)).TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Unexpected SDL temporary directory.' }
    Remove-Item $sdlDir -Recurse -Force -ErrorAction SilentlyContinue
}
$widgetOutput = "$OutputDirectory\Widget\"
& $msbuild "$root\Widget\LegionTweaks.Widget.csproj" /restore /t:Build /p:Configuration=Release /p:Platform=x64 /p:GenerateAppxPackageOnBuild=true /p:AppxPackageSigningEnabled=false /p:AppxBundle=Never /p:UapAppxPackageBuildMode=SideloadOnly "/p:AppxPackageDir=$widgetOutput" /verbosity:minimal /nologo
if ($LASTEXITCODE) { throw 'Game Bar widget build failed.' }
$packages = @(Get-ChildItem "$OutputDirectory\Widget" -Recurse -File | Where-Object { $_.Extension -in '.appx','.msix' -and $_.FullName -notmatch '\\Dependencies\\' })
if ($packages.Count -ne 1) { throw "Expected one widget package; found $($packages.Count)." }
if (-not $Unsigned) {
    $signTool = Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\bin\*\x64\signtool.exe" | Sort-Object FullName -Descending | Select-Object -First 1
    if (-not $signTool) { throw 'Windows SDK SignTool was not found.' }
    $cert = New-SelfSignedCertificate -Type Custom -Subject 'CN=LegionTweaks Dev' -KeyUsage DigitalSignature -FriendlyName 'LegionTweaks experimental build' -CertStoreLocation 'Cert:\CurrentUser\My' -TextExtension @('2.5.29.37={text}1.3.6.1.5.5.7.3.3','2.5.29.19={text}') -NotAfter (Get-Date).AddYears(1)
    try {
        foreach ($file in @($packages[0].FullName, "$OutputDirectory\Helper\LegionTweaks.Helper.exe", "$OutputDirectory\Helper\Qam\LegionTweaks.QamProbe.exe")) {
            & $signTool.FullName sign /fd SHA256 /sha1 $cert.Thumbprint $file
            if ($LASTEXITCODE) { throw "Signing failed: $file" }
        }
        Export-Certificate -Cert $cert -FilePath "$OutputDirectory\LegionTweaks.cer" | Out-Null
    } finally { Remove-Item "Cert:\CurrentUser\My\$($cert.Thumbprint)" }
}
Copy-Item "$root\scripts\Install-LegionGo.ps1","$root\scripts\Uninstall-LegionGo.ps1","$root\README.md","$root\SOURCES.md","$root\VALIDATION.md","$root\LICENSE.GoTweaks","$root\LICENSE.LibreHardwareMonitor" $OutputDirectory
Copy-Item "$root\..\LICENSE" "$OutputDirectory\LICENSE"
Copy-Item "$root\LICENSE.SDL2","$root\LICENSE.ViGEm.NET","$root\LICENSE.HidHide.NET","$root\CONTROLLERS.md","$root\REMAP.md","$root\AMD-GRAPHICS.md" $OutputDirectory
$maintenanceFiles = @('LegionTweaks-Maintenance-v1.ps1','Maintenance-v1.md','Build-Inventory.json','Test-LegionGoPackage.ps1') + @($inventory.MaintenanceModules)
foreach ($name in $maintenanceFiles) { Copy-Item -LiteralPath (Join-Path $PSScriptRoot $name) -Destination (Join-Path $OutputDirectory $name) }
$files = Get-ChildItem $OutputDirectory -Recurse -File | Where-Object { $_.Name -ne 'SHA256SUMS.txt' }
$files | ForEach-Object { '{0}  {1}' -f (Get-FileHash $_.FullName -Algorithm SHA256).Hash, $_.FullName.Substring($OutputDirectory.Length + 1) } | Set-Content "$OutputDirectory\SHA256SUMS.txt"
& "$PSScriptRoot\Test-LegionGoPackage.ps1" -PackageRoot $OutputDirectory -SourceRoot $root -Unsigned:$Unsigned | Tee-Object -FilePath "$ValidationDirectory\package-verification.json"
Write-Host "Build ready: $OutputDirectory"
