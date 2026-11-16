# SPDX-License-Identifier: AGPL-3.0-only
# Descriptor round trips on private fixtures with different parent inheritance.
$ErrorActionPreference='Stop'
Import-Module (Join-Path $PSScriptRoot '..\scripts\MaintenanceBackup.psm1') -Force
Import-Module (Join-Path $PSScriptRoot '..\scripts\MaintenanceBackupSet.psm1') -Force
$root=Join-Path ([IO.Path]::GetTempPath()) ('LegionTweaks-backup-inheritance-fixture-'+[Guid]::NewGuid().ToString('N'))
$null=New-Item -ItemType Directory -Path $root
$who=[Security.Principal.WindowsIdentity]::GetCurrent().User
$acl=Get-Acl -LiteralPath $root
$acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new($who,[Security.AccessControl.FileSystemRights]::FullControl,[Security.AccessControl.InheritanceFlags]'ContainerInherit,ObjectInherit',[Security.AccessControl.PropagationFlags]::None,[Security.AccessControl.AccessControlType]::Allow))
Set-Acl -LiteralPath $root -AclObject $acl
$source=Join-Path $root 'source';$other=Join-Path $root 'other-parent'
$null=New-Item -ItemType Directory -Path (Join-Path $source 'nested'),(Join-Path $source 'empty'),$other
$sourceAcl=Get-Acl -LiteralPath $source
$sourceAcl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new([Security.Principal.SecurityIdentifier]::new('S-1-5-21-11-22-33-4444'),[Security.AccessControl.FileSystemRights]::ReadAndExecute,[Security.AccessControl.InheritanceFlags]'ContainerInherit,ObjectInherit',[Security.AccessControl.PropagationFlags]::None,[Security.AccessControl.AccessControlType]::Allow))
Set-Acl -LiteralPath $source -AclObject $sourceAcl
$otherAcl=Get-Acl -LiteralPath $other
$otherAcl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new([Security.Principal.SecurityIdentifier]::new('S-1-5-21-11-22-33-5555'),[Security.AccessControl.FileSystemRights]::Write,[Security.AccessControl.InheritanceFlags]'ContainerInherit,ObjectInherit',[Security.AccessControl.PropagationFlags]::None,[Security.AccessControl.AccessControlType]::Allow))
Set-Acl -LiteralPath $other -AclObject $otherAcl
$protect={param($a,$b,$c)return $true}
foreach($mode in @('auto','legacy','protected')){
    $file=Join-Path $source ('nested\'+$mode+'.fixture');[IO.File]::WriteAllText($file,('synthetic '+$mode))
    $security=Get-Acl -LiteralPath $file
    if($mode -eq 'legacy'){
        $raw=[Security.AccessControl.RawSecurityDescriptor]::new($security.GetSecurityDescriptorBinaryForm(),0)
        $raw.SetFlags([Security.AccessControl.ControlFlags]($raw.ControlFlags -band (-bnot [Security.AccessControl.ControlFlags]::DiscretionaryAclAutoInherited)))
        $bytes=New-Object byte[] $raw.BinaryLength;$raw.GetBinaryForm($bytes,0);$security.SetSecurityDescriptorBinaryForm($bytes)
        $null=Set-LtBackupSecurity -Path $file -Security $security -AssertProtection $protect
    }elseif($mode -eq 'protected'){
        $security.SetAccessRuleProtection($true,$true);$null=Set-LtBackupSecurity -Path $file -Security $security -AssertProtection $protect
    }
    $before=(Get-Acl -LiteralPath $file).Sddl;$hash=(Get-FileHash -LiteralPath $file).Hash
    $destination=Join-Path $other ('copy-'+$mode)
    $records=@(Copy-LtProtectedBackup -SourceRoot $source -DestinationRoot $destination -RelativeFiles @('nested\'+$mode+'.fixture') -AssertProtection $protect)
    $copied=Join-Path $destination ('nested\'+$mode+'.fixture')
    if($records.Count -ne 1 -or $records[0].Sddl -cne $before -or (Get-Acl -LiteralPath $copied).Sddl -cne $before -or (Get-Acl -LiteralPath $file).Sddl -cne $before -or (Get-FileHash -LiteralPath $copied).Hash -cne $hash){throw ('Descriptor round trip failed: '+$mode)}
    Write-Output ('PASS exact backup descriptor '+$mode+' across different parent ACLs; source unchanged')
}
$beforeDirectories=@(Get-Item -LiteralPath $source;Get-ChildItem -LiteralPath $source -Directory -Recurse) | ForEach-Object {[pscustomobject]@{Path=$_.FullName;Sddl=(Get-Acl -LiteralPath $_.FullName).Sddl}}
$metadata=@{TaskXml='<Task/>';TaskWasEnabled=$true;PackageFullName='fixture';TargetCertificateWasPresent=$false;SourceCertificateWasPresent=$true}
$backup=Join-Path $other 'backupset'
$saved=New-LtBackupSet -Destination $backup -Groups @([pscustomobject]@{Name='helper';SourceRoot=$source}) -Metadata $metadata -AssertProtection $protect
$null=Test-LtBackupSet -Destination $backup -ExpectedManifestSha256 $saved.ManifestSha256 -AssertProtection $protect
$restore=Join-Path $other 'restore'
$null=New-LtRestoreTree -BackupDestination $backup -ExpectedManifestSha256 $saved.ManifestSha256 -RestoreTree $restore -AssertProtection $protect
$null=Test-LtRestoreTree -BackupDestination $backup -ExpectedManifestSha256 $saved.ManifestSha256 -RestoreTree $restore -AssertProtection $protect
foreach($entry in $beforeDirectories){if((Get-Acl -LiteralPath $entry.Path).Sddl -cne $entry.Sddl){throw 'Source directory ACL changed.'}}
Write-Output 'PASS backupset and restore retain file/directory owner, group, ACEs, protection and inheritance flags'
$changed=Join-Path $restore 'nested\auto.fixture';$changedAcl=Get-Acl -LiteralPath $changed
$changedAcl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new($who,[Security.AccessControl.FileSystemRights]::Read,[Security.AccessControl.AccessControlType]::Allow));Set-Acl -LiteralPath $changed -AclObject $changedAcl
$blocked=$false;try{Test-LtRestoreTree -BackupDestination $backup -ExpectedManifestSha256 $saved.ManifestSha256 -RestoreTree $restore -AssertProtection $protect|Out-Null}catch{$blocked=$true}
if(-not $blocked){throw 'A changed restore descriptor was accepted.'}
Write-Output 'PASS altered restore descriptor blocks without changing source or backup'
