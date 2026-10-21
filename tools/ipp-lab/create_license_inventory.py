#!/usr/bin/env python3
"""Capture exact installed package versions and their Ubuntu copyright metadata."""

from __future__ import annotations

import hashlib
import json
import re
import subprocess
import sys
from pathlib import Path


MAX_COPYRIGHT_FILE_BYTES = 2 * 1024 * 1024
LICENSE_PATTERN = re.compile(r"^License:\s*(.+?)\s*$", re.MULTILINE)


def main() -> int:
    if len(sys.argv) != 2:
        raise SystemExit("usage: create_license_inventory.py OUTPUT.json")
    package_output = subprocess.run(
        ["dpkg-query", "-W", "-f=${Package}\t${Version}\n"],
        check=True,
        capture_output=True,
        text=True,
    ).stdout
    packages = []
    for line in package_output.splitlines():
        package, version = line.split("\t", 1)
        documentation_package = package.split(":", 1)[0]
        copyright_path = Path("/usr/share/doc") / documentation_package / "copyright"
        if not copyright_path.is_file():
            packages.append(
                {
                    "package": package,
                    "version": version,
                    "copyrightMetadata": None,
                    "licenseDeclarations": [],
                }
            )
            continue
        digest = hashlib.sha256()
        captured = bytearray()
        with copyright_path.open("rb") as source:
            while chunk := source.read(64 * 1024):
                digest.update(chunk)
                if len(captured) < MAX_COPYRIGHT_FILE_BYTES:
                    captured.extend(chunk[: MAX_COPYRIGHT_FILE_BYTES - len(captured)])
        declarations = sorted(set(LICENSE_PATTERN.findall(captured.decode("utf-8", errors="replace"))))
        packages.append(
            {
                "package": package,
                "version": version,
                "copyrightMetadata": str(copyright_path),
                "copyrightMetadataSha256": digest.hexdigest(),
                "licenseDeclarations": declarations,
                "licenseMetadataTruncated": copyright_path.stat().st_size > MAX_COPYRIGHT_FILE_BYTES,
            }
        )

    destination = Path(sys.argv[1])
    destination.parent.mkdir(parents=True, exist_ok=True)
    destination.write_text(
        json.dumps({"format": "ubuntu-package-copyright-inventory-v1", "packages": packages}, indent=2)
        + "\n",
        encoding="utf-8",
    )
    return 0


if __name__ == "__main__":
    sys.exit(main())
