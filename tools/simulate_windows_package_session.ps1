[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$PackageDirectory,
    [Parameter(Mandatory = $true)][string]$PackageArchive
)
$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
if ([string]::IsNullOrWhiteSpace($env:RUNNER_TEMP)) { throw 'RUNNER_TEMP is required for isolated session setup.' }
$sessionRoot = Join-Path $env:RUNNER_TEMP 'paperbatch-u9-session'
$reportPath = Join-Path $repoRoot 'u9-windows-package-session-report.json'
if (-not (Test-Path -LiteralPath $PackageDirectory -PathType Container)) { throw 'Package directory is missing.' }
if (Test-Path -LiteralPath $sessionRoot) { throw "Session path already exists: $sessionRoot" }
New-Item -ItemType Directory -Path $sessionRoot | Out-Null
$local = Join-Path $sessionRoot 'profile/LocalAppData'
$roaming = Join-Path $sessionRoot 'profile/RoamingAppData'
$sandboxHome = Join-Path $sessionRoot 'profile/Home'
$temp = Join-Path $sessionRoot 'temp'
$blockedRules = @()
$hostProcess = $null
$beforePrinters = $null
$beforeRegistry = $null
$beforeManifest = $null
$afterPrinters = $null
$afterRegistry = $null
$scenario = [ordered]@{ id = 'u9.windows.package-session'; status = 'failed'; assertions = @(); evidenceLimits = @('Hosted Windows runner snapshot, not a client-clean VM.', 'Outbound deny applies to packaged coordinator executable only; it is not system-wide network tracing.', 'Registry evidence is limited to named current-user PaperBatch keys.', 'Filesystem evidence is limited to dedicated temp/appdata/package roots.') }
function Get-PrinterSnapshot {
    $items = @(Get-CimInstance Win32_Printer | Sort-Object Name | ForEach-Object {
        [ordered]@{ name = $_.Name; default = [bool]$_.Default; driver = $_.DriverName; port = $_.PortName }
    })
    return ,$items
}
function Get-PaperBatchRegistrySnapshot {
    function Get-RegistryTree([Microsoft.Win32.RegistryKey]$key, [int]$depth, [ref]$nodeCount) {
        if ($depth -gt 8) { throw 'Selected PaperBatch registry tree exceeded its depth ceiling.' }
        $nodeCount.Value++
        if ($nodeCount.Value -gt 256) { throw 'Selected PaperBatch registry tree exceeded its key-count ceiling.' }
        $values = [ordered]@{}
        foreach ($name in ($key.GetValueNames() | Sort-Object)) {
            $kind = $key.GetValueKind($name).ToString()
            $raw = $key.GetValue($name, $null, [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames)
            $values[$name] = [ordered]@{ kind = $kind; value = $raw }
        }
        $children = [ordered]@{}
        foreach ($name in ($key.GetSubKeyNames() | Sort-Object)) {
            $child = $key.OpenSubKey($name, $false)
            if ($null -eq $child) { $children[$name] = $null; continue }
            try { $children[$name] = Get-RegistryTree $child ($depth + 1) $nodeCount }
            finally { $child.Dispose() }
        }
        return [ordered]@{ values = $values; subkeys = $children }
    }
    $paths = @('HKCU:\Software\PaperBatch', 'HKCU:\Software\Classes\Applications\PaperBatch.Desktop.exe', 'HKCU:\Software\Classes\PaperBatch.Document')
    $result = [ordered]@{}
    foreach ($path in $paths) {
        $relative = $path.Substring('HKCU:\'.Length)
        $key = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($relative, $false)
        if ($null -eq $key) { $result[$path] = $null; continue }
        try {
            $nodes = 0
            $result[$path] = Get-RegistryTree $key 0 ([ref]$nodes)
        } finally { $key.Dispose() }
    }
    return $result
}
function Get-SnapshotDigest($value) {
    $json = ConvertTo-Json -InputObject $value -Depth 32 -Compress
    $bytes = [Text.Encoding]::UTF8.GetBytes($json)
    return [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes)).ToLowerInvariant()
}
function Get-PaperBatchHostFailureOutput([System.Diagnostics.Process]$process) {
    $details = [System.Collections.Generic.List[string]]::new()
    foreach ($entry in @(
        @{ label = 'stderr'; path = $process.PaperBatchStderrPath },
        @{ label = 'stdout'; path = $process.PaperBatchStdoutPath }
    )) {
        $path = $entry.path
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { continue }
        $stream = [IO.File]::Open($path, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::ReadWrite)
        try {
            $count = [int][Math]::Min(1900, $stream.Length)
            if ($count -le 0) { continue }
            [void]$stream.Seek(-$count, [IO.SeekOrigin]::End)
            $bytes = [byte[]]::new($count)
            [void]$stream.Read($bytes, 0, $count)
            $output = [Text.Encoding]::UTF8.GetString($bytes).Trim()
            if (-not [string]::IsNullOrWhiteSpace($output)) { $details.Add("[$($entry.label) tail] $output") }
        } finally { $stream.Dispose() }
    }
    return ($details -join "`n")
}
function Start-PaperBatchHost([string]$packageRoot) {
    $exe = Join-Path $packageRoot 'app/PaperBatch.Desktop.exe'
    if (-not (Test-Path -LiteralPath $exe -PathType Leaf)) { throw "Packaged host missing: $exe" }
    $logPrefix = Join-Path $temp "coordinator-$([Guid]::NewGuid().ToString('N'))"
    $stdout = "$logPrefix.stdout"
    $stderr = "$logPrefix.stderr"
    $proc = Start-Process -FilePath $exe -ArgumentList '--coordinator-host' -WorkingDirectory (Split-Path -Parent $exe) -PassThru -WindowStyle Hidden -RedirectStandardOutput $stdout -RedirectStandardError $stderr
    $proc | Add-Member -NotePropertyName PaperBatchStdoutPath -NotePropertyValue $stdout
    $proc | Add-Member -NotePropertyName PaperBatchStderrPath -NotePropertyValue $stderr
    $sid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
    $pipeInput = [Text.Encoding]::UTF8.GetBytes("PaperBatch`n$sid")
    $pipeDigest = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($pipeInput)).ToLowerInvariant().Substring(0, 24)
    $pipeName = "paperbatch-$pipeDigest"
    $deadline = [DateTime]::UtcNow.AddSeconds(10)
    $ready = $false
    try {
        while ([DateTime]::UtcNow -lt $deadline) {
            if ($proc.HasExited) {
                $details = Get-PaperBatchHostFailureOutput $proc
                throw "Packaged coordinator exited during startup ($($proc.ExitCode)). Output: $details"
            }
            $owned = Get-CimInstance Win32_Process -Filter "ProcessId = $($proc.Id)"
            if (-not $owned -or [string]::IsNullOrWhiteSpace($owned.ExecutablePath)) { throw 'Coordinator process ownership could not be verified.' }
            if ([IO.Path]::GetFullPath($owned.ExecutablePath) -ne [IO.Path]::GetFullPath($exe)) { throw 'Coordinator process executable does not match the staged package.' }
            $probe = [System.IO.Pipes.NamedPipeClientStream]::new('.', $pipeName, [System.IO.Pipes.PipeDirection]::InOut)
            try { $probe.Connect(200); $ready = $true; return $proc } catch { Start-Sleep -Milliseconds 100 } finally { $probe.Dispose() }
        }
        throw 'Packaged coordinator pipe did not become ready within ten seconds.'
    } finally {
        if (-not $ready -and -not $proc.HasExited) { Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue }
    }
}
function Stop-PaperBatchHost([System.Diagnostics.Process]$process) {
    if ($null -eq $process) { return }
    $process.Refresh()
    if ($process.HasExited) {
        $details = Get-PaperBatchHostFailureOutput $process
        throw "Packaged coordinator exited before cleanup was requested (code $($process.ExitCode)). Output: $details"
    }
    try { Stop-Process -Id $process.Id -Force -ErrorAction Stop }
    catch {
        $process.Refresh()
        if (-not $process.HasExited) { throw }
        if ($process.ExitCode -ne 0) {
            $details = Get-PaperBatchHostFailureOutput $process
            throw "Packaged coordinator exited during cleanup with code $($process.ExitCode). Output: $details"
        }
    }
    if (-not $process.WaitForExit(5000)) { throw 'Packaged coordinator did not stop within five seconds.' }
}
function Invoke-Doctor([string]$command) {
    $project = Join-Path $repoRoot 'src/PaperBatch.Cli/PaperBatch.Cli.csproj'
    $stdout = Join-Path $temp "cli-$command.stdout"
    $stderr = Join-Path $temp "cli-$command.stderr"
    $process = Start-Process -FilePath 'dotnet' -ArgumentList @('run', '--no-restore', '--project', "`"$project`"", '--', $command) -WorkingDirectory $repoRoot -PassThru -NoNewWindow -RedirectStandardOutput $stdout -RedirectStandardError $stderr
    $deadline = [DateTime]::UtcNow.AddSeconds(30)
    while (-not $process.HasExited -and [DateTime]::UtcNow -lt $deadline) { Start-Sleep -Milliseconds 100 }
    if (-not $process.HasExited) { Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue; throw "CLI $command exceeded its 30-second ceiling." }
    if ($process.ExitCode -ne 0) { throw "CLI $command failed with exit code $($process.ExitCode)." }
    if ($command -eq 'doctor') {
        $json = Get-Content -Raw -LiteralPath $stdout | ConvertFrom-Json
        if ($json.coordinator -ne 'connected') { throw 'CLI doctor did not connect to the packaged coordinator.' }
    }
}
try {
    foreach ($path in @($local, $roaming, $sandboxHome, $temp)) { New-Item -ItemType Directory -Path $path -Force | Out-Null }
    $env:PAPERBATCH_LOCAL_APP_DATA = $local
    $env:LOCALAPPDATA = $local
    $env:APPDATA = $roaming
    $env:USERPROFILE = $sandboxHome
    $env:TEMP = $temp
    $env:TMP = $temp
    if ([IO.Path]::GetFullPath($env:PAPERBATCH_LOCAL_APP_DATA) -ne [IO.Path]::GetFullPath($local)) { throw 'PaperBatch app-data override is not isolated.' }
    $beforePrinters = Get-PrinterSnapshot
    $beforeRegistry = Get-PaperBatchRegistrySnapshot
    $beforeManifest = (Get-FileHash -LiteralPath (Join-Path $PackageDirectory 'package-manifest.json') -Algorithm SHA256).Hash.ToLowerInvariant()
    $packageA = Join-Path $sessionRoot 'package-a'
    $packageB = Join-Path $sessionRoot 'package-b'
    Expand-Archive -LiteralPath $PackageArchive -DestinationPath $packageA
    Expand-Archive -LiteralPath $PackageArchive -DestinationPath $packageB
    $hostExe = (Resolve-Path (Join-Path $packageA 'app/PaperBatch.Desktop.exe')).Path
    $hostExeB = (Resolve-Path (Join-Path $packageB 'app/PaperBatch.Desktop.exe')).Path
    foreach ($program in @($hostExe, $hostExeB)) {
        $ruleName = "PaperBatch-U9-Offline-$($blockedRules.Count)-$PID"
        New-NetFirewallRule -DisplayName $ruleName -Direction Outbound -Program $program -Action Block -Profile Any | Out-Null
        $blockedRules += $ruleName
    }
    $restoreOut = Join-Path $temp 'restore.stdout'
    $restoreErr = Join-Path $temp 'restore.stderr'
    $restore = Start-Process -FilePath 'dotnet' -ArgumentList @('restore', (Join-Path $repoRoot 'src/PaperBatch.Cli/PaperBatch.Cli.csproj'), '-p:NuGetAudit=false') -WorkingDirectory $repoRoot -PassThru -NoNewWindow -RedirectStandardOutput $restoreOut -RedirectStandardError $restoreErr
    $restoreDeadline = [DateTime]::UtcNow.AddSeconds(120)
    while (-not $restore.HasExited -and [DateTime]::UtcNow -lt $restoreDeadline) { Start-Sleep -Milliseconds 200 }
    if (-not $restore.HasExited) { Stop-Process -Id $restore.Id -Force -ErrorAction SilentlyContinue; throw 'CLI locked restore exceeded its 120-second ceiling.' }
    if ($restore.ExitCode -ne 0) { throw "CLI locked restore failed with exit code $($restore.ExitCode)." }
    Invoke-Doctor 'help'
    $scenario.assertions += 'cli-help-passed'
    # Same package bytes are used for two trees; this is continuity, not migration.
    $hostProcess = Start-PaperBatchHost $packageA
    Invoke-Doctor 'doctor'
    $scenario.assertions += 'packaged-coordinator-and-cli-doctor-passed'
    $dataRoot = Join-Path $local 'PaperBatch'
    $marker = Join-Path $dataRoot 'u9-swap-marker.txt'
    Set-Content -LiteralPath $marker -Value 'fixture-survives-package-tree-switch' -Encoding utf8
    Stop-PaperBatchHost $hostProcess; $hostProcess = $null
    $hostProcess = Start-PaperBatchHost $packageB
    Invoke-Doctor 'doctor'
    if ((Get-Content -Raw -LiteralPath $marker).Trim() -ne 'fixture-survives-package-tree-switch') { throw 'User-data fixture did not survive package-tree switch.' }
    Stop-PaperBatchHost $hostProcess; $hostProcess = $null
    $hostProcess = Start-PaperBatchHost $packageA
    Invoke-Doctor 'doctor'
    if ((Get-Content -Raw -LiteralPath $marker).Trim() -ne 'fixture-survives-package-tree-switch') { throw 'User-data fixture did not survive package-tree rollback.' }
    Stop-PaperBatchHost $hostProcess; $hostProcess = $null
    if (-not (Test-Path -LiteralPath $dataRoot) -or -not (Test-Path -LiteralPath (Join-Path $dataRoot 'CoordinatorLocks'))) { throw 'Coordinator did not use isolated PaperBatch app data.' }
    $sessionFiles = @(Get-ChildItem -LiteralPath $sessionRoot -File -Recurse | ForEach-Object { [IO.Path]::GetFullPath($_.FullName) })
    foreach ($file in $sessionFiles) { if (-not $file.StartsWith([IO.Path]::GetFullPath($sessionRoot) + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Session file escaped the task-owned root.' } }
    $scenario.assertions += 'task-owned-session-file-boundary-passed'
    $scenario.status = 'passed'
} finally {
    if ($hostProcess) {
        try { Stop-PaperBatchHost $hostProcess }
        catch { $scenario.status = 'failed'; $scenario.assertions += 'coordinator-process-cleanup-failed' }
    }
    foreach ($rule in $blockedRules) {
        try { Remove-NetFirewallRule -DisplayName $rule -ErrorAction Stop }
        catch { $scenario.status = 'failed'; $scenario.assertions += 'firewall-rule-cleanup-failed' }
    }
    try {
        $afterPrinters = Get-PrinterSnapshot
        if ($null -eq $beforePrinters -or (Get-SnapshotDigest $beforePrinters) -ne (Get-SnapshotDigest $afterPrinters)) { $scenario.status = 'failed'; $scenario.assertions += 'printer-inventory-changed-or-unavailable' }
        else { $scenario.assertions += 'printer-inventory-unchanged' }
    } catch { $scenario.status = 'failed'; $scenario.assertions += 'printer-snapshot-unavailable' }
    try {
        $afterRegistry = Get-PaperBatchRegistrySnapshot
        if ($null -eq $beforeRegistry -or (Get-SnapshotDigest $beforeRegistry) -ne (Get-SnapshotDigest $afterRegistry)) { $scenario.status = 'failed'; $scenario.assertions += 'selected-registry-keys-changed-or-unavailable' }
        else { $scenario.assertions += 'selected-registry-keys-unchanged' }
    } catch { $scenario.status = 'failed'; $scenario.assertions += 'selected-registry-snapshot-unavailable' }
    try {
        $afterManifest = (Get-FileHash -LiteralPath (Join-Path $PackageDirectory 'package-manifest.json') -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($null -eq $beforeManifest -or $beforeManifest -ne $afterManifest) { $scenario.status = 'failed'; $scenario.assertions += 'package-manifest-changed-or-unavailable' }
    } catch { $scenario.status = 'failed'; $scenario.assertions += 'package-manifest-snapshot-unavailable' }
    try { Remove-Item -LiteralPath $sessionRoot -Recurse -Force -ErrorAction Stop }
    catch { $scenario.status = 'failed'; $scenario.assertions += 'session-root-cleanup-failed' }
    $beforePrinterHash = if ($null -ne $beforePrinters) { Get-SnapshotDigest $beforePrinters } else { $null }
    $afterPrinterHash = if ($null -ne $afterPrinters) { Get-SnapshotDigest $afterPrinters } else { $null }
    $printerObservationStatus = if ($null -eq $beforePrinterHash -or $null -eq $afterPrinterHash) { 'blocked' } elseif ($beforePrinterHash -eq $afterPrinterHash) { 'passed' } else { 'failed' }
    $beforeRegistryHash = if ($null -ne $beforeRegistry) { Get-SnapshotDigest $beforeRegistry } else { $null }
    $afterRegistryHash = if ($null -ne $afterRegistry) { Get-SnapshotDigest $afterRegistry } else { $null }
    $registryObservationStatus = if ($null -eq $beforeRegistryHash -or $null -eq $afterRegistryHash) { 'blocked' } elseif ($beforeRegistryHash -eq $afterRegistryHash) { 'passed' } else { 'failed' }
    $sourceCommit = $env:PB_SOURCE_COMMIT
    if ([string]::IsNullOrWhiteSpace($sourceCommit)) { $sourceCommit = (& git -C $repoRoot rev-parse HEAD).Trim() }
    $runIdentity = $null
    if ($env:GITHUB_WORKFLOW -and $env:GITHUB_RUN_ID -and $env:GITHUB_RUN_ATTEMPT) {
        $runIdentity = [ordered]@{ workflow = $env:GITHUB_WORKFLOW; runId = $env:GITHUB_RUN_ID; runAttempt = $env:GITHUB_RUN_ATTEMPT }
    }
    $report = [ordered]@{
        schemaVersion = 1; commit = $sourceCommit; runIdentity = $runIdentity
        runner = [ordered]@{ os = [string](Get-CimInstance Win32_OperatingSystem).Caption; image = $env:ImageOS; name = $env:RUNNER_NAME; powershell = $PSVersionTable.PSVersion.ToString() }
        packageManifestSha256 = $beforeManifest; generatedAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
        packageTreeComparison = [ordered]@{
            status = 'partial'
            distinctArtifacts = $false
            trees = @(
                [ordered]@{ id = 'package-a'; sourceRevision = (Get-Content -Raw -LiteralPath (Join-Path $PackageDirectory 'package-manifest.json') | ConvertFrom-Json).sourceRevision; packageManifestSha256 = $beforeManifest; archiveSha256 = (Get-FileHash -LiteralPath $PackageArchive -Algorithm SHA256).Hash.ToLowerInvariant() },
                [ordered]@{ id = 'package-b'; sourceRevision = (Get-Content -Raw -LiteralPath (Join-Path $PackageDirectory 'package-manifest.json') | ConvertFrom-Json).sourceRevision; packageManifestSha256 = $beforeManifest; archiveSha256 = (Get-FileHash -LiteralPath $PackageArchive -Algorithm SHA256).Hash.ToLowerInvariant() }
            )
            reason = 'Both trees are extracted from the same current package archive; no historical release artifact or supported package upgrade lifecycle is available.'
        }
        schemaMigration = [ordered]@{
            evidence = 'managed-compatibility-tests'
            tests = @('SQLite migrations are idempotent and preserve v1 records', 'SQLite migration failure rolls back and never resubmits legacy jobs')
            boundary = 'SQLite schema semantics only; no previous application version, installer/updater, or product-level rollback lifecycle is exercised.'
        }
        observations = [ordered]@{
            printers = [ordered]@{ status = $printerObservationStatus; beforeSha256 = $beforePrinterHash; afterSha256 = $afterPrinterHash; beforeCount = if ($null -ne $beforePrinters) { $beforePrinters.Count } else { $null }; afterCount = if ($null -ne $afterPrinters) { $afterPrinters.Count } else { $null } }
            registry = [ordered]@{ status = $registryObservationStatus; beforeSha256 = $beforeRegistryHash; afterSha256 = $afterRegistryHash; selectedKeyCount = 3 }
        }
        scenarios = @($scenario); evidenceLimits = @('Printer and registry are bounded before/after snapshots, not system-wide auditing.', 'No print job or physical output was attempted.', 'The CLI executable is built from the checked-out source; it connects to the packaged coordinator.', 'Temporary state is removed after report creation.')
    }
    [IO.File]::WriteAllText($reportPath, ($report | ConvertTo-Json -Depth 9) + [Environment]::NewLine, [Text.UTF8Encoding]::new($false))
}
if ($scenario.status -ne 'passed') { throw 'Windows package session assertions failed; see bounded JSON report.' }
Write-Output 'Windows package session passed with isolated app data, outbound-denied coordinator, and bounded read-only host snapshots.'
