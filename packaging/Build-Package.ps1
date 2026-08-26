[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$projectRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$artifactsRoot = [System.IO.Path]::GetFullPath((Join-Path $projectRoot 'artifacts'))
$stagingRoot = [System.IO.Path]::GetFullPath((Join-Path $artifactsRoot 'package-input'))
$packageRoot = [System.IO.Path]::GetFullPath((Join-Path $stagingRoot 'RhiGhAI'))
$installerRoot = [System.IO.Path]::GetFullPath((Join-Path $artifactsRoot 'RhiGhAI-0.2.0-local-installer'))
$installerPayload = Join-Path $installerRoot 'Payload\RhiGhAI'

foreach ($validatedPath in @($stagingRoot, $packageRoot, $installerRoot)) {
    if (-not $validatedPath.StartsWith($artifactsRoot + [System.IO.Path]::DirectorySeparatorChar, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Package path escaped the project artifacts directory: $validatedPath"
    }
}

$env:DOTNET_CLI_HOME = Join-Path $projectRoot '.dotnet-home'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'

function Invoke-DotNet {
    param([Parameter(ValueFromRemainingArguments)][string[]]$Arguments)

    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet $($Arguments -join ' ') failed with exit code $LASTEXITCODE."
    }
}

Invoke-DotNet restore (Join-Path $projectRoot 'RhiGhAI.sln') --locked-mode
Invoke-DotNet test (Join-Path $projectRoot 'tests\RhiGhAI.Tests\RhiGhAI.Tests.csproj') -c $Configuration --no-restore
Invoke-DotNet build (Join-Path $projectRoot 'RhiGhAI.sln') -c $Configuration --no-restore

if (Test-Path -LiteralPath $stagingRoot) {
    Remove-Item -LiteralPath $stagingRoot -Recurse -Force
}

New-Item -ItemType Directory -Path $packageRoot -Force | Out-Null
$rhinoOutput = Join-Path $projectRoot "src\RhiGhAI.Rhino\bin\$Configuration\net8.0-windows"

$files = @(
    'RhiGhAI.Rhino.rhp',
    'RhiGhAI.Rhino.deps.json',
    'RhiGhAI.Rhino.runtimeconfig.json',
    'RhiGhAI.Core.dll',
    'RhiGhAI.Grasshopper.gha'
)
foreach ($file in $files) {
    Copy-Item -LiteralPath (Join-Path $rhinoOutput $file) -Destination $packageRoot
}

Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'manifest.yml') -Destination $packageRoot
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'NOTICE.txt') -Destination $packageRoot
Copy-Item -LiteralPath (Join-Path $projectRoot 'README.md') -Destination (Join-Path $packageRoot 'README.md')

# Codex Desktop is not added to PATH, so fall back to its per-user content-addressed bin folder.
$codexSource = (Get-Command codex.exe -ErrorAction SilentlyContinue | Select-Object -First 1).Source
if ([string]::IsNullOrEmpty($codexSource) -or -not (Test-Path -LiteralPath $codexSource)) {
    $codexSource = Get-ChildItem -Path (Join-Path $env:LOCALAPPDATA 'OpenAI\Codex\bin') -Filter codex.exe -Recurse -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTimeUtc -Descending |
        Select-Object -First 1 -ExpandProperty FullName
}
if ([string]::IsNullOrEmpty($codexSource) -or -not (Test-Path -LiteralPath $codexSource)) {
    throw 'Official Codex Desktop/CLI runtime was not found. Install Codex before building the full RhiGhAI package.'
}

$runtimeDirectory = Join-Path $packageRoot 'Runtime'
New-Item -ItemType Directory -Path $runtimeDirectory -Force | Out-Null
$runtimePath = Join-Path $runtimeDirectory 'codex.exe'
Copy-Item -LiteralPath $codexSource -Destination $runtimePath
$codexNotices = Join-Path (Split-Path -Parent $codexSource) 'THIRD_PARTY_NOTICES.txt'
if (Test-Path -LiteralPath $codexNotices) {
    Copy-Item -LiteralPath $codexNotices -Destination (Join-Path $runtimeDirectory 'OPENAI-THIRD-PARTY-NOTICES.txt')
}
$signature = Get-AuthenticodeSignature -LiteralPath $runtimePath
if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -notmatch 'O="?OpenAI OpCo, LLC"?') {
    throw 'The discovered Codex runtime is not validly signed by OpenAI OpCo, LLC.'
}

$yakZip = Join-Path $artifactsRoot 'RhiGhAI-0.2.0-rh8-win-yak.zip'
$yakPath = Join-Path $artifactsRoot 'RhiGhAI-0.2.0-rh8-win.yak'
$installerZip = Join-Path $artifactsRoot 'RhiGhAI-0.2.0-local-installer.zip'
$obsoleteRhiPath = Join-Path $artifactsRoot 'RhiGhAI-0.1.0-rh8-win.rhi'

foreach ($path in @($yakZip, $yakPath, $installerZip, $obsoleteRhiPath)) {
    if (Test-Path -LiteralPath $path) {
        Remove-Item -LiteralPath $path -Force
    }
}

Compress-Archive -Path (Join-Path $packageRoot '*') -DestinationPath $yakZip -CompressionLevel Optimal
Move-Item -LiteralPath $yakZip -Destination $yakPath

if (Test-Path -LiteralPath $installerRoot) {
    Remove-Item -LiteralPath $installerRoot -Recurse -Force
}

New-Item -ItemType Directory -Path $installerPayload -Force | Out-Null
Get-ChildItem -LiteralPath $packageRoot -Force | Copy-Item -Destination $installerPayload -Recurse -Force
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Install RhiGhAI.cmd') -Destination $installerRoot
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'README-FIRST.txt') -Destination $installerRoot

$checksumPaths = @($files | ForEach-Object { Join-Path $installerPayload $_ }) + (Join-Path $installerPayload 'Runtime\codex.exe')
$checksums = Get-FileHash -Algorithm SHA256 -LiteralPath $checksumPaths
# GetRelativePath does not exist in Windows PowerShell 5.1, which silently produced an empty SHA256SUMS.txt.
$payloadPrefix = $installerPayload.TrimEnd([System.IO.Path]::DirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar
$checksums | ForEach-Object {
    $relative = $_.Path.Substring($payloadPrefix.Length)
    '{0}  Payload\RhiGhAI\{1}' -f $_.Hash, $relative
} |
    Set-Content -LiteralPath (Join-Path $installerRoot 'SHA256SUMS.txt') -Encoding utf8

Compress-Archive -Path (Join-Path $installerRoot '*') -DestinationPath $installerZip -CompressionLevel Optimal

Get-FileHash -Algorithm SHA256 -LiteralPath $installerZip, $yakPath |
    Select-Object Path, Hash
