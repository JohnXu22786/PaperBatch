[CmdletBinding()]
param(
    [string]$Configuration = 'Release',
    [string]$OutputDirectory = 'artifacts/windows-x64-lite'
)

$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$outputPath = [System.IO.Path]::GetFullPath((Join-Path $repoRoot $OutputDirectory))
$artifactsRoot = [System.IO.Path]::GetFullPath((Join-Path $repoRoot 'artifacts'))
$artifactsPrefix = $artifactsRoot.TrimEnd([System.IO.Path]::DirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar
$publishPath = Join-Path $outputPath 'app'
$archivePath = Join-Path $outputPath 'PaperBatch-windows-x64-lite.zip'

if (-not $IsWindows) { throw 'The Windows x64 package must be built on Windows.' }
if (-not $outputPath.StartsWith($artifactsPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw 'OutputDirectory must resolve to a child directory of artifacts/.'
}
$status = (& git -C $repoRoot status --porcelain --untracked-files=all)
if ($LASTEXITCODE -ne 0) { throw 'Could not verify source worktree status.' }
if ($status) { throw 'Packaging requires a clean worktree so the source revision identifies the packaged source.' }
if (Test-Path $outputPath) { Remove-Item -LiteralPath $outputPath -Recurse -Force }
New-Item -ItemType Directory -Path $publishPath -Force | Out-Null

$project = Join-Path $repoRoot 'src/PaperBatch.Desktop/PaperBatch.Desktop.csproj'
& dotnet restore $project --runtime win-x64 --locked-mode -p:NuGetAudit=false
if ($LASTEXITCODE -ne 0) { throw "dotnet restore failed with exit code $LASTEXITCODE" }
& dotnet publish $project --configuration $Configuration --runtime win-x64 --self-contained true --no-restore -p:NuGetAudit=false -p:Deterministic=true -p:ContinuousIntegrationBuild=true --output $publishPath
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE" }

Copy-Item -LiteralPath (Join-Path $repoRoot 'LICENSE') -Destination (Join-Path $outputPath 'LICENSE')
Copy-Item -LiteralPath (Join-Path $repoRoot 'docs/dependencies/desktop-ui.md') -Destination (Join-Path $outputPath 'THIRD-PARTY-UI-NOTICES.md')
Copy-Item -LiteralPath (Join-Path $repoRoot 'docs/dependencies/core-runtime.md') -Destination (Join-Path $outputPath 'THIRD-PARTY-CORE-NOTICES.md')
if (-not $env:DOTNET_ROOT) { throw 'DOTNET_ROOT is required to locate the SDK third-party notices.' }
Copy-Item -LiteralPath (Join-Path $env:DOTNET_ROOT 'ThirdPartyNotices.txt') -Destination (Join-Path $outputPath 'DOTNET-THIRD-PARTY-NOTICES.txt')
@'
PaperBatch Windows x64 lite packaging prototype

This self-contained .NET desktop shell targets Windows x64. It does not bundle
LibreOffice, Microsoft Office, WPS Office, PDFium, printer drivers, or fonts.
Consequently it is not an offline full renderer and does not claim Office/WPS
compatibility, installation on a clean PC, or physical printing support. PDF
preview and preparation features needing PDFium remain unavailable unless the
user separately configures an explicitly trusted runtime. The current app also
has no production print backend.

The application and source are licensed under AGPL-3.0-only; see LICENSE.
Third-party dependency versions and license evidence are in
THIRD-PARTY-UI-NOTICES.md, THIRD-PARTY-CORE-NOTICES.md, and
DOTNET-THIRD-PARTY-NOTICES.txt. Package provenance and per-file SHA-256 values are in
package-manifest.json. This unsigned development artifact is not a release.
'@ | Set-Content -LiteralPath (Join-Path $outputPath 'README-package.txt') -Encoding utf8

$files = Get-ChildItem -LiteralPath $outputPath -File -Recurse |
    Where-Object { $_.FullName -ne (Join-Path $outputPath 'package-manifest.json') } | ForEach-Object {
    [ordered]@{
        path = [System.IO.Path]::GetRelativePath($outputPath, $_.FullName).Replace('\', '/')
        size = $_.Length
        sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    }
}
$lockFiles = @(
    'src/PaperBatch.Desktop/packages.lock.json',
    'src/PaperBatch.Core/packages.lock.json'
) | ForEach-Object {
    [ordered]@{
        path = $_
        sha256 = (Get-FileHash -LiteralPath (Join-Path $repoRoot $_) -Algorithm SHA256).Hash.ToLowerInvariant()
    }
}
$revision = (& git -C $repoRoot rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0 -or -not $revision) { throw 'Could not determine source revision.' }
$manifest = [ordered]@{
    schemaVersion = 1
    package = 'PaperBatch Windows x64 lite prototype'
    sourceRevision = $revision
    target = 'win-x64'
    configuration = $Configuration
    selfContained = $true
    runtimeIdentifier = 'win-x64'
    dependencyLockFiles = @($lockFiles)
    license = 'AGPL-3.0-only'
    signed = $false
    excludedRuntimes = @('LibreOffice', 'Microsoft Office', 'WPS Office', 'PDFium', 'printer drivers', 'fonts')
    files = @($files | Sort-Object path)
}
$manifest | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $outputPath 'package-manifest.json') -Encoding utf8
Compress-Archive -Path (Join-Path $outputPath '*') -DestinationPath $archivePath -CompressionLevel Optimal
$archiveHash = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash.ToLowerInvariant()
"Package: $archivePath"
"SHA256: $archiveHash"
