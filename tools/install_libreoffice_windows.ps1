$ErrorActionPreference = 'Stop'
$version = '26.2.2.2'
$fingerprint = 'C2839ECAD9408FBE9531C3E9F434A1EFAFEEAEA3'
$base = "https://downloadarchive.documentfoundation.org/libreoffice/old/$version/win/x86_64"
$packageName = "LibreOffice_${version}_Win_x86-64.msi"
$package = Join-Path $env:RUNNER_TEMP $packageName
$signature = "$package.asc"
$key = Join-Path $env:RUNNER_TEMP 'tdf-release-key.asc'
$keyring = Join-Path $env:RUNNER_TEMP 'tdf-release-key.gpg'
$status = Join-Path $env:RUNNER_TEMP 'tdf-signature-status.txt'

$gpg = (Get-Command gpg -ErrorAction SilentlyContinue).Source
if (-not $gpg) {
  $gitGpg = Join-Path ${env:ProgramFiles} 'Git\usr\bin\gpg.exe'
  if (Test-Path -LiteralPath $gitGpg -PathType Leaf) { $gpg = $gitGpg }
}
if (-not $gpg) { throw 'A GPG verifier is unavailable on this hosted Windows runner.' }
$gpgv = Join-Path (Split-Path $gpg) 'gpgv.exe'
if (-not (Test-Path -LiteralPath $gpgv -PathType Leaf)) { throw 'The matching gpgv verifier is unavailable.' }

Invoke-WebRequest -Uri "$base/$packageName" -OutFile $package
Invoke-WebRequest -Uri "$base/$packageName.asc" -OutFile $signature
Invoke-WebRequest -Uri 'https://keyserver.ubuntu.com/pks/lookup?op=get&search=0xF434A1EFAFEEAEA3' -OutFile $key
$keyListing = & $gpg --batch --show-keys --with-colons $key
if ($LASTEXITCODE -ne 0 -or -not ($keyListing -match "fpr:::::::::${fingerprint}:")) { throw 'The downloaded Document Foundation signing key fingerprint is invalid.' }
& $gpg --batch --yes --dearmor --output $keyring $key
if ($LASTEXITCODE -ne 0) { throw 'Could not prepare the pinned Document Foundation signing key.' }
$verification = & $gpgv --status-fd=1 --keyring $keyring $signature $package 2>&1
$verificationExitCode = $LASTEXITCODE
$verification | Set-Content -Encoding utf8 $status
if ($verificationExitCode -ne 0 -or -not (Select-String -LiteralPath $status -SimpleMatch "[GNUPG:] VALIDSIG $fingerprint ")) {
  throw 'LibreOffice MSI signature verification failed.'
}

$installRoot = Join-Path $env:RUNNER_TEMP "paperbatch-libreoffice-$version"
$installOutput = Start-Process -FilePath 'msiexec.exe' -ArgumentList "/i `"$package`" /qn /norestart INSTALLLOCATION=`"$installRoot`"" -Wait -PassThru
if ($installOutput.ExitCode -notin @(0, 3010)) { throw "Verified LibreOffice MSI installation failed with exit code $($installOutput.ExitCode)." }
$engine = Join-Path $installRoot 'program\soffice.com'
if (-not (Test-Path -LiteralPath $engine -PathType Leaf)) { throw 'The verified LibreOffice installation did not contain program\soffice.com.' }
$versionOutput = (& $engine --version | Out-String).Trim()
if ($LASTEXITCODE -ne 0 -or -not $versionOutput.StartsWith("LibreOffice $version")) { throw 'Installed LibreOffice version did not match the pinned version.' }
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
