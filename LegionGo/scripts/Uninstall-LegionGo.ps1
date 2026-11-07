#Requires -RunAsAdministrator
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$taskName = 'LegionTweaks-Z1E'
$installDir = Join-Path $env:ProgramFiles 'LegionTweaks-Z1E'
$runningQam = @(Get-CimInstance Win32_Process -Filter "Name='LegionTweaks.QamProbe.exe'" | Where-Object {
    $_.ExecutablePath -eq "$installDir\Qam\LegionTweaks.QamProbe.exe"
})
if ($runningQam.Count) { throw 'Close the installed QAM from Inicio > Terminar prueba before uninstalling. No uninstallation was started.' }
$displaySid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
$displayRecovery = Join-Path $env:ProgramData "LegionTweaks\$displaySid\display-change.json"
$fpsY3Recovery = Join-Path $env:ProgramData "LegionTweaks\$displaySid\fps-y3-trial.json"
if (Test-Path -LiteralPath $displayRecovery) { throw 'Finish or recover the pending display trial before uninstalling. The helper and backup were retained.' }
if (Test-Path -LiteralPath $fpsY3Recovery) { throw 'Stop and recover the pending FPS Y3 trial before uninstalling. The helper and backup were retained.' }
$task = Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue
if ($task) { Stop-ScheduledTask -InputObject $task }
Get-CimInstance Win32_Process -Filter "Name='LegionTweaks.Helper.exe'" | Where-Object {
    $_.ExecutablePath -eq "$installDir\LegionTweaks.Helper.exe" -and $_.CommandLine -notmatch '\s--(?:display-(?:trial|recover)|fps-y3-(?:watch|io))\s'
} | ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
if (Test-Path -LiteralPath $displayRecovery) { throw 'Display recovery is still pending. Its independent worker and backup were retained.' }
if (Test-Path -LiteralPath $fpsY3Recovery) { throw 'FPS Y3 recovery is still pending. Its independent workers and backup were retained.' }
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
