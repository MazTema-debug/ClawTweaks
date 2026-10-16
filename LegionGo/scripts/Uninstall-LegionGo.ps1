#Requires -RunAsAdministrator
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$taskName = 'LegionTweaks-Z1E'
$installDir = Join-Path $env:ProgramFiles 'LegionTweaks-Z1E'
$task = Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue
if ($task) { Stop-ScheduledTask -InputObject $task }
Get-Process -Name 'LegionTweaks.Helper' -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq "$installDir\LegionTweaks.Helper.exe" } | Stop-Process -Force
$helper = "$installDir\LegionTweaks.Helper.exe"
if (Test-Path $helper) {
    & $helper --restore
    if ($LASTEXITCODE) { throw 'Restoration failed. The helper, backup and task have been retained for retry. Review the error before uninstalling.' }
}
if ($task) { Unregister-ScheduledTask -TaskName $taskName -Confirm:$false }
Get-AppxPackage -Name 'MazTema.LegionTweaks' | Remove-AppxPackage
$thumbPath = "$installDir\certificate-thumbprint.txt"
if (Test-Path $thumbPath) {
    $thumb = (Get-Content $thumbPath -Raw).Trim()
    if ($thumb -match '^[A-Fa-f0-9]{40}$') {
        $certPath = "Cert:\LocalMachine\TrustedPeople\$thumb"
        $cert = Get-Item $certPath -ErrorAction SilentlyContinue
        if ($cert -and $cert.Subject -eq 'CN=LegionTweaks Dev') { Remove-Item $certPath }
    }
}
if (Test-Path $installDir) { Remove-Item $installDir -Recurse -Force }
Write-Host 'LegionTweaks removed. Saved device settings restored. Legion Space keeps its selected state; its separate backup remains in ProgramData. Other applications were not uninstalled.'
