$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Windows.Forms
try {
    $qamExecutable = Join-Path $PSScriptRoot 'LegionTweaks.QamProbe.exe'
    $qamReaderExecutable = Join-Path $PSScriptRoot 'Helper/LegionTweaks.QamReadOnly.exe'
    if (-not (Test-Path -LiteralPath $qamExecutable) -or -not (Test-Path -LiteralPath $qamReaderExecutable)) {
        throw 'Faltan archivos de la prueba. Conserva el QAM, su configuración y la carpeta Helper juntos.'
    }
    if (Get-Process -Name 'LegionTweaks.QamProbe' -ErrorAction SilentlyContinue) {
        throw 'Termina el QAM anterior desde Inicio → Terminar prueba y vuelve a abrir esta prueba.'
    }
    $qamProcess = Start-Process -FilePath $qamExecutable -PassThru
    if ($qamProcess.WaitForExit(750)) { throw 'El QAM terminó antes de iniciar las lecturas.' }
    # Only the fixed diagnostic reader is elevated, with the lifetime of this exact QAM process.
    $qamReaderArguments = @('--qam-readonly', $qamProcess.Id.ToString(), $qamProcess.StartTime.ToUniversalTime().Ticks.ToString())
    $qamReader = Start-Process -FilePath $qamReaderExecutable -WorkingDirectory (Split-Path -Parent $qamReaderExecutable) -ArgumentList $qamReaderArguments -Verb RunAs -WindowStyle Hidden -PassThru
    while (-not $qamProcess.WaitForExit(1000)) {
        if ($qamReader.HasExited) {
            throw 'El lector temporal terminó. Las consultas pueden quedar indisponibles; termina la prueba y vuelve a abrirla desde el escritorio.'
        }
    }
    $qamReader.WaitForExit(5000) | Out-Null
}
catch {
    [System.Windows.Forms.MessageBox]::Show($_.Exception.Message, 'Prueba QAM · solo lectura', 'OK', 'Information') | Out-Null
}
