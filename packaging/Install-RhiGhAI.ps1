[CmdletBinding()]
param(
    # PS 5.1 leaves $PSScriptRoot empty in param defaults of an advanced script run via -File (as the .cmd does).
    [string]$PayloadPath,
    [string]$InstallRoot = (Join-Path $env:APPDATA 'McNeel\Rhinoceros\8.0\Plug-ins\RhiGhAI'),
    [string]$BackupRoot = (Join-Path $env:LOCALAPPDATA 'RhiGhAI\InstallBackups'),
    [switch]$SkipRegistration
)
$ErrorActionPreference = 'Stop'
if ([string]::IsNullOrEmpty($PayloadPath)) { $PayloadPath = Join-Path $PSScriptRoot 'Payload\RhiGhAI' }
if ([System.Security.Principal.WindowsIdentity]::GetCurrent().Name -match 'CodexSandbox') {
    throw 'Run the installer under your Windows account, not the Codex sandbox account.'
}
function Assert-RhinoClosed {
    if (Get-Process Rhino -ErrorAction SilentlyContinue) { throw 'Close all Rhino windows, then run Install RhiGhAI.cmd again.' }
}
function Assert-SafePath([string]$Path, [string]$Parent) {
    $resolved = [IO.Path]::GetFullPath($Path)
    $prefix = [IO.Path]::GetFullPath($Parent).TrimEnd('\') + '\'
    if (-not $resolved.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) { throw "Unsafe install path: $resolved" }
    if ((Test-Path -LiteralPath $resolved) -and ((Get-Item -LiteralPath $resolved -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
        throw "Reparse point is not an installation directory: $resolved"
    }
}
Assert-RhinoClosed
if (-not $SkipRegistration -and -not (Test-Path -LiteralPath (Join-Path $env:ProgramFiles 'Rhino 8\System\Rhino.exe'))) { throw 'Rhino 8 was not found.' }
$target = [IO.Path]::GetFullPath($InstallRoot)
$parent = Split-Path -Parent $target
$source = [IO.Path]::GetFullPath($PayloadPath)
$backups = [IO.Path]::GetFullPath($BackupRoot)
$stage = Join-Path $parent ('.RhiGhAI-stage-' + [Guid]::NewGuid().ToString('N'))
$backup = Join-Path $backups ((Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N'))
Assert-SafePath $target $parent
Assert-SafePath $stage $parent
Assert-SafePath $backup $backups
$required = @('RhiGhAI.Rhino.rhp','RhiGhAI.Rhino.deps.json','RhiGhAI.Rhino.runtimeconfig.json','RhiGhAI.Core.dll','RhiGhAI.Grasshopper.gha','manifest.yml','NOTICE.txt','README.md','Runtime\codex.exe')
foreach ($file in $required) {
    if (-not (Test-Path -LiteralPath (Join-Path $source $file) -PathType Leaf)) { throw "Missing package file: $file. Extract the entire ZIP first." }
}
$signature = Get-AuthenticodeSignature -LiteralPath (Join-Path $source 'Runtime\codex.exe')
if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -notmatch 'O="?OpenAI OpCo, LLC"?') { throw 'Codex runtime signature is invalid.' }
$checksumsPath = Join-Path $PSScriptRoot 'SHA256SUMS.txt'
if (-not (Test-Path -LiteralPath $checksumsPath)) { throw 'Package checksum manifest is missing.' }
$packageHashes = @{}
foreach ($line in Get-Content -LiteralPath $checksumsPath) {
    if ($line -notmatch '^([A-Fa-f0-9]{64})  Payload\\RhiGhAI\\(.+)$') { throw 'Invalid checksum manifest.' }
    $expectedHash = $matches[1]; $relative = $matches[2]
    if ($packageHashes.ContainsKey($relative)) { throw "Duplicate checksum entry: $relative" }
    $packageHashes[$relative] = $expectedHash
    $filePath = [IO.Path]::GetFullPath((Join-Path $source $relative))
    Assert-SafePath $filePath $source
    if ((Get-FileHash -LiteralPath $filePath -Algorithm SHA256).Hash -ne $expectedHash) { throw "Checksum mismatch: $relative" }
}
foreach ($file in $required) { if (-not $packageHashes.ContainsKey($file)) { throw "Missing checksum: $file" } }
$key = 'HKCU:\Software\McNeel\Rhinoceros\8.0\Plug-Ins\BC57A265-8A44-4BDB-A887-EA2647812367'
$oldRegistry = if (-not $SkipRegistration -and (Test-Path -LiteralPath ($key + '\PlugIn'))) {
    (Get-ItemProperty -LiteralPath ($key + '\PlugIn') -Name FileName -ErrorAction SilentlyContinue).FileName
} else { $null }
New-Item -ItemType Directory -Path $stage -Force | Out-Null
$hadPrevious = $false; $activated = $false
try {
    # Only known files are copied; the old installation is kept whole, including versioned folders.
    foreach ($file in $required) {
        $destination = Join-Path $stage $file
        New-Item -ItemType Directory -Path (Split-Path -Parent $destination) -Force | Out-Null
        Copy-Item -LiteralPath (Join-Path $source $file) -Destination $destination
    }
    $notices = Join-Path $source 'Runtime\OPENAI-THIRD-PARTY-NOTICES.txt'
    if (Test-Path -LiteralPath $notices) { Copy-Item -LiteralPath $notices -Destination (Join-Path $stage 'Runtime') }
    foreach ($file in $required) {
        if ((Get-FileHash -LiteralPath (Join-Path $stage $file) -Algorithm SHA256).Hash -ne $packageHashes[$file]) {
            throw "Staged checksum mismatch: $file"
        }
    }
    Assert-RhinoClosed
    New-Item -ItemType Directory -Path $backups -Force | Out-Null
    if (Test-Path -LiteralPath $target) {
        Assert-SafePath $target $parent
        Assert-SafePath $backup $backups
        Move-Item -LiteralPath $target -Destination $backup
        $hadPrevious = $true
    }
    Move-Item -LiteralPath $stage -Destination $target
    $activated = $true
    if (-not $SkipRegistration) {
        New-Item -Path ($key + '\PlugIn') -Force | Out-Null
        Set-ItemProperty -LiteralPath $key -Name Name -Value 'RhiGhAI'
        Set-ItemProperty -LiteralPath ($key + '\PlugIn') -Name FileName -Value (Join-Path $target 'RhiGhAI.Rhino.rhp')
        Remove-ItemProperty -LiteralPath $key -Name FileName -ErrorAction SilentlyContinue
    }
    Write-Output "Installed RhiGhAI 0.2.3: $target"
    if ($hadPrevious) { Write-Output "Previous installation preserved: $backup" }
    Write-Output 'Start Rhino 8 and run RhiGhAI. Header must show v0.2.3.'
}
catch {
    if ($activated) {
        $rejected = Join-Path $backups ('failed-' + [Guid]::NewGuid().ToString('N'))
        Assert-SafePath $target $parent; Assert-SafePath $rejected $backups
        Move-Item -LiteralPath $target -Destination $rejected
    }
    if ($hadPrevious) { Assert-SafePath $backup $backups; Move-Item -LiteralPath $backup -Destination $target }
    if (-not $SkipRegistration -and $oldRegistry) { Set-ItemProperty -LiteralPath ($key + '\PlugIn') -Name FileName -Value $oldRegistry }
    throw
}
finally {
    if (Test-Path -LiteralPath $stage) {
        # Stage contains only this invocation's copied files, and was validated within the plugin parent.
        Assert-SafePath $stage $parent
        Remove-Item -LiteralPath $stage -Recurse -Force
    }
}
