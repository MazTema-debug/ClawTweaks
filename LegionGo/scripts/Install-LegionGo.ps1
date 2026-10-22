#Requires -RunAsAdministrator
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$displaySid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
$displayRecovery = Join-Path $env:ProgramData "LegionTweaks\$displaySid\display-change.json"
if (Test-Path -LiteralPath $displayRecovery) { throw 'Finish or recover the pending display trial in LegionTweaks before updating. No installation was started.' }
function Select-MissingAppxDependencies {
    param([string[]]$Paths)
    Add-Type -AssemblyName System.IO.Compression.FileSystem,System.IO.Compression
    foreach ($dependencyPath in $Paths) {
        $archive = [IO.Compression.ZipFile]::OpenRead($dependencyPath)
        try {
            $entry = $archive.GetEntry('AppxManifest.xml')
            if (-not $entry) { throw "Missing dependency manifest: $dependencyPath" }
            $reader = [IO.StreamReader]::new($entry.Open())
            try { [xml]$manifest = $reader.ReadToEnd() } finally { $reader.Dispose() }
        } finally { $archive.Dispose() }
        $identity = $manifest.Package.Identity
        $architecture = [string]$identity.ProcessorArchitecture
        if (-not $identity.Name -or -not $identity.Publisher -or -not $identity.Version -or
            $manifest.Package.Properties.Framework -ne 'true' -or $architecture -notin 'x64','neutral') {
            throw "Invalid x64 framework dependency: $dependencyPath"
        }
        $minimumVersion = [version]$identity.Version
        $healthy = @(Get-AppxPackage -Name ([string]$identity.Name) -PackageTypeFilter Framework | Where-Object {
            $_.IsFramework -and $_.Status -eq 'Ok' -and
            $_.Publisher -eq [string]$identity.Publisher -and
            [string]$_.Architecture -in $architecture,'Neutral' -and [version]$_.Version -ge $minimumVersion
        })
        # Re-deploying identical shared frameworks can force unrelated apps closed.
        # Reuse only healthy, matching frameworks registered for this user; a
        # fresh install still supplies every missing or older bundled dependency.
        if ($healthy.Count -eq 0) { $dependencyPath }
    }
}
$taskName = 'LegionTweaks-Z1E'
$installDir = Join-Path $env:ProgramFiles 'LegionTweaks-Z1E'
$packageName = 'MazTema.LegionTweaks'
$cpu = (Get-CimInstance Win32_Processor | Select-Object -First 1).Name
$product = Get-CimInstance Win32_ComputerSystemProduct
if ($product.Vendor.Trim() -ne 'LENOVO' -or ($product.Name -notlike '83E1*' -and $product.Version.Trim() -ne 'Legion Go 8APU1') -or $cpu -notmatch 'AMD.*Z1 Extreme') {
    throw "Unsupported hardware: $($product.Vendor) / $($product.Name) / $cpu. Only Legion Go 8APU1 Z1 Extreme is enabled."
}
$certificatePath = Join-Path $PSScriptRoot 'LegionTweaks.cer'
if (-not (Test-Path $certificatePath)) { throw 'This build is unsigned. Build it on Windows using Build-LegionGo.ps1 without -Unsigned.' }
$certificate = [Security.Cryptography.X509Certificates.X509Certificate2]::new($certificatePath)
if ($certificate.Subject -ne 'CN=LegionTweaks Dev') { throw 'Unexpected development certificate publisher.' }
$packages = @(Get-ChildItem "$PSScriptRoot\Widget" -Recurse -File | Where-Object { $_.Extension -in '.appx','.msix' -and $_.FullName -notmatch '\\Dependencies\\' })
if ($packages.Count -ne 1) { throw 'The build must contain exactly one widget package.' }
$helperSource = "$PSScriptRoot\Helper\LegionTweaks.Helper.exe"
foreach ($file in @($packages[0].FullName, $helperSource)) {
    $signature = Get-AuthenticodeSignature $file
    if ($signature.SignerCertificate.Thumbprint -ne $certificate.Thumbprint -or $signature.Status -eq 'HashMismatch') { throw "Invalid signature: $file" }
}
Write-Host "Trusting development certificate for this experimental build: $($certificate.Thumbprint)"
Import-Certificate -FilePath $certificatePath -CertStoreLocation 'Cert:\LocalMachine\TrustedPeople' | Out-Null
foreach ($file in @($packages[0].FullName, $helperSource)) {
    if ((Get-AuthenticodeSignature $file).Status -ne 'Valid') { throw "Signature validation failed: $file" }
}
$dependencies = @(Get-ChildItem "$PSScriptRoot\Widget" -Recurse -File -Filter *.appx | Where-Object { $_.FullName -match '\\Dependencies\\x64\\' } | Select-Object -ExpandProperty FullName)
$dependencies = @(Select-MissingAppxDependencies -Paths $dependencies)
if ($dependencies.Count) { Add-AppxPackage -ForceApplicationShutdown -ForceUpdateFromAnyVersion -Path $packages[0].FullName -DependencyPath $dependencies }
else { Add-AppxPackage -ForceApplicationShutdown -ForceUpdateFromAnyVersion -Path $packages[0].FullName }
$installed = Get-AppxPackage -Name $packageName | Sort-Object Version -Descending | Select-Object -First 1
if (-not $installed -or $installed.Publisher -ne 'CN=LegionTweaks Dev') { throw 'The port package was not installed for the current user.' }
$existingTask = Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue
if (Test-Path -LiteralPath $displayRecovery) { throw 'A display trial started during installation. Keep the current helper and recover the display before retrying.' }
if ($existingTask) { Stop-ScheduledTask -InputObject $existingTask }
Get-CimInstance Win32_Process -Filter "Name='LegionTweaks.Helper.exe'" | Where-Object {
    $_.ExecutablePath -eq "$installDir\LegionTweaks.Helper.exe" -and $_.CommandLine -notmatch '\s--display-(trial|recover)\s'
} | ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
if (Test-Path -LiteralPath $displayRecovery) { throw 'Display recovery is still pending. Its independent worker was retained; do not replace the helper yet.' }
$sid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
$lease = Join-Path $env:ProgramData "LegionTweaks\$sid\controller-lease.json"
$deviceRecovery = Join-Path $env:ProgramData "LegionTweaks\$sid\controller-device-recovery.json"
$modeRecovery = Join-Path $env:ProgramData "LegionTweaks\$sid\controller-mode-recovery.json"
if ((Test-Path $lease) -or (Test-Path $deviceRecovery) -or (Test-Path $modeRecovery)) {
    & "$installDir\LegionTweaks.Helper.exe" --recover-controller
    if ($LASTEXITCODE) { throw 'Physical controller recovery failed. The previous helper and backup have been retained. Retry recovery before updating.' }
}
New-Item -ItemType Directory -Path $installDir -Force | Out-Null
# Program Files location + explicit ACL prevents an unelevated process replacing the helper/config.
$acl = [Security.AccessControl.DirectorySecurity]::new()
$acl.SetAccessRuleProtection($true, $false)
foreach ($entry in @(@('S-1-5-18','FullControl'),@('S-1-5-32-544','FullControl'),@('S-1-5-32-545','ReadAndExecute'))) {
    $sid = [Security.Principal.SecurityIdentifier]::new($entry[0])
    $rule = [Security.AccessControl.FileSystemAccessRule]::new($sid,$entry[1],'ContainerInherit,ObjectInherit','None','Allow')
    $acl.AddAccessRule($rule)
}
Set-Acl -Path $installDir -AclObject $acl
Copy-Item "$PSScriptRoot\Helper\*" $installDir -Recurse -Force
Copy-Item "$PSScriptRoot\Uninstall-LegionGo.ps1" $installDir -Force
$installed.PackageFamilyName | Set-Content "$installDir\allowed-family.txt" -Encoding UTF8
$certificate.Thumbprint | Set-Content "$installDir\certificate-thumbprint.txt"
$user = [Security.Principal.WindowsIdentity]::GetCurrent().Name
$action = New-ScheduledTaskAction -Execute "$installDir\LegionTweaks.Helper.exe" -WorkingDirectory $installDir
$trigger = New-ScheduledTaskTrigger -AtLogOn -User $user
$principal = New-ScheduledTaskPrincipal -UserId $user -LogonType Interactive -RunLevel Highest
$settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -ExecutionTimeLimit ([TimeSpan]::Zero) -RestartCount 3 -RestartInterval (New-TimeSpan -Minutes 1) -MultipleInstances IgnoreNew
Register-ScheduledTask -TaskName $taskName -Action $action -Trigger $trigger -Principal $principal -Settings $settings -Force | Out-Null
# Updating binaries must not itself initiate a controller mode transition.
& "$installDir\LegionTweaks.Helper.exe" --suppress-controller-startup-once
if ($LASTEXITCODE) { throw 'Could not suppress controller startup during installation. The task has not been started.' }
Start-ScheduledTask -TaskName $taskName
Write-Host 'Installed. Open Win+G > Widgets > LegionTweaks Lab. The helper reads settings until you apply a change.'
Write-Host 'Virtual controller profiles require the official ViGEmBus and HidHide drivers. See CONTROLLERS.md. No driver is installed automatically.'
