$ErrorActionPreference = 'Stop'
if ($env:GITHUB_ACTIONS -ne 'true' -or $env:RUNNER_ENVIRONMENT -ne 'github-hosted' -or $env:RUNNER_OS -ne 'Windows') {
  throw 'LibreOffice installation is restricted to hosted Windows CI.'
}
$version = '26.2.2.2'
$fingerprint = 'C2839ECAD9408FBE9531C3E9F434A1EFAFEEAEA3'
$base = "https://downloadarchive.documentfoundation.org/libreoffice/old/$version/win/x86_64"
$packageName = "LibreOffice_${version}_Win_x86-64.msi"
$package = Join-Path $env:RUNNER_TEMP $packageName
$signature = "$package.asc"
$key = Join-Path $env:RUNNER_TEMP 'tdf-release-key.asc'
$keyringHome = Join-Path $env:RUNNER_TEMP 'paperbatch-gpg-home'
$keyringName = 'tdf-release-key.gpg'
$keyring = Join-Path $keyringHome $keyringName
$status = Join-Path $env:RUNNER_TEMP 'tdf-signature-status.txt'

$gpg = (Get-Command gpg -ErrorAction SilentlyContinue).Source
if (-not $gpg) {
  $gitGpg = Join-Path ${env:ProgramFiles} 'Git\usr\bin\gpg.exe'
  if (Test-Path -LiteralPath $gitGpg -PathType Leaf) { $gpg = $gitGpg }
}
if (-not $gpg) { throw 'A GPG verifier is unavailable on this hosted Windows runner.' }
$gpgv = Join-Path (Split-Path $gpg) 'gpgv.exe'
if (-not (Test-Path -LiteralPath $gpgv -PathType Leaf)) { throw 'The matching gpgv verifier is unavailable.' }
$cygpath = Join-Path (Split-Path $gpg) 'cygpath.exe'
if (-not (Test-Path -LiteralPath $cygpath -PathType Leaf)) { throw 'The path converter matching the Git-for-Windows GPG tools is unavailable.' }

Invoke-WebRequest -Uri "$base/$packageName" -OutFile $package
Invoke-WebRequest -Uri "$base/$packageName.asc" -OutFile $signature
Invoke-WebRequest -Uri 'https://keyserver.ubuntu.com/pks/lookup?op=get&search=0xF434A1EFAFEEAEA3' -OutFile $key
$keyListing = & $gpg --batch --show-keys --with-colons $key
if ($LASTEXITCODE -ne 0 -or -not ($keyListing -match "fpr:::::::::${fingerprint}:")) { throw 'The downloaded Document Foundation signing key fingerprint is invalid.' }
New-Item -ItemType Directory -Path $keyringHome -Force | Out-Null
& $gpg --batch --yes --dearmor --output $keyring $key
if ($LASTEXITCODE -ne 0) { throw 'Could not prepare the pinned Document Foundation signing key.' }
$keyringHomeForGpg = (& $cygpath -u $keyringHome | Out-String).Trim()
$keyringHomeConversionExitCode = $LASTEXITCODE
if ($keyringHomeConversionExitCode -ne 0 -or $keyringHomeForGpg -notmatch '^/(?:[A-Za-z]|cygdrive/[A-Za-z])/[^:\r\n]*$') {
  throw 'Could not convert the private GnuPG home to an absolute MSYS path.'
}
$verification = @(& $gpgv --homedir $keyringHomeForGpg --status-fd=1 --keyring $keyringName $signature $package 2>&1 | ForEach-Object { [string]$_ })
$verificationExitCode = $LASTEXITCODE
$verificationText = $verification -join [Environment]::NewLine
$verificationText | Set-Content -Encoding utf8 $status
$validSignatures = @($verification | Where-Object { $_ -match '^\[GNUPG:\] VALIDSIG ([0-9A-Fa-f]+) ' })
$pinnedSignature = @($validSignatures | Where-Object { $_ -match "^\[GNUPG:\] VALIDSIG $fingerprint " })
if ($verificationExitCode -ne 0) {
  $details = (@($verification | Where-Object { $_ -match '^\[GNUPG:\] (ERRSIG|NO_PUBKEY|BADSIG|FAILURE|VALIDSIG)\b|^gpgv:' } | Select-Object -First 4) -join ' | ')
  if ($details.Length -gt 600) { $details = $details.Substring(0, 600) }
  throw "LibreOffice MSI signature verifier exited with code $verificationExitCode; expected signing key $fingerprint. Details: $details"
}
if ($pinnedSignature.Count -eq 0) {
  $observed = @($validSignatures | ForEach-Object { if ($_ -match '^\[GNUPG:\] VALIDSIG ([0-9A-Fa-f]+) ') { $Matches[1] } } | Select-Object -Unique)
  $observedText = if ($observed.Count -gt 0) { $observed -join ', ' } else { 'no VALIDSIG status record' }
  if ($observedText.Length -gt 200) { $observedText = $observedText.Substring(0, 200) }
  throw "LibreOffice MSI signature identity mismatch; expected $fingerprint, observed $observedText (gpgv exit code 0)."
}

$installRoot = Join-Path $env:ProgramFiles "PaperBatchLibreOffice-$version"
$installOutput = Start-Process -FilePath 'msiexec.exe' -ArgumentList "/i `"$package`" /qn /norestart INSTALLLOCATION=`"$installRoot`"" -Wait -PassThru
if ($installOutput.ExitCode -notin @(0, 3010)) { throw "Verified LibreOffice MSI installation failed with exit code $($installOutput.ExitCode)." }
$engine = Join-Path $installRoot 'program\soffice.bin'
$launcher = Join-Path $installRoot 'program\soffice.com'
if (-not (Test-Path -LiteralPath $engine -PathType Leaf) -or -not (Test-Path -LiteralPath $launcher -PathType Leaf)) {
  throw 'The verified LibreOffice installation did not contain program\soffice.bin and program\soffice.com.'
}
$versionOutput = (& $launcher --version | Out-String).Trim()
$versionPattern = '^LibreOffice\s+' + [regex]::Escape($version) + '(?=\s|$)'
if ($LASTEXITCODE -ne 0 -or $versionOutput -notmatch $versionPattern) { throw 'Installed LibreOffice version did not match the pinned version.' }
$engineHash = (Get-FileHash -LiteralPath $engine -Algorithm SHA256).Hash.ToLowerInvariant()
$packageHash = (Get-FileHash -LiteralPath $package -Algorithm SHA256).Hash.ToLowerInvariant()
$provenancePath = Join-Path $env:RUNNER_TEMP 'paperbatch-lo-installer-provenance.json'
$provenance = @{
  version = $version
  source = 'Document Foundation official archive'
  sourceUrl = "$base/$packageName"
  package = $packageName
  packageSha256 = $packageHash
  integrityMethod = 'OpenPGP detached signature verified before MSI install'
  signingKeyFingerprint = $fingerprint
} | ConvertTo-Json -Compress
[System.IO.File]::WriteAllText($provenancePath, $provenance, [System.Text.UTF8Encoding]::new($false))
@(
  "PAPERBATCH_LO_ENGINE_PATH=$engine"
  "PAPERBATCH_LO_ENGINE_SHA256=$engineHash"
  "PAPERBATCH_LO_EXPECTED_VERSION=LibreOffice $version"
  "PAPERBATCH_LO_INSTALLER_PROVENANCE=$provenancePath"
) | Add-Content -Encoding utf8 $env:GITHUB_ENV
Write-Host "Verified LibreOffice $version installer signature and installed engine identity."
