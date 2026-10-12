[CmdletBinding()]
param([string]$OutputDirectory = (Join-Path $PSScriptRoot '..\..\artifacts\LegionTweaks-Z1E'), [switch]$Unsigned)
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Force $OutputDirectory | Out-Null
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
if (-not (Test-Path $vswhere)) { throw 'Install Visual Studio 2022 with Universal Windows Platform development and Windows SDK 10.0.26100.0.' }
$msbuild = & $vswhere -latest -products '*' -find 'MSBuild\**\Bin\MSBuild.exe' | Select-Object -First 1
if (-not $msbuild) { throw 'MSBuild was not found.' }
& dotnet run --project "$root\Tests\LegionTweaks.Tests.csproj" --configuration Release
if ($LASTEXITCODE) { throw 'Core tests failed.' }
& dotnet build "$root\Helper\LegionTweaks.Helper.csproj" -c Release -o "$OutputDirectory\Helper"
if ($LASTEXITCODE) { throw 'Helper build failed.' }
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
        foreach ($file in @($packages[0].FullName, "$OutputDirectory\Helper\LegionTweaks.Helper.exe")) {
            & $signTool.FullName sign /fd SHA256 /sha1 $cert.Thumbprint $file
            if ($LASTEXITCODE) { throw "Signing failed: $file" }
        }
        Export-Certificate -Cert $cert -FilePath "$OutputDirectory\LegionTweaks.cer" | Out-Null
    } finally { Remove-Item "Cert:\CurrentUser\My\$($cert.Thumbprint)" }
}
Copy-Item "$root\scripts\Install-LegionGo.ps1","$root\scripts\Uninstall-LegionGo.ps1","$root\README.md","$root\SOURCES.md","$root\LICENSE.GoTweaks","$root\LICENSE.LibreHardwareMonitor" $OutputDirectory
Copy-Item "$root\..\LICENSE" "$OutputDirectory\LICENSE"
$files = Get-ChildItem $OutputDirectory -Recurse -File | Where-Object { $_.Name -ne 'SHA256SUMS.txt' }
$files | ForEach-Object { '{0}  {1}' -f (Get-FileHash $_.FullName -Algorithm SHA256).Hash, $_.FullName.Substring($OutputDirectory.Length + 1) } | Set-Content "$OutputDirectory\SHA256SUMS.txt"
Write-Host "Build ready: $OutputDirectory"
