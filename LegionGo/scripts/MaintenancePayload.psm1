# SPDX-License-Identifier: AGPL-3.0-only
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'

# Constructs and checks a staging tree only; no installer or historical script
# is run. The package and protection callbacks must be supplied by the trusted
# coordinator and must validate the externally pinned package and ACL scope.
function Invoke-LtPayloadCheck([scriptblock]$Check,$Argument){
    $r=@(& $Check $Argument);if($r.Count -ne 1 -or $r[0] -isnot [bool] -or -not $r[0]){throw 'Verificación de payload rechazada.'}
    return $true
}
function Get-LtControlBytes([string]$Text,[string]$Encoding){
    $codec=if($Encoding -eq 'utf8'){[Text.UTF8Encoding]::new($true)}else{[Text.UnicodeEncoding]::new($false,$true)}
    $preamble=$codec.GetPreamble();$content=$codec.GetBytes($Text+"`r`n")
    $bytes=[byte[]]::new($preamble.Length+$content.Length)
    [Array]::Copy($preamble,0,$bytes,0,$preamble.Length);[Array]::Copy($content,0,$bytes,$preamble.Length,$content.Length)
    return ,$bytes
}
function Test-LtDestinationTree {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Extracted,[Parameter(Mandatory)][string]$Tree,
          [Parameter(Mandatory)][string]$Family,[Parameter(Mandatory)][string]$Signer,
          [Parameter(Mandatory)][scriptblock]$AssertProtection)
    if($Family -ne 'MazTema.LegionTweaks_wsckx05r95yk0' -or $Signer -notmatch '^[0-9A-Fa-f]{40}$'){throw 'Identidad de payload inválida.'}
    $root=[IO.Path]::GetFullPath($Tree).TrimEnd('\');$source=[IO.Path]::GetFullPath($Extracted).TrimEnd('\')
    $null=Invoke-LtPayloadCheck $AssertProtection $root
    $helper=Join-Path $source 'Helper';$expected=[Collections.Generic.Dictionary[string,string]]::new([StringComparer]::OrdinalIgnoreCase)
    $directories=[Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach($file in Get-ChildItem -LiteralPath $helper -Force -Recurse -ErrorAction Stop){
        if($file.Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'Enlace en payload.'}
        $rel=$file.FullName.Substring($helper.Length+1)
        if($file.PSIsContainer){$null=$directories.Add($rel)}else{$expected.Add($rel,(Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash)}
    }
    $uninstall=Join-Path $source 'Uninstall-LegionGo.ps1'
    $expected.Add('Uninstall-LegionGo.ps1',(Get-FileHash -LiteralPath $uninstall -Algorithm SHA256).Hash)
    foreach($pair in @(@('allowed-family.txt',(Get-LtControlBytes $Family 'utf8')),@('certificate-thumbprint.txt',(Get-LtControlBytes $Signer 'unicode')))){
        $sha=[Security.Cryptography.SHA256]::Create();try{$digest=([BitConverter]::ToString($sha.ComputeHash([byte[]]$pair[1]))).Replace('-','')}finally{$sha.Dispose()}
        $expected.Add([string]$pair[0],$digest)
    }
    foreach($file in Get-ChildItem -LiteralPath $root -Force -Recurse -ErrorAction Stop){
        if($file.Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'Enlace en árbol destino.'}
        $rel=$file.FullName.Substring($root.Length+1)
        if($file.PSIsContainer){if(-not $directories.Remove($rel)){throw 'Directorio destino inesperado.'}}
        else{
            if(-not $expected.ContainsKey($rel) -or (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash -ne $expected[$rel]){throw 'Archivo destino inesperado o alterado.'}
            $null=$expected.Remove($rel)
        }
    }
    if($expected.Count -ne 0 -or $directories.Count -ne 0){throw 'Faltan archivos o directorios del árbol destino.'}
    $null=Invoke-LtPayloadCheck $AssertProtection $root
    return $true
}
function New-LtDestinationTree {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Extracted,[Parameter(Mandatory)][string]$Tree,
          [Parameter(Mandatory)][string]$Family,[Parameter(Mandatory)][string]$Signer,
          [Parameter(Mandatory)][scriptblock]$AssertPackage,[Parameter(Mandatory)][scriptblock]$AssertProtection,
          [Parameter(Mandatory)][scriptblock]$ApplyAndValidateAcl)
    $root=[IO.Path]::GetFullPath($Tree).TrimEnd('\');$source=[IO.Path]::GetFullPath($Extracted).TrimEnd('\')
    if($root -eq $source -or $root.StartsWith($source+'\',[StringComparison]::OrdinalIgnoreCase) -or $source.StartsWith($root+'\',[StringComparison]::OrdinalIgnoreCase) -or (Test-Path -LiteralPath $root)){throw 'Destino de staging inválido o existente.'}
    $null=Invoke-LtPayloadCheck $AssertPackage $source
    $null=Invoke-LtPayloadCheck $AssertProtection ([IO.Path]::GetDirectoryName($root))
    $null=New-Item -ItemType Directory -Path $root -ErrorAction Stop
    Copy-Item -Path (Join-Path $source 'Helper\*') -Destination $root -Recurse -Force -ErrorAction Stop
    Copy-Item -LiteralPath (Join-Path $source 'Uninstall-LegionGo.ps1') -Destination $root -ErrorAction Stop
    [IO.File]::WriteAllBytes((Join-Path $root 'allowed-family.txt'),(Get-LtControlBytes $Family 'utf8'))
    [IO.File]::WriteAllBytes((Join-Path $root 'certificate-thumbprint.txt'),(Get-LtControlBytes $Signer 'unicode'))
    $null=Invoke-LtPayloadCheck $ApplyAndValidateAcl $root
    $null=Invoke-LtPayloadCheck $AssertPackage $source
    return (Test-LtDestinationTree -Extracted $source -Tree $root -Family $Family -Signer $Signer -AssertProtection $AssertProtection)
}
Export-ModuleMember -Function New-LtDestinationTree,Test-LtDestinationTree
