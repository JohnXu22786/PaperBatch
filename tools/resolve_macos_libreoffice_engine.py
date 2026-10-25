#!/usr/bin/env python3
"""Resolve and verify LibreOffice's native executable in a macOS app bundle."""

from __future__ import annotations

import argparse
import os
import re
import subprocess
from pathlib import Path
from typing import Callable


def resolve_engine(
    app_bundle: Path,
    expected_version: str,
    run: Callable[..., subprocess.CompletedProcess[str]] = subprocess.run,
) -> Path:
    """Return the signed app's main Mach-O executable after checking its version."""
    app_root = app_bundle.resolve(strict=True)
    executable = app_root / "Contents" / "MacOS" / "soffice"
    if not executable.is_file() or not os.access(executable, os.X_OK):
        raise RuntimeError("LibreOffice app bundle is missing executable Contents/MacOS/soffice.")

    engine = executable.resolve(strict=True)
    if not engine.is_relative_to(app_root):
        raise RuntimeError("LibreOffice main executable resolves outside the verified app bundle.")

    try:
        kind = run(["/usr/bin/file", "-b", str(engine)], capture_output=True, text=True,
                   timeout=10, check=False)
    except (OSError, subprocess.TimeoutExpired) as exc:
        raise RuntimeError("Could not inspect the LibreOffice main executable file type.") from exc
    if kind.returncode != 0 or "Mach-O" not in kind.stdout:
        raise RuntimeError("LibreOffice Contents/MacOS/soffice is not a native Mach-O executable.")

    try:
        version = run([str(engine), "--version"], capture_output=True, text=True,
                      timeout=20, check=False)
    except (OSError, subprocess.TimeoutExpired) as exc:
        raise RuntimeError("Could not run the LibreOffice main executable version check.") from exc
    version_line = version.stdout.strip()
    pinned_version_line = rf"^LibreOffice\s+{re.escape(expected_version)}(?:\s|$)"
    if version.returncode != 0 or not re.match(pinned_version_line, version_line):
        raise RuntimeError(f"LibreOffice main executable did not report pinned version {expected_version}.")
    return engine


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--app-bundle", required=True, type=Path)
    parser.add_argument("--expected-version", required=True)
    args = parser.parse_args()
    try:
        print(resolve_engine(args.app_bundle, args.expected_version))
    except (OSError, RuntimeError) as exc:
        parser.exit(1, f"{exc}\n")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
