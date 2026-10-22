#!/usr/bin/env python3
"""Validate the lite package manifest and ZIP contents before any launch."""
from __future__ import annotations
import argparse
import hashlib
import json
import re
import stat
import shutil
import tempfile
import zipfile
from pathlib import Path, PurePosixPath

HEX = re.compile(r"^[0-9a-f]{64}$")
REQUIRED = {"schemaVersion", "package", "sourceRevision", "target", "configuration", "selfContained", "runtimeIdentifier", "dependencyLockFiles", "license", "signed", "excludedRuntimes", "files"}

def fail(message: str) -> None:
    raise ValueError(message)

def safe_path(value: object) -> str:
    if not isinstance(value, str) or not value or "\\" in value or "\x00" in value:
        fail("manifest path must be a non-empty POSIX relative path")
    raw_parts = value.split("/")
    p = PurePosixPath(value)
    if p.is_absolute() or any(part in ("", ".", "..") for part in raw_parts) or ":" in raw_parts[0]:
        fail(f"unsafe package path: {value!r}")
    return p.as_posix()

def validate(root: Path, excluded_outputs: set[str] | None = None) -> dict:
    excluded_outputs = excluded_outputs or set()
    manifest_path = root / "package-manifest.json"
    m = json.loads(manifest_path.read_text(encoding="utf-8-sig"))
    if not isinstance(m, dict) or set(m) != REQUIRED:
        fail("manifest keys do not match schema version 1")
    if m["schemaVersion"] != 1 or m["target"] != "win-x64" or m["runtimeIdentifier"] != "win-x64" or m["selfContained"] is not True:
        fail("unsupported target or manifest schema")
    if m["license"] != "AGPL-3.0-only" or m["signed"] is not False:
        fail("package license/signing declaration is invalid")
    if not isinstance(m["sourceRevision"], str) or not re.fullmatch(r"[0-9a-f]{40,64}", m["sourceRevision"]):
        fail("invalid source revision")
    seen = set()
    entries = m["files"]
    if not isinstance(entries, list) or not entries:
        fail("manifest files must be a non-empty array")
    for row in entries:
        if not isinstance(row, dict) or set(row) != {"path", "size", "sha256"}:
            fail("invalid file entry")
        name = safe_path(row["path"])
        if name in seen:
            fail(f"duplicate manifest path: {name}")
        seen.add(name)
        if not isinstance(row["size"], int) or row["size"] < 0 or not isinstance(row["sha256"], str) or not HEX.fullmatch(row["sha256"]):
            fail(f"invalid size or digest for {name}")
        path = root.joinpath(*PurePosixPath(name).parts)
        attrs = getattr(path.stat(follow_symlinks=False), "st_file_attributes", 0)
        if not path.is_file() or path.is_symlink() or attrs & getattr(stat, "FILE_ATTRIBUTE_REPARSE_POINT", 0x400):
            fail(f"manifest file missing or not regular: {name}")
        data = path.read_bytes()
        if len(data) != row["size"] or hashlib.sha256(data).hexdigest() != row["sha256"]:
            fail(f"manifest size or digest mismatch: {name}")
    actual = {
        p.relative_to(root).as_posix()
        for p in root.rglob("*")
        if p.is_file() and p != manifest_path and p.relative_to(root).as_posix() not in excluded_outputs
    }
    if actual != seen:
        fail(f"package file set differs from manifest (extra={sorted(actual-seen)}, missing={sorted(seen-actual)})")
    for lock in m["dependencyLockFiles"]:
        if not isinstance(lock, dict) or set(lock) != {"path", "sha256"} or not HEX.fullmatch(str(lock["sha256"])):
            fail("invalid dependency lock entry")
        safe_path(lock["path"])
    for executable in root.rglob("*"):
        attrs = getattr(executable.stat(follow_symlinks=False), "st_file_attributes", 0)
        if executable.is_symlink() or attrs & getattr(stat, "FILE_ATTRIBUTE_REPARSE_POINT", 0x400):
            fail(f"reparse or symlink entry: {executable.relative_to(root).as_posix()}")
        if executable.is_file() and executable.suffix.lower() in {".exe", ".dll", ".com", ".bat", ".cmd", ".ps1"}:
            rel = executable.relative_to(root).as_posix()
            if not rel.startswith("app/") or executable.suffix.lower() in {".com", ".bat", ".cmd", ".ps1"}:
                fail(f"unexpected executable or script: {rel}")
            if any(token in executable.name.lower() for token in ("libreoffice", "office", "wps", "pdfium", "font", "driver")):
                fail(f"excluded runtime is present: {rel}")
    if not {"LICENSE", "THIRD-PARTY-UI-NOTICES.md", "THIRD-PARTY-CORE-NOTICES.md", "DOTNET-THIRD-PARTY-NOTICES.txt", "README-package.txt"}.issubset(seen):
        fail("required license and package notice files missing")
    return m

def verify_archive(archive: Path, expected_root: Path, excluded_outputs: set[str] | None = None) -> None:
    excluded_outputs = excluded_outputs or set()
    expected = {}
    for path in expected_root.rglob("*"):
        if path.is_file() and path.relative_to(expected_root).as_posix() not in excluded_outputs:
            digest = hashlib.sha256()
            size = 0
            with path.open("rb") as stream:
                for block in iter(lambda: stream.read(1024 * 1024), b""):
                    size += len(block)
                    digest.update(block)
            expected[path.relative_to(expected_root).as_posix()] = (size, digest.hexdigest())
    with zipfile.ZipFile(archive) as z:
        names, seen = [], set()
        for info in z.infolist():
            name = safe_path(info.filename.rstrip("/")) if info.filename.rstrip("/") else ""
            if not name:
                fail("rooted or empty ZIP entry")
            if name in seen:
                fail(f"duplicate ZIP entry: {name}")
            seen.add(name)
            mode = info.external_attr >> 16
            if stat.S_ISLNK(mode):
                fail(f"symlink ZIP entry: {name}")
            if info.is_dir():
                continue
            names.append(name)
        if set(names) != set(expected):
            fail("archive file list differs from package directory")
        for name in names:
            digest = hashlib.sha256()
            size = 0
            with z.open(name) as stream:
                for block in iter(lambda: stream.read(1024 * 1024), b""):
                    size += len(block)
                    digest.update(block)
            if (size, digest.hexdigest()) != expected[name]:
                fail(f"archive payload mismatch: {name}")
        with tempfile.TemporaryDirectory(prefix="paperbatch-package-check-") as temp:
            for info in z.infolist():
                name = info.filename.rstrip("/")
                if not name:
                    fail("empty ZIP entry")
                target = Path(temp).joinpath(*PurePosixPath(name).parts)
                if not target.resolve().is_relative_to(Path(temp).resolve()):
                    fail(f"archive path escapes extraction root: {name}")
                if info.is_dir():
                    target.mkdir(parents=True, exist_ok=True)
                else:
                    target.parent.mkdir(parents=True, exist_ok=True)
                    with z.open(info) as source, target.open("wb") as destination:
                        shutil.copyfileobj(source, destination, 1024 * 1024)
            extracted = {p.relative_to(temp).as_posix() for p in Path(temp).rglob("*") if p.is_file()}
            if extracted != set(expected):
                fail("fresh extraction file set differs from package")
            validate(Path(temp))

def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("package_dir", type=Path)
    ap.add_argument("archive", type=Path)
    args = ap.parse_args()
    sidecar = Path(str(args.archive) + ".sha256")
    package_outputs: set[str] = set()
    try:
        package_outputs.add(args.archive.resolve().relative_to(args.package_dir.resolve()).as_posix())
        package_outputs.add(sidecar.resolve().relative_to(args.package_dir.resolve()).as_posix())
    except ValueError:
        pass
    validate(args.package_dir, package_outputs)
    verify_archive(args.archive, args.package_dir, package_outputs)
    fields = sidecar.read_text(encoding="ascii").split()
    if len(fields) != 2 or fields[1] != args.archive.name or hashlib.sha256(args.archive.read_bytes()).hexdigest() != fields[0]:
        fail("archive SHA-256 sidecar mismatch")
    print("package manifest, payload, archive, and fresh extraction verified")
    return 0
if __name__ == "__main__":
    try: raise SystemExit(main())
    except (ValueError, OSError, zipfile.BadZipFile, json.JSONDecodeError) as e:
        raise SystemExit(f"package verification failed: {e}")
