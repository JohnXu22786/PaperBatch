$ErrorActionPreference = 'Stop'

function Get-InspectionFailureStatus($exception) {
    if ($exception -is [System.UnauthorizedAccessException] -or
        $exception -is [System.Security.SecurityException] -or
        $exception.HResult -eq -2147024891 -or
        $exception.Message -match '(?i)access denied|access is denied|permission denied|not authorized') {
        return 'Blocked'
    }

    return 'Unavailable'
}

$service = $null
$serviceStatus = 'Unavailable'
$serviceError = $null
try {
    $service = Get-Service -Name Spooler -ErrorAction Stop
    $serviceStatus = $service.Status.ToString()
} catch {
    $serviceStatus = Get-InspectionFailureStatus $_.Exception
    $serviceError = $_.Exception.Message
}

$queues = @()
$queueStatus = 'Unavailable'
$queueError = $null
try {
    $queues = @(Get-Printer -ErrorAction Stop | ForEach-Object {
        [ordered]@{
            name = $_.Name
            driverName = $_.DriverName
            portName = $_.PortName
            type = [string]$_.Type
            shared = [bool]$_.Shared
            published = [bool]$_.Published
            printerStatus = [string]$_.PrinterStatus
        }
    })
    $queueStatus = if ($queues.Count -eq 0) { 'Unavailable' } else { 'Verified' }
} catch {
    $queueStatus = Get-InspectionFailureStatus $_.Exception
    $queueError = $_.Exception.Message
}

$os = $null
$osStatus = 'Unavailable'
$osError = $null
try {
    $os = Get-CimInstance -ClassName Win32_OperatingSystem -ErrorAction Stop
    $osStatus = 'Verified'
} catch {
    $osStatus = Get-InspectionFailureStatus $_.Exception
    $osError = $_.Exception.Message
}
$sessionId = [System.Diagnostics.Process]::GetCurrentProcess().SessionId
$sessionName = [string]$env:SESSIONNAME
$sessionType = 'Unavailable'
$sessionStatus = 'Unavailable'
if ($sessionName -eq 'Services') {
    $sessionType = 'service'
    $sessionStatus = 'Verified'
} elseif ($sessionName -eq 'Console' -or $sessionName -match '^(RDP|ICA)-Tcp#\d+$') {
    $sessionType = 'interactive'
    $sessionStatus = 'Verified'
}
$evidence = [ordered]@{
    schemaVersion = '1.0'
    generatedAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
    runner = [ordered]@{
        osStatus = if ($null -ne $os) { 'Verified' } else { $osStatus }
        os = if ($null -ne $os) { $os.Caption } else { $null }
        version = if ($null -ne $os) { $os.Version } else { $null }
        build = if ($null -ne $os) { $os.BuildNumber } else { $null }
        osError = $osError
        osArchitecture = if ($null -ne $os) { $os.OSArchitecture } else { $null }
        runnerArchitecture = if ([string]::IsNullOrWhiteSpace($env:RUNNER_ARCH)) { $null } else { $env:RUNNER_ARCH }
        processArchitecture = $env:PROCESSOR_ARCHITECTURE
        powershellVersion = $PSVersionTable.PSVersion.ToString()
        sessionStatus = $sessionStatus
        processSessionId = $sessionId
        sessionName = if ([string]::IsNullOrWhiteSpace($sessionName)) { $null } else { $sessionName }
        sessionType = $sessionType
    }
    spooler = [ordered]@{
        status = $serviceStatus
        startType = if ($null -ne $service) { [string]$service.StartType } else { $null }
        error = $serviceError
    }
    queues = [ordered]@{
        status = $queueStatus
        items = $queues
        error = $queueError
    }
    gdiSubmission = [ordered]@{
        status = 'Unavailable'
        method = 'safety policy'
        limitation = 'No test page was submitted because the runner queue has not been verified as both noninteractive and local-only. Built-in file-output queues can prompt for a path; other routes may contact an external device.'
        jobId = $null
        outputArtifact = $null
        physicalPaper = $false
    }
}

$json = $evidence | ConvertTo-Json -Depth 8
[System.IO.File]::WriteAllText((Join-Path $PWD 'windows-print-runner-evidence.json'), $json + [Environment]::NewLine, [System.Text.UTF8Encoding]::new($false))
