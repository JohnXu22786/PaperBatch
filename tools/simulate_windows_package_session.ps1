[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$PackageDirectory,
    [Parameter(Mandatory = $true)][string]$PackageArchive
)
$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$sessionRoot = Join-Path $env:RUNNER_TEMP 'paperbatch-u9-session'
if (-not (Test-Path -LiteralPath $PackageDirectory -PathType Container)) { throw 'Package directory is missing.' }
if (Test-Path -LiteralPath $sessionRoot) { throw "Session path already exists: $sessionRoot" }
New-Item -ItemType Directory -Path $sessionRoot | Out-Null
$local = Join-Path $sessionRoot 'profile/LocalAppData'
$roaming = Join-Path $sessionRoot 'profile/RoamingAppData'
$sandboxHome = Join-Path $sessionRoot 'profile/Home'
$temp = Join-Path $sessionRoot 'temp'
foreach ($path in @($local, $roaming, $sandboxHome, $temp)) { New-Item -ItemType Directory -Path $path -Force | Out-Null }
$env:PAPERBATCH_LOCAL_APP_DATA = $local
$env:LOCALAPPDATA = $local
$env:APPDATA = $roaming
$env:USERPROFILE = $sandboxHome
$env:TEMP = $temp
$env:TMP = $temp
if ([IO.Path]::GetFullPath($env:PAPERBATCH_LOCAL_APP_DATA) -ne [IO.Path]::GetFullPath($local)) { throw 'PaperBatch app-data override is not isolated.' }
$beforeDefault = Get-CimInstance Win32_Printer | Where-Object Default | Select-Object -First 1 -ExpandProperty Name
$blockedRules = @()
$hostProcess = $null
function Start-PaperBatchHost([string]$packageRoot) {
    $exe = Join-Path $packageRoot 'app/PaperBatch.Desktop.exe'
    if (-not (Test-Path -LiteralPath $exe -PathType Leaf)) { throw "Packaged host missing: $exe" }
    $proc = Start-Process -FilePath $exe -ArgumentList '--coordinator-host' -WorkingDirectory (Split-Path -Parent $exe) -PassThru -WindowStyle Hidden
    $sid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
    $pipeInput = [Text.Encoding]::UTF8.GetBytes("PaperBatch`n$sid")
    $pipeDigest = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($pipeInput)).ToLowerInvariant().Substring(0, 24)
    $pipeName = "paperbatch-$pipeDigest"
    $deadline = [DateTime]::UtcNow.AddSeconds(10)
    while ([DateTime]::UtcNow -lt $deadline) {
        if ($proc.HasExited) { throw "Packaged coordinator exited during startup ($($proc.ExitCode))." }
        $probe = [System.IO.Pipes.NamedPipeClientStream]::new('.', $pipeName, [System.IO.Pipes.PipeDirection]::InOut)
        try {
            $probe.Connect(200)
            return $proc
        } catch {
            Start-Sleep -Milliseconds 100
        } finally {
            $probe.Dispose()
        }
    }
    throw 'Packaged coordinator pipe did not become ready within ten seconds.'
}
function Invoke-Doctor {
    $result = & dotnet run --no-restore --project (Join-Path $repoRoot 'src/PaperBatch.Cli/PaperBatch.Cli.csproj') -- doctor
    if ($LASTEXITCODE -ne 0) { throw "CLI doctor failed with exit code $LASTEXITCODE." }
    $json = ($result -join "`n") | ConvertFrom-Json
    if ($json.coordinator -ne 'connected') { throw 'CLI doctor did not connect to the packaged coordinator.' }
}
try {
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
    dotnet restore (Join-Path $repoRoot 'src/PaperBatch.Cli/PaperBatch.Cli.csproj') -p:NuGetAudit=false
    if ($LASTEXITCODE -ne 0) { throw 'CLI locked restore failed.' }

    # The same reviewed package bytes are staged in independent trees because no
    # historical product package/updater lifecycle exists. This checks startup
    # and user-data continuity across a directory swap, not version migration.
    $hostProcess = Start-PaperBatchHost $packageA
    Invoke-Doctor
    $dataRoot = Join-Path $local 'PaperBatch'
    $marker = Join-Path $dataRoot 'u9-swap-marker.txt'
    Set-Content -LiteralPath $marker -Value 'fixture-survives-package-tree-switch' -Encoding utf8
    Stop-Process -Id $hostProcess.Id -Force
    $hostProcess = Start-PaperBatchHost $packageB
    Invoke-Doctor
    if ((Get-Content -Raw -LiteralPath $marker).Trim() -ne 'fixture-survives-package-tree-switch') { throw 'User-data fixture did not survive package-tree switch.' }
    Stop-Process -Id $hostProcess.Id -Force
    $hostProcess = Start-PaperBatchHost $packageA
    Invoke-Doctor
    Stop-Process -Id $hostProcess.Id -Force
    $hostProcess = $null

    if (-not (Test-Path -LiteralPath $dataRoot)) { throw 'Coordinator did not create state under the isolated profile.' }
    if (-not (Test-Path -LiteralPath (Join-Path $dataRoot 'CoordinatorLocks'))) { throw 'Coordinator did not use the isolated PaperBatch app-data override.' }
    $afterDefault = Get-CimInstance Win32_Printer | Where-Object Default | Select-Object -First 1 -ExpandProperty Name
    if ($beforeDefault -ne $afterDefault) { throw 'The default printer changed during the smoke test.' }
    Write-Output 'Q2/Q3 simulation passed: isolated-profile coordinator launch and CLI doctor succeeded offline across package-tree swap/rollback.'
    Write-Output 'Evidence is an ephemeral hosted Windows runner simulation; package trees contain the same build and no product-version migration is claimed.'
}
finally {
    if ($hostProcess -and -not $hostProcess.HasExited) { Stop-Process -Id $hostProcess.Id -Force -ErrorAction SilentlyContinue }
    foreach ($rule in $blockedRules) { Remove-NetFirewallRule -DisplayName $rule -ErrorAction SilentlyContinue }
}
