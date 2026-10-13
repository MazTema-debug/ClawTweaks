#Requires -RunAsAdministrator
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
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
if ($dependencies.Count) { Add-AppxPackage -ForceApplicationShutdown -ForceUpdateFromAnyVersion -Path $packages[0].FullName -DependencyPath $dependencies }
else { Add-AppxPackage -ForceApplicationShutdown -ForceUpdateFromAnyVersion -Path $packages[0].FullName }
$installed = Get-AppxPackage -Name $packageName | Sort-Object Version -Descending | Select-Object -First 1
if (-not $installed -or $installed.Publisher -ne 'CN=LegionTweaks Dev') { throw 'The port package was not installed for the current user.' }
$existingTask = Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue
if ($existingTask) { Stop-ScheduledTask -InputObject $existingTask }
Get-Process -Name 'LegionTweaks.Helper' -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq "$installDir\LegionTweaks.Helper.exe" } | Stop-Process -Force
$sid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
$lease = Join-Path $env:ProgramData "LegionTweaks\$sid\controller-lease.json"
if (Test-Path $lease) {
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
Start-ScheduledTask -TaskName $taskName
Write-Host 'Installed. Open Win+G > Widgets > LegionTweaks Lab. The helper reads settings until you apply a change.'
Write-Host 'Virtual controller profiles require the official ViGEmBus and HidHide drivers. See CONTROLLERS.md. No driver is installed automatically.'
