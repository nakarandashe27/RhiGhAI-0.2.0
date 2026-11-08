$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$package = Join-Path $repo 'artifacts\RhiGhAI-0.2.2-local-installer'
$fixture = Join-Path $repo ('artifacts\installer-test-' + [Guid]::NewGuid().ToString('N'))
$target = Join-Path $fixture 'Plug-ins\RhiGhAI'
$backups = Join-Path $fixture 'Backups'
$source = Join-Path $fixture 'Payload'
$lines = [Collections.Generic.List[string]]::new()
New-Item -ItemType Directory -Path (Join-Path $target '0.1.4') -Force | Out-Null
Set-Content -LiteralPath (Join-Path $target '0.1.4\user-note.txt') -Value 'preserve me'
try {
 & (Join-Path $package 'Install-RhiGhAI.ps1') -InstallRoot $target -BackupRoot $backups -SkipRegistration
 if (-not (Get-ChildItem -LiteralPath $backups -Filter user-note.txt -Recurse)) { throw 'Previous version was not preserved' }
 $lines.Add('PASS valid install, old version and unrelated file preserved in backup')
 $before = (Get-FileHash -LiteralPath (Join-Path $target 'RhiGhAI.Core.dll')).Hash
 New-Item -ItemType Directory -Path $source | Out-Null
 Get-ChildItem -LiteralPath (Join-Path $package 'Payload\RhiGhAI') | Copy-Item -Destination $source -Recurse
 Add-Content -LiteralPath (Join-Path $source 'RhiGhAI.Core.dll') -Value 'corruption'
 try {
  & (Join-Path $package 'Install-RhiGhAI.ps1') -PayloadPath $source -InstallRoot $target -BackupRoot $backups -SkipRegistration
  throw 'Corrupt payload was accepted'
 } catch { if ($_.Exception.Message -notmatch 'Checksum mismatch') { throw } }
 if ((Get-FileHash -LiteralPath (Join-Path $target 'RhiGhAI.Core.dll')).Hash -ne $before) { throw 'Corrupt package changed installation' }
 $lines.Add('PASS corrupt payload rejected before mutation; installed hash unchanged')
 Remove-Item -LiteralPath (Join-Path $source 'RhiGhAI.Core.dll')
 try {
  & (Join-Path $package 'Install-RhiGhAI.ps1') -PayloadPath $source -InstallRoot $target -BackupRoot $backups -SkipRegistration
  throw 'Missing payload was accepted'
 } catch { if ($_.Exception.Message -notmatch 'Missing package file') { throw } }
 $lines.Add('PASS missing required file rejected before mutation')
 $blockedBackups = Join-Path $fixture 'backup-is-a-file'
 Set-Content -LiteralPath $blockedBackups -Value 'block directory creation'
 try {
  & (Join-Path $package 'Install-RhiGhAI.ps1') -InstallRoot $target -BackupRoot $blockedBackups -SkipRegistration
  throw 'Expected backup staging failure'
 } catch { if ($_.Exception.Message -eq 'Expected backup staging failure') { throw } }
 if ((Get-FileHash -LiteralPath (Join-Path $target 'RhiGhAI.Core.dll')).Hash -ne $before) { throw 'Staging failure changed installation' }
 if (Get-ChildItem -LiteralPath (Split-Path $target) -Filter '.RhiGhAI-stage-*') { throw 'Failed staging left residue' }
 $lines.Add('PASS backup staging failure preserves active install and cleans temporary stage')
 $lines.Add('Fixture retained for inspection: ' + $fixture)
 $lines | Set-Content -LiteralPath (Join-Path $repo 'artifacts\INSTALLER_ACCEPTANCE.txt')
 $lines
} catch {
 $lines.Add('FAILED: ' + $_)
 $lines | Set-Content -LiteralPath (Join-Path $repo 'artifacts\INSTALLER_ACCEPTANCE.txt')
 throw
}

