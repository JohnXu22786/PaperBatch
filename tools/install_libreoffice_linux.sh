#!/usr/bin/env bash
set -euo pipefail

version='26.2.2.2'
fingerprint='C2839ECAD9408FBE9531C3E9F434A1EFAFEEAEA3'
architecture='x86-64'
package_name="LibreOffice_${version}_Linux_${architecture}_deb.tar.gz"
base="https://downloadarchive.documentfoundation.org/libreoffice/old/${version}/deb/x86_64"
package="$RUNNER_TEMP/$package_name"
signature="$package.asc"
key="$RUNNER_TEMP/tdf-release-key.asc"
keyring="$RUNNER_TEMP/tdf-release-key.gpg"
status="$RUNNER_TEMP/tdf-signature-status.txt"
extract="$RUNNER_TEMP/paperbatch-lo-deb"
provenance="$RUNNER_TEMP/paperbatch-lo-installer-provenance.json"

[[ "${GITHUB_ACTIONS:-}" == 'true' && "${RUNNER_ENVIRONMENT:-}" == 'github-hosted' && "${RUNNER_OS:-}" == 'Linux' ]] || { echo 'LibreOffice installation is restricted to hosted Linux CI.' >&2; exit 1; }
command -v gpg >/dev/null 2>&1 || { echo 'A GPG verifier is unavailable on this hosted Linux runner.' >&2; exit 1; }
command -v gpgv >/dev/null 2>&1 || { echo 'The matching gpgv verifier is unavailable.' >&2; exit 1; }
curl --fail --location --retry 3 "$base/$package_name" --output "$package"
curl --fail --location --retry 3 "$base/$package_name.asc" --output "$signature"
curl --fail --location --retry 3 'https://keyserver.ubuntu.com/pks/lookup?op=get&search=0xF434A1EFAFEEAEA3' --output "$key"
gpg --batch --show-keys --with-colons "$key" | grep -F "fpr:::::::::${fingerprint}:"
gpg --batch --dearmor --output "$keyring" "$key"
gpgv --status-fd=1 --keyring "$keyring" "$signature" "$package" | tee "$status"
grep -F "[GNUPG:] VALIDSIG ${fingerprint} " "$status"

mkdir -p "$extract"
tar -xzf "$package" -C "$extract"
mapfile -t debs < <(find "$extract" -type f -path '*/DEBS/*.deb' -print | sort)
[[ ${#debs[@]} -gt 0 ]] || { echo 'The signed LibreOffice archive contains no Debian packages.' >&2; exit 1; }
sudo apt-get update
sudo apt-get install -y "${debs[@]}"
source_install='/opt/libreoffice26.2'
install_root='/usr/local/lib/paperbatch-libreoffice26.2'
sudo install -d -o root -g root -m 0755 "$install_root"
sudo cp -a "$source_install/." "$install_root/"
engine="$install_root/program/soffice.bin"
launcher="$install_root/program/soffice"
[[ -f "$engine" && -x "$engine" && -f "$launcher" ]] || { echo 'The verified LibreOffice installation is missing its program engine or launcher.' >&2; exit 1; }
version_output="$("$launcher" --version)"
[[ "$version_output" =~ ^LibreOffice[[:space:]]+${version}([[:space:]]|$) ]] || { echo 'Installed LibreOffice version did not match the pinned version.' >&2; exit 1; }
engine_hash="$(sha256sum "$engine" | awk '{print $1}')"
package_hash="$(sha256sum "$package" | awk '{print $1}')"
python3 - "$provenance" "$version" "$base/$package_name" "$package_name" "$package_hash" "$fingerprint" <<'PY'
import json, sys
path, version, url, package, digest, fingerprint = sys.argv[1:]
value = {
    "version": version,
    "source": "Document Foundation official archive",
    "sourceUrl": url,
    "package": package,
    "packageSha256": digest,
    "integrityMethod": "OpenPGP detached signature verified before extraction and installation",
    "signingKeyFingerprint": fingerprint,
}
with open(path, "w", encoding="utf-8", newline="\n") as stream:
    json.dump(value, stream, separators=(",", ":"))
    stream.write("\n")
PY
{
  echo "PAPERBATCH_LO_ENGINE_PATH=$engine"
  echo "PAPERBATCH_LO_ENGINE_SHA256=$engine_hash"
  echo "PAPERBATCH_LO_EXPECTED_VERSION=LibreOffice $version"
  echo "PAPERBATCH_LO_INSTALLER_PROVENANCE=$provenance"
} >> "$GITHUB_ENV"
echo "Verified LibreOffice $version installer signature and installed engine identity."
