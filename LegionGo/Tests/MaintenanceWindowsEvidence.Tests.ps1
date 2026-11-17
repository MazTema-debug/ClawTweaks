# SPDX-License-Identifier: AGPL-3.0-only
# Queries and trust are simulated; the public certificate is a test fixture.
$ErrorActionPreference='Stop'
Import-Module (Join-Path $PSScriptRoot '..\scripts\MaintenanceWindowsEvidence.psm1') -Force
$root=Join-Path ([IO.Path]::GetTempPath()) ('LegionTweaks-external-fixture-'+[Guid]::NewGuid().ToString('N'));$null=New-Item -ItemType Directory -Path $root
$widget=Join-Path $root 'widget.fixture';[IO.File]::WriteAllText($widget,'fixture')
$id=[Security.Principal.WindowsIdentity]::GetCurrent();try{$sid=$id.User.Value}finally{$id.Dispose()}
$helper=Join-Path $env:ProgramFiles 'LegionTweaks-Z1E\LegionTweaks.Helper.exe';$working=Split-Path -Parent $helper
$xml=@"
<Task xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task"><Triggers><LogonTrigger><UserId>$sid</UserId></LogonTrigger></Triggers><Principals><Principal><UserId>$sid</UserId><LogonType>InteractiveToken</LogonType><RunLevel>HighestAvailable</RunLevel></Principal></Principals><Settings><Enabled>true</Enabled></Settings><Actions><Exec><Command>$helper</Command><WorkingDirectory>$working</WorkingDirectory></Exec></Actions></Task>
"@
$cert=[Security.Cryptography.X509Certificates.X509Certificate2]::new((Join-Path $PSScriptRoot 'Fixtures\LegionTweaks49-public.cer'))
$state=[pscustomobject]@{Xml=$xml;Startup='{"Skip":true,"Version":1}';Version='0.1.49.0';WidgetOk=$true}
$config=@{ExpectedSid=$sid;WidgetPath=$widget;WidgetSha256=(Get-FileHash -LiteralPath $widget -Algorithm SHA256).Hash;Version='0.1.49.0';Signer=$cert.Thumbprint;HelperPath=$helper;AssertProtection={param($path)return $true}}
$ops=@{Package={return [pscustomobject]@{PackageFamilyName='MazTema.LegionTweaks_wsckx05r95yk0';Publisher='CN=LegionTweaks Dev';Architecture='X64';Version=$state.Version;Status='Ok';PackageFullName='fixture';InstallLocation='C:\fixture'}}.GetNewClosure();TaskXml={return $state.Xml}.GetNewClosure();Certificate={param($thumb)return $cert}.GetNewClosure();VerifyWidget={param($path,$location)return $state.WidgetOk}.GetNewClosure();Startup={param($sid,$check)return $state.Startup}.GetNewClosure()}
$config.RequireStartup=$true
$fingerprint=Get-LtWindowsPublicationFingerprint -Configuration $config -Operations $ops
if($fingerprint -cnotmatch '^[A-F0-9]{64}$'){throw 'Huella externa inválida.'}
$state.Startup='absent';$blocked=$false
try{Get-LtWindowsPublicationFingerprint -Configuration $config -Operations $ops|Out-Null}catch{$blocked=$true}
if(-not $blocked){throw 'Faltó exigir supresión antes de publicar.'}
$config.Remove('RequireStartup');$after=Get-LtWindowsPublicationFingerprint -Configuration $config -Operations $ops -IncludeStartupState
if($after.Sha256 -ne $fingerprint -or $after.StartupState -ne 'absent' -or $state.Startup -ne 'absent'){throw 'Consumo legítimo no fue preservado o alteró huella estable.'}
$state.Startup='malformed';$blocked=$false
try{Get-LtWindowsPublicationFingerprint -Configuration $config -Operations $ops|Out-Null}catch{$blocked=$true}
if(-not $blocked){throw 'Supresión inesperada aceptada.'};$state.Startup='absent'
Write-Output 'PASS publicación exige marca y reconciliación admite su consumo sin recrearla'
$state.WidgetOk=$false;$blocked=$false
try{Get-LtWindowsPublicationFingerprint -Configuration $config -Operations $ops|Out-Null}catch{$blocked=$true}
if(-not $blocked){throw 'Widget alterado aceptado.'}
$state.WidgetOk=$true;$state.Version='0.1.50.0';$blocked=$false
try{Get-LtWindowsPublicationFingerprint -Configuration $config -Operations $ops|Out-Null}catch{$blocked=$true}
if(-not $blocked){throw 'Versión externa incorrecta aceptada.'}
Write-Output 'PASS evidencia externa rechaza widget o versión distintos'

# Use the real installed-widget consumer against a temporary deployment, with
# only Windows package/task/trust queries replaced by fixture values.
Import-Module (Join-Path $PSScriptRoot '..\scripts\MaintenanceSource.psm1') -DisableNameChecking
Add-Type -AssemblyName System.IO.Compression,System.IO.Compression.FileSystem
$fixtureInput=Join-Path $root 'msix-input';$fixtureInstalled=Join-Path $root 'msix-installed';$null=New-Item -ItemType Directory -Path $fixtureInput -Force
$types='<Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Default Extension="xml" ContentType="text/xml"/><Default Extension="bin" ContentType="application/octet-stream"/><Default Extension="p7x" ContentType="application/octet-stream"/></Types>'
foreach($pair in @(@('[Content_Types].xml',$types),@('AppxManifest.xml','manifest'),@('AppxBlockMap.xml','blockmap'),@('AppxSignature.p7x','signature'),@('payload.bin','payload'))){[IO.File]::WriteAllText((Join-Path $fixtureInput $pair[0]),$pair[1])}
$fixturePackage=Join-Path $root 'fixture.msix';[IO.Compression.ZipFile]::CreateFromDirectory($fixtureInput,$fixturePackage);[IO.Compression.ZipFile]::ExtractToDirectory($fixturePackage,$fixtureInstalled)
Remove-Item -LiteralPath (Join-Path $fixtureInstalled '[Content_Types].xml')
$config.WidgetPath=$fixturePackage;$config.WidgetSha256=(Get-FileHash -LiteralPath $fixturePackage -Algorithm SHA256).Hash;$state.Version='0.1.49.0'
$ops.Package={return [pscustomobject]@{PackageFamilyName='MazTema.LegionTweaks_wsckx05r95yk0';Publisher='CN=LegionTweaks Dev';Architecture='X64';Version=$state.Version;Status='Ok';PackageFullName='fixture';InstallLocation=$fixtureInstalled}}.GetNewClosure()
$ops.VerifyWidget={param($path,$location)return (Test-LtInstalledWidgetContents -PackagePath $path -InstallLocation $location)}
$fingerprint=Get-LtWindowsPublicationFingerprint -Configuration $config -Operations $ops
if($fingerprint -cnotmatch '^[A-F0-9]{64}$'){throw 'Evidencia externa no verificó el widget fixture.'}
[IO.File]::WriteAllText((Join-Path $fixtureInstalled 'AppxManifest.xml'),'tampered');$blocked=$false
try{Get-LtWindowsPublicationFingerprint -Configuration $config -Operations $ops|Out-Null}catch{$blocked=$true}
if(-not $blocked){throw 'Evidencia externa aceptó manifiesto instalado alterado.'}
Write-Output 'PASS evidencia externa usa verificador real y rechaza manifiesto alterado'
