$ErrorActionPreference = 'Stop'

$expectedSha256 = '3452d11031050ec65e57f180215b57b9e6d465e3fbedafa8a77ad90885c7d23e'
$releaseTag = 'chromium/8111'
$releaseDate = '2026-10-19'
$assetName = 'pdfium-win-x64.tgz'
$expectedAssetSizeBytes = 3905343
$assetUrl = 'https://github.com/bblanchon/pdfium-binaries/releases/download/chromium/8111/pdfium-win-x64.tgz'
$sourceRepository = 'https://github.com/bblanchon/pdfium-binaries'
$proofRoot = Join-Path $env:RUNNER_TEMP 'paperbatch-pdfium-proof'
$extractRoot = Join-Path $proofRoot 'extract'
$archivePath = Join-Path $proofRoot $assetName
$reportPath = Join-Path $proofRoot 'report.json'
$nativeReportPath = Join-Path $proofRoot 'native-report.json'
$processLogPath = Join-Path $proofRoot 'native-process.log'
$startedAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
$noticeFiles = @()
$archiveSizeBytes = $null
$archiveHash = $null
$nativeProof = $null

New-Item -ItemType Directory -Path $proofRoot -Force | Out-Null

function Write-ProofReport {
    param(
        [Parameter(Mandatory = $true)][string]$Status,
        [string]$FailureReason
    )

    $notices = @(
        'https://github.com/bblanchon/pdfium-binaries/blob/master/LICENSE',
        'https://pdfium.googlesource.com/pdfium/+/refs/heads/chromium/8111/LICENSE',
        'https://pdfium.googlesource.com/pdfium/+/refs/heads/chromium/8111/third_party/'
    )
    $binary = $null
    $apiExports = @()
    $composition = [ordered]@{ status = 'unavailable'; reason = $FailureReason }
    $formFixture = [ordered]@{ status = 'unavailable'; reason = $FailureReason }
    if ($null -ne $nativeProof) {
        $binary = $nativeProof.binary
        $apiExports = $nativeProof.apiExports
        $composition = $nativeProof.composition
        $formFixture = $nativeProof.formFixture
    }
    $report = [ordered]@{
        schemaVersion = '1.0'
        generatedAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
        status = $Status
        scope = 'Hosted Windows x64 CI validation of one pinned community-provided PDFium binary.'
        runner = [ordered]@{
            os = [System.Runtime.InteropServices.RuntimeInformation]::OSDescription
            osArchitecture = [System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString()
            processArchitecture = [System.Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture.ToString()
            githubRunId = $env:GITHUB_RUN_ID
            githubRunAttempt = $env:GITHUB_RUN_ATTEMPT
        }
        startedAtUtc = $startedAtUtc
        source = [ordered]@{
            repository = $sourceRepository
            releaseTag = $releaseTag
            releaseDate = $releaseDate
            asset = $assetName
            assetUrl = $assetUrl
            expectedSha256 = $expectedSha256
            observedSha256 = $archiveHash
            archiveSizeBytes = $archiveSizeBytes
            releaseNotesUrl = 'https://github.com/bblanchon/pdfium-binaries/releases/tag/chromium/8111'
            publisherDisclaimer = 'The publisher describes these as community builds and states it is unaffiliated with Google or Foxit.'
            buildLineage = 'The release notes state this version was built from PDFium branch chromium/8111; this is not an independent source-to-binary attestation.'
            packageNoticeFiles = $noticeFiles
            licensingReferences = $notices
        }
        binary = $binary
        apiExports = $apiExports
        composition = $composition
        formFixture = $formFixture
        failure = $FailureReason
        g1 = 'open'
        productDistributionValidated = $false
        notValidated = @(
            'Product-bundled PDFium distribution, release selection policy, or production dependency pin.',
            'Official Google or Foxit build provenance or a complete source-to-binary provenance policy.',
            'Dynamic XFA, JavaScript, all form field types, or general AcroForm compatibility.',
            'Microsoft Office or WPS coexistence.',
            'A safe Windows GDI queue/driver route, job-ID behavior, or physical printer output.'
        )
    }

    $report | ConvertTo-Json -Depth 20 | Set-Content -Path $reportPath -Encoding utf8
}

function Expand-SafeArchive {
    param([Parameter(Mandatory = $true)][string]$Archive, [Parameter(Mandatory = $true)][string]$Destination)

    $root = [System.IO.Path]::GetFullPath($Destination).TrimEnd([System.IO.Path]::DirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar
    [System.IO.Directory]::CreateDirectory($Destination) | Out-Null
    $archiveStream = [System.IO.File]::OpenRead($Archive)
    $gzipStream = [System.IO.Compression.GZipStream]::new($archiveStream, [System.IO.Compression.CompressionMode]::Decompress)
    $tarReader = [System.Formats.Tar.TarReader]::new($gzipStream)
    $totalExpandedBytes = [long]0
    $files = [System.Collections.Generic.List[string]]::new()
    try {
        while ($null -ne ($entry = $tarReader.GetNextEntry($false))) {
            $relativePath = $entry.Name.Replace('\', '/')
            if ($relativePath.StartsWith('./', [System.StringComparison]::Ordinal)) { $relativePath = $relativePath.Substring(2) }
            if ($relativePath -eq '.' -or $relativePath -eq '') {
                if ($entry.EntryType -eq [System.Formats.Tar.TarEntryType]::Directory) { continue }
                throw "The pinned archive contains an empty regular-file member path: $($entry.Name)"
            }
            if ([string]::IsNullOrWhiteSpace($relativePath) -or $relativePath.StartsWith('/', [System.StringComparison]::Ordinal) -or $relativePath.Split('/') -contains '..' -or $relativePath.Contains(':')) {
                throw "The pinned archive contains an unsafe member path: $($entry.Name)"
            }

            $target = [System.IO.Path]::GetFullPath((Join-Path $Destination ($relativePath.Replace('/', [System.IO.Path]::DirectorySeparatorChar))))
            if (-not $target.StartsWith($root, [System.StringComparison]::OrdinalIgnoreCase)) {
                throw "The pinned archive member escapes its temporary extraction directory: $($entry.Name)"
            }

            if ($entry.EntryType -eq [System.Formats.Tar.TarEntryType]::Directory) {
                [System.IO.Directory]::CreateDirectory($target) | Out-Null
                continue
            }
            if ($entry.EntryType -notin @([System.Formats.Tar.TarEntryType]::RegularFile, [System.Formats.Tar.TarEntryType]::V7RegularFile)) {
                throw "The pinned archive contains a link or special file; refusing to extract: $($entry.Name)"
            }
            if ($null -eq $entry.DataStream -or $entry.Length -lt 0 -or $entry.Length -gt 536870912) {
                throw "The pinned archive contains a file outside the 512 MiB extraction limit: $($entry.Name)"
            }
            $totalExpandedBytes += $entry.Length
            if ($totalExpandedBytes -gt 536870912) { throw 'The pinned archive exceeds the 512 MiB total extraction limit.' }

            [System.IO.Directory]::CreateDirectory([System.IO.Path]::GetDirectoryName($target)) | Out-Null
            $destinationStream = [System.IO.File]::Open($target, [System.IO.FileMode]::CreateNew, [System.IO.FileAccess]::Write, [System.IO.FileShare]::None)
            try { $entry.DataStream.CopyTo($destinationStream) } finally { $destinationStream.Dispose() }
            if ((Get-Item -LiteralPath $target).Length -ne $entry.Length) { throw "Extracted archive size differed from its header: $($entry.Name)" }
            $files.Add($relativePath)
        }
    }
    finally {
        $tarReader.Dispose()
        $gzipStream.Dispose()
        $archiveStream.Dispose()
    }
    return ,$files.ToArray()
}

try {
    if ([string]::IsNullOrWhiteSpace($env:RUNNER_TEMP)) { throw 'RUNNER_TEMP is not set.' }
    if ([System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture -ne [System.Runtime.InteropServices.Architecture]::X64 -or [System.Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture -ne [System.Runtime.InteropServices.Architecture]::X64) {
        throw 'The PDFium proof requires a Windows x64 host and process.'
    }

    Invoke-WebRequest -Uri $assetUrl -OutFile $archivePath -MaximumRedirection 5 -TimeoutSec 120
    $archiveSizeBytes = (Get-Item -LiteralPath $archivePath).Length
    if ($archiveSizeBytes -ne $expectedAssetSizeBytes) { throw "Pinned PDFium archive size mismatch; expected $expectedAssetSizeBytes bytes, got $archiveSizeBytes. The archive was not extracted or loaded." }
    $archiveHash = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($archiveHash -ne $expectedSha256) { throw "Pinned PDFium archive digest mismatch; expected $expectedSha256, got $archiveHash. The archive was not extracted or loaded." }

    $files = Expand-SafeArchive -Archive $archivePath -Destination $extractRoot
    $dllPaths = @($files | Where-Object { [System.IO.Path]::GetFileName($_) -ieq 'pdfium.dll' })
    if ($dllPaths.Count -ne 1) { throw "Expected exactly one pdfium.dll in the verified archive, found $($dllPaths.Count)." }
    $noticeFiles = @($files | Where-Object { [System.IO.Path]::GetFileName($_) -match '^(LICENSE|LICENCE|NOTICE|COPYING)(\.|$)|^LICENSES[._-]' } | Sort-Object)
    $dllPath = Join-Path $extractRoot ($dllPaths[0].Replace('/', [System.IO.Path]::DirectorySeparatorChar))

    $projectPath = Join-Path $PSScriptRoot '../tests/PaperBatch.Pdfium.Windows.Tests/PaperBatch.Pdfium.Windows.Tests.csproj'
    $workingDirectory = $extractRoot
    $process = [System.Diagnostics.ProcessStartInfo]::new()
    $process.FileName = 'dotnet'
    $process.WorkingDirectory = $workingDirectory
    $process.RedirectStandardOutput = $true
    $process.RedirectStandardError = $true
    $process.UseShellExecute = $false
    foreach ($argument in @('run', '--project', $projectPath, '-p:NuGetAudit=false', '--', 'smoke', '--pdfium-path', $dllPath, '--archive-path', $archivePath, '--output', $nativeReportPath)) {
        $process.ArgumentList.Add($argument)
    }
    $child = [System.Diagnostics.Process]::Start($process)
    $stdout = $child.StandardOutput.ReadToEndAsync()
    $stderr = $child.StandardError.ReadToEndAsync()
    if (-not $child.WaitForExit(120000)) {
        $child.Kill($true)
        $child.WaitForExit()
        throw 'The PDFium smoke exceeded the two-minute execution limit.'
    }
    $processText = "STDOUT`n$($stdout.GetAwaiter().GetResult())`nSTDERR`n$($stderr.GetAwaiter().GetResult())"
    $processText | Set-Content -Path $processLogPath -Encoding utf8
    if (Test-Path -LiteralPath $nativeReportPath) { $nativeProof = Get-Content -LiteralPath $nativeReportPath -Raw | ConvertFrom-Json }
    if ($null -eq $nativeProof) { throw "PDFium smoke did not write a report. $processText" }
    Write-ProofReport -Status ([string]$nativeProof.status) -FailureReason ([string]$nativeProof.failure)
    if ($child.ExitCode -ne 0) { throw "PDFium native composition/form behavior failed (exit $($child.ExitCode)). See the uploaded report artifact." }
}
catch {
    $failureReason = $_.Exception.Message
    if (-not (Test-Path -LiteralPath $reportPath)) { Write-ProofReport -Status 'blocked' -FailureReason $failureReason }
    throw
}

Write-Output "Wrote scoped PDFium community proof report: $reportPath"
