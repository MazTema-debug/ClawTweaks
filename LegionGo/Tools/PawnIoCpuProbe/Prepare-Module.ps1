[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
$cpuModuleOutput = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $cpuModuleOutput -Force | Out-Null
if ((Get-Item -LiteralPath $cpuModuleOutput).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Output must not be a reparse point.' }
$cpuArchivePath = Join-Path $cpuModuleOutput 'release_0_2_11.zip'
if (!(Test-Path -LiteralPath $cpuArchivePath)) {
    # Explicit preparation only; the probe never downloads dependencies.
    Invoke-WebRequest -UseBasicParsing -Uri 'https://github.com/namazso/PawnIO.Modules/releases/download/0.2.11/release_0_2_11.zip' -OutFile $cpuArchivePath
}
if ((Get-Item -LiteralPath $cpuArchivePath).Length -ne 69582 -or
    (Get-FileHash -LiteralPath $cpuArchivePath -Algorithm SHA256).Hash -ne '43608CB89BC84247FEF1368A139013F7D043E17DB6D6C8DFC9B46BF0905A81F4') { throw 'Official release archive mismatch.' }
Add-Type -AssemblyName System.IO.Compression.FileSystem
$cpuArchive = [IO.Compression.ZipFile]::OpenRead($cpuArchivePath)
try {
    foreach ($cpuEntryName in @('AMDFamily17.bin','COPYING')) {
        $cpuEntry = @($cpuArchive.Entries | Where-Object { $_.FullName -eq $cpuEntryName })
        if ($cpuEntry.Count -ne 1) { throw "Unexpected archive entry: $cpuEntryName" }
        $cpuEntryOutput = Join-Path $cpuModuleOutput $cpuEntryName
        if (!(Test-Path -LiteralPath $cpuEntryOutput)) { [IO.Compression.ZipFileExtensions]::ExtractToFile($cpuEntry[0], $cpuEntryOutput) }
    }
} finally { $cpuArchive.Dispose() }
$cpuModulePath = Join-Path $cpuModuleOutput 'AMDFamily17.bin'
if ((Get-Item -LiteralPath $cpuModulePath).Length -ne 10652 -or
    (Get-FileHash -LiteralPath $cpuModulePath -Algorithm SHA256).Hash -ne 'DAE74615761B78BDF064DFB3E136252DDCC6FC727D88F14738D0E5800D427A91') { throw 'Official AMD module mismatch.' }
Write-Output "Prepared module 0.2.11: $cpuModulePath. Driver not installed; module not loaded. Source: https://github.com/namazso/PawnIO.Modules/blob/52a7e536dff3e53c96917a28caac5e0fa6510696/AMDFamily17.p"
