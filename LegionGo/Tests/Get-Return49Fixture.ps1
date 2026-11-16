# SPDX-License-Identifier: AGPL-3.0-only
[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$Archive,
      [Parameter(Mandatory=$true)][string]$Extracted, [switch]$Download)
$ErrorActionPreference='Stop'
$expected='5225CA985580E24DB634B022E6C83805CDA5B2514846B6AFB523885BE698B39A'
if(-not(Test-Path -LiteralPath $Archive)){
    if(-not $Download -or -not $env:GITHUB_TOKEN){throw 'The exact .49 archive is required; automatic download needs GITHUB_TOKEN and -Download.'}
    $null=New-Item -ItemType Directory -Force -Path (Split-Path -Parent ([IO.Path]::GetFullPath($Archive)))
    # The archive is read as a fixture, never executed or trusted.
    Invoke-WebRequest -UseBasicParsing -Headers @{Authorization=('Bearer '+$env:GITHUB_TOKEN);Accept='application/vnd.github+json'} -Uri 'https://api.github.com/repos/MazTema-debug/ClawTweaks/actions/artifacts/13636036356/zip' -OutFile $Archive
}
if((Get-FileHash -LiteralPath $Archive -Algorithm SHA256).Hash -cne $expected){throw 'The .49 archive digest does not match the preserved return.'}
if(-not(Test-Path -LiteralPath $Extracted)){Expand-Archive -LiteralPath $Archive -DestinationPath $Extracted}
$root=[IO.Path]::GetFullPath($Extracted).TrimEnd('\')+'\'
$seen=@{}
foreach($line in Get-Content -LiteralPath (Join-Path $root 'SHA256SUMS.txt')){
    if($line -cnotmatch '^([A-F0-9]{64})  (.+)$'){throw 'Invalid .49 checksum record.'}
    $hash=$Matches[1];$relative=$Matches[2];$file=[IO.Path]::GetFullPath((Join-Path $root $relative))
    if(-not $file.StartsWith($root,[StringComparison]::OrdinalIgnoreCase) -or $seen.ContainsKey($file)){throw 'Invalid .49 checksum path.'}
    $seen[$file]=$true
    if((Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash -cne $hash){throw ('The .49 extraction differs: '+$relative)}
}
$files=@(Get-ChildItem -LiteralPath $root -Recurse -File | Where-Object Name -ne 'SHA256SUMS.txt')
if($seen.Count -ne $files.Count){throw 'The .49 extraction has missing or extra files.'}
foreach($pin in @(
    @{Path='Helper\LegionTweaks.Helper.exe';Hash='9D485EF0E4354278BB9FC496110A7536585B735EA898ED94A4CE4BD9BF438B00'},
    @{Path='Helper\Qam\LegionTweaks.QamProbe.exe';Hash='0874C28F843071381FC54A406FAA28682980480FA4ECB8B603B5088C4FA3B50C'})){
    if((Get-FileHash -LiteralPath (Join-Path $root $pin.Path) -Algorithm SHA256).Hash -cne $pin.Hash){throw 'A .49 executable pin differs.'}
}
$cert=[Security.Cryptography.X509Certificates.X509Certificate2]::new((Join-Path $root 'LegionTweaks.cer'))
if($cert.HasPrivateKey -or $cert.Thumbprint -cne '4AB5A8F9579DFDA51130307D543BEEF06EBF4DA0'){throw 'The .49 public certificate differs.'}
Write-Output ('PASS exact .49 fixture: '+$expected+'; '+$seen.Count+' internal hashes; not executed or imported')
