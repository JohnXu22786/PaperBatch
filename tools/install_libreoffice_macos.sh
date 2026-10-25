#!/usr/bin/env bash
set -euo pipefail

version='26.2.2.2'
fingerprint='C2839ECAD9408FBE9531C3E9F434A1EFAFEEAEA3'
case "$(uname -m)" in
  arm64) architecture='aarch64' ;;
  x86_64) architecture='x86_64' ;;
  *) echo 'Unsupported hosted macOS architecture.' >&2; exit 1 ;;
esac
package_name="LibreOffice_${version}_MacOS_${architecture}.dmg"
base="https://downloadarchive.documentfoundation.org/libreoffice/old/${version}/mac/${architecture}"
package="$RUNNER_TEMP/$package_name"
signature="$package.asc"
key="$RUNNER_TEMP/tdf-release-key.asc"
keyring="$RUNNER_TEMP/tdf-release-key.gpg"
status="$RUNNER_TEMP/tdf-signature-status.txt"
mountpoint="$RUNNER_TEMP/paperbatch-lo-dmg"
install_root="$RUNNER_TEMP/paperbatch-libreoffice-$version"
app="$install_root/LibreOffice.app"

if ! command -v gpg >/dev/null 2>&1; then
  brew install gnupg
fi
command -v gpg >/dev/null 2>&1 || { echo 'A GPG verifier is unavailable on this hosted macOS runner.' >&2; exit 1; }
command -v gpgv >/dev/null 2>&1 || { echo 'The matching gpgv verifier is unavailable.' >&2; exit 1; }
curl --fail --location --retry 3 "$base/$package_name" --output "$package"
curl --fail --location --retry 3 "$base/$package_name.asc" --output "$signature"
curl --fail --location --retry 3 'https://keyserver.ubuntu.com/pks/lookup?op=get&search=0xF434A1EFAFEEAEA3' --output "$key"
gpg --batch --show-keys --with-colons "$key" | grep -F "fpr:::::::::${fingerprint}:"
gpg --batch --dearmor --output "$keyring" "$key"
gpgv --status-fd=1 --keyring "$keyring" "$signature" "$package" | tee "$status"
grep -F "[GNUPG:] VALIDSIG ${fingerprint} " "$status"

mkdir -p "$install_root" "$mountpoint"
mounted=0
cleanup() {
  if [[ "$mounted" == 1 ]]; then hdiutil detach "$mountpoint" -quiet || true; fi
  rm -rf "$mountpoint"
}
trap cleanup EXIT
hdiutil attach "$package" -readonly -nobrowse -mountpoint "$mountpoint" >/dev/null
mounted=1
source_app="$(find "$mountpoint" -maxdepth 3 -type d -name 'LibreOffice.app' -print -quit)"
[[ -n "$source_app" && -d "$source_app" ]]
ditto "$source_app" "$app"
hdiutil detach "$mountpoint" -quiet
mounted=0
engine="$app/Contents/MacOS/soffice.bin"
launcher="$app/Contents/MacOS/soffice"
[[ -f "$engine" && -x "$engine" && -f "$launcher" && -x "$launcher" ]]
codesign --verify --deep --strict "$app"
engine_hash="$(shasum -a 256 "$engine" | awk '{print $1}')"
package_hash="$(shasum -a 256 "$package" | awk '{print $1}')"
provenance="$RUNNER_TEMP/paperbatch-lo-installer-provenance.json"
VERSION="$version" BASE_URL="$base" PACKAGE="$package_name" PACKAGE_HASH="$package_hash" FINGERPRINT="$fingerprint" PROVENANCE="$provenance" python3 - <<'PY'
import json, os
value = {
    "version": os.environ["VERSION"],
    "source": "Document Foundation official archive",
    "sourceUrl": os.environ["BASE_URL"] + "/" + os.environ["PACKAGE"],
    "package": os.environ["PACKAGE"],
    "packageSha256": os.environ["PACKAGE_HASH"],
    "integrityMethod": "OpenPGP detached signature verified before DMG mount and copy",
    "signingKeyFingerprint": os.environ["FINGERPRINT"],
}
with open(os.environ["PROVENANCE"], "w", encoding="utf-8", newline="\n") as stream:
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
