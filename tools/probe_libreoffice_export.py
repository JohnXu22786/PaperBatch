#!/usr/bin/env python3
"""Run an isolated LibreOffice DOCX-to-PDF smoke test with fixed input."""

from __future__ import annotations

import argparse
import hashlib
import json
import os
import platform
import platform
import re
import shutil
import signal
import threading
import stat
import struct
import subprocess
import sys
import tempfile
import time
import zipfile
import zlib
from dataclasses import dataclass
from datetime import datetime, timezone
from pathlib import Path
from typing import Any

MAX_PDF_INSPECTION_BYTES = 64 * 1024 * 1024
MAX_PDF_OBJECTS = 1_000_000
MAX_PDF_PARSE_DEPTH = 64
MAX_PDF_PARSE_TOKENS = 100_000
MAX_PDF_INTEGER_DIGITS = 20
MAX_PDF_GENERATION = 65_535
MAX_PDF_DECODED_STREAM_BYTES = 8 * 1024 * 1024
MAX_PDF_PAGE_CONTENT_BYTES = MAX_PDF_DECODED_STREAM_BYTES
MAX_PDF_TOTAL_CONTENT_BYTES = MAX_PDF_DECODED_STREAM_BYTES
MAX_PDF_TOTAL_CMAP_BYTES = MAX_PDF_DECODED_STREAM_BYTES
MAX_ENGINE_SNAPSHOT_BYTES = 512 * 1024 * 1024
MAX_RUNTIME_DIRECTORY_BYTES = 4 * 1024 * 1024 * 1024
MAX_RUNTIME_DIRECTORY_ENTRIES = 100_000
PDF_WHITESPACE = b"\x00\x09\x0a\x0c\x0d\x20"
PDF_WHITESPACE_PATTERN = rb"[\x00\x09\x0a\x0c\x0d\x20]"
PDF_OBJECT_MARKER_TERMINATOR_PATTERN = (
    rb"(?:" + PDF_WHITESPACE_PATTERN + rb"|[()<>\[\]{}/%])"
)
PDF_INDIRECT_OBJECT_HEADER = re.compile(
    rb"(\d+)" + PDF_WHITESPACE_PATTERN + rb"+(\d+)" + PDF_WHITESPACE_PATTERN
    + rb"+obj(?=" + PDF_OBJECT_MARKER_TERMINATOR_PATTERN + rb")"
)
PDF_REAL_NUMBER = re.compile(rb"[+-]?(?:\d+\.\d*|\.\d+)")
EXPECTED_FIXTURE_TEXT = "PaperBatch U0 export probe"


class PdfContentLimitError(ValueError):
    """Raised when a decoded page content stream exceeds parser token limits."""


class PdfCMapInspectionState:
    """Cache bounded decoded CMaps for one document inspection."""

    def __init__(self) -> None:
        self.decoded_by_reference: dict[tuple[int, int], bytes | None] = {}
        self.parsed_by_cmap: dict[bytes, dict[bytes, str | None] | None] = {}
        self.total_decoded_bytes = 0
        self.exceeded = False

    def read_cmap(
        self,
        data: bytes,
        entries: dict[int, tuple[int, int, bytes]],
        reference: tuple[int, int],
        xref_offset: int,
    ) -> bytes | None:
        if self.exceeded:
            return None
        if reference in self.decoded_by_reference:
            return self.decoded_by_reference[reference]

        stream = read_indirect_stream(data, entries, reference, xref_offset)
        if stream is None:
            self.decoded_by_reference[reference] = None
            return None
        dictionary, payload = stream
        decoded = decode_pdf_stream(dictionary, payload)
        if decoded is not None:
            if self.total_decoded_bytes + len(decoded) > MAX_PDF_TOTAL_CMAP_BYTES:
                self.exceeded = True
                self.decoded_by_reference[reference] = None
                return None
            self.total_decoded_bytes += len(decoded)
            self.parsed_map(decoded)
        self.decoded_by_reference[reference] = decoded
        return decoded

    def parsed_map(self, cmap: bytes) -> dict[bytes, str | None] | None:
        if cmap not in self.parsed_by_cmap:
            tokens = tokenize_cmap(cmap)
            body_tokens = cmap_body_tokens(tokens)
            if body_tokens is None:
                self.parsed_by_cmap[cmap] = None
            else:
                self.parsed_by_cmap[cmap] = parse_tounicode_bfchar_tokens(body_tokens)
        return self.parsed_by_cmap[cmap]


class PdfContentInspectionState:
    """Cache decoded content streams and cap unique decoded bytes per document."""

    def __init__(self) -> None:
        self.decoded_by_reference: dict[tuple[int, int], bytes | None] = {}
        self.nonempty_by_reference: dict[tuple[int, int], bool] = {}
        self.total_decoded_bytes = 0
        self.exceeded = False

    def read_content(
        self,
        data: bytes,
        entries: dict[int, tuple[int, int, bytes]],
        reference: tuple[int, int],
        xref_offset: int,
    ) -> bytes | None:
        if self.exceeded:
            return None
        if reference in self.decoded_by_reference:
            return self.decoded_by_reference[reference]

        stream = read_indirect_stream(data, entries, reference, xref_offset)
        if stream is None:
            self.decoded_by_reference[reference] = None
            return None
        dictionary, payload = stream
        if b"Filter" not in dictionary and len(payload) > MAX_PDF_TOTAL_CONTENT_BYTES - self.total_decoded_bytes:
            self.exceeded = True
            self.decoded_by_reference[reference] = None
            return None
        decoded = decode_pdf_stream(dictionary, payload)
        if decoded is not None:
            if self.total_decoded_bytes + len(decoded) > MAX_PDF_TOTAL_CONTENT_BYTES:
                self.exceeded = True
                self.decoded_by_reference[reference] = None
                return None
            self.total_decoded_bytes += len(decoded)
        self.decoded_by_reference[reference] = decoded
        return decoded

    def has_nonempty_payload(
        self,
        data: bytes,
        entries: dict[int, tuple[int, int, bytes]],
        reference: tuple[int, int],
        xref_offset: int,
    ) -> bool:
        if reference not in self.nonempty_by_reference:
            payload = read_indirect_stream_payload(data, entries, reference, xref_offset)
            self.nonempty_by_reference[reference] = (
                payload is not None and bool(payload.strip(b"\x00\x09\x0a\x0c\x0d\x20"))
            )
        return self.nonempty_by_reference[reference]


def parse_bounded_pdf_integer(token: bytes) -> int:
    digits = token[1:] if token[:1] in (b"+", b"-") else token
    if not digits or len(digits) > MAX_PDF_INTEGER_DIGITS or not digits.isdigit():
        raise ValueError("PDF integer is outside the inspection limit")
    return int(token)


def write_fixture(path: Path) -> None:
    """Create a minimal fixed-content DOCX without adding a binary fixture."""
    parts = (
        (
            "[Content_Types].xml",
            """<?xml version="1.0" encoding="UTF-8" standalone="yes"?>
<Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">
  <Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/>
  <Default Extension="xml" ContentType="application/xml"/>
  <Override PartName="/word/document.xml" ContentType="application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml"/>
</Types>""",
        ),
        (
            "_rels/.rels",
            """<?xml version="1.0" encoding="UTF-8" standalone="yes"?>
<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
  <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="word/document.xml"/>
</Relationships>""",
        ),
        (
            "word/document.xml",
            """<?xml version="1.0" encoding="UTF-8" standalone="yes"?>
<w:document xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main">
  <w:body>
    <w:p><w:r><w:t>PaperBatch U0 export probe</w:t></w:r></w:p>
    <w:sectPr/>
  </w:body>
</w:document>""",
        ),
    )

    with zipfile.ZipFile(path, "w", compression=zipfile.ZIP_DEFLATED, compresslevel=9) as archive:
        for name, content in parts:
            info = zipfile.ZipInfo(name, date_time=(2020, 1, 1, 0, 0, 0))
            info.compress_type = zipfile.ZIP_DEFLATED
            info.external_attr = 0o600 << 16
            archive.writestr(info, content.encode("utf-8"))


def sha256_file(path: Path) -> str:
    with open_readonly_regular_file(path) as stream:
        return sha256_stream(stream)


def sha256_stream(stream) -> str:
    digest = hashlib.sha256()
    stream.seek(0)
    for chunk in iter(lambda: stream.read(1024 * 1024), b""):
        digest.update(chunk)
    stream.seek(0)
    return digest.hexdigest()


class PinnedEngineError(OSError):
    def __init__(self, message: str, observed_sha256: str | None = None) -> None:
        super().__init__(message)
        self.observed_sha256 = observed_sha256


@dataclass
class PinnedEngine:
    stream: Any
    launch_target: str
    launch_kwargs: dict[str, Any]
    sha256: str
    engine_path: str
    identity_binding: str
    runtime_directory_descriptor: int | None = None
    runtime_directory_path: str | None = None
    runtime_directory_sha256: str = ""
    engine_file_identity: tuple[int, int] | None = None
    source_stream: Any = None

    def close(self) -> None:
        try:
            self.stream.close()
        finally:
            try:
                if self.source_stream is not None:
                    self.source_stream.close()
                    self.source_stream = None
            finally:
                if self.runtime_directory_descriptor is not None:
                    os.close(self.runtime_directory_descriptor)
                    self.runtime_directory_descriptor = None

    def verify_runtime_directory(self, *, verify_engine_image: bool = False) -> str:
        if self.runtime_directory_path is None:
            raise PinnedEngineError("The selected engine has no validated runtime directory.")
        observed = runtime_directory_sha256(
            Path(self.runtime_directory_path),
            Path(self.engine_path).name,
            self.engine_file_identity,
        )
        if observed != self.runtime_directory_sha256:
            raise PinnedEngineError(
                "LibreOffice runtime components changed while the process was running; version/export evidence is blocked."
            )
        if verify_engine_image and sys.platform.startswith("linux"):
            engine_path = Path(self.runtime_directory_path) / Path(self.engine_path).name
            with open_readonly_regular_file(engine_path) as engine_stream:
                if sha256_stream(engine_stream).casefold() != self.sha256.casefold():
                    raise PinnedEngineError(
                        "The installed LibreOffice engine no longer matches the pinned engine image."
                    )
        return observed

    def create_launcher_overlay(self, overlay_directory: Path) -> Path:
        """Create an isolated LO program view whose engine entry points at the sealed image."""
        if not sys.platform.startswith("linux") or self.runtime_directory_path is None:
            raise PinnedEngineError("The LibreOffice launcher overlay requires a verified Linux runtime directory.")
        self.verify_runtime_directory(verify_engine_image=True)
        source_directory = Path(self.runtime_directory_path)
        overlay_directory.mkdir(parents=True, exist_ok=False)
        for entry in os.scandir(source_directory):
            if entry.name in {"soffice", "soffice.bin"}:
                continue
            target = overlay_directory / entry.name
            os.symlink(entry.path, target, target_is_directory=entry.is_dir(follow_symlinks=False))

        launcher_source = source_directory / "soffice"
        with open_readonly_regular_file(launcher_source) as launcher_stream:
            launcher_stat = os.fstat(launcher_stream.fileno())
            if not stat.S_ISREG(launcher_stat.st_mode) or launcher_stat.st_size > 1024 * 1024:
                raise PinnedEngineError("The LibreOffice launcher is not a bounded regular file.")
            launcher_bytes = launcher_stream.read(1024 * 1024 + 1)
            if len(launcher_bytes) != launcher_stat.st_size:
                raise PinnedEngineError("The LibreOffice launcher changed while the overlay was being created.")
        overlay_launcher = overlay_directory / "soffice"
        overlay_launcher.write_bytes(launcher_bytes)
        os.chmod(overlay_launcher, stat.S_IMODE(launcher_stat.st_mode))
        if not (launcher_stat.st_mode & 0o111):
            raise PinnedEngineError("The LibreOffice runtime launcher is not executable.")

        engine_descriptor = self.stream.fileno()
        engine_source = source_directory / Path(self.engine_path).name
        engine_metadata = os.stat(engine_source, follow_symlinks=False)
        engine_open = open_readonly_regular_file(engine_source)
        try:
            engine_status = os.fstat(engine_open.fileno())
            if (engine_status.st_dev, engine_status.st_ino) != (engine_metadata.st_dev, engine_metadata.st_ino):
                raise PinnedEngineError("The installed LibreOffice engine changed while the overlay was being created.")
        finally:
            engine_open.close()
        os.symlink(f"/proc/{os.getpid()}/fd/{engine_descriptor}", overlay_directory / "soffice.bin")
        return overlay_launcher


def runtime_directory_sha256(
    directory: Path,
    excluded_engine_name: str,
    excluded_file_identity: tuple[int, int] | None = None,
) -> str:
    """Hash a bounded program-directory tree, excluding the separately pinned engine image."""
    digest = hashlib.sha256()
    entries_seen = 0
    bytes_seen = 0
    pending = [(directory, "")]
    excluded = excluded_engine_name.casefold() if os.name == "nt" else excluded_engine_name

    while pending:
        current, relative_directory = pending.pop()
        with os.scandir(current) as iterator:
            children = []
            for child in iterator:
                entries_seen += 1
                if entries_seen > MAX_RUNTIME_DIRECTORY_ENTRIES:
                    raise PinnedEngineError("The LibreOffice runtime directory exceeds the 100,000-entry inspection limit.")
                children.append(child)
            children.sort(key=lambda entry: entry.name)
        for child in children:
            relative = f"{relative_directory}/{child.name}" if relative_directory else child.name
            comparable = relative.casefold() if os.name == "nt" else relative
            # Resolve the entry through the same path-based stat API used on all
            # platforms. Windows DirEntry metadata can omit file identity data
            # that fstat reports for the opened handle.
            metadata = os.stat(child.path, follow_symlinks=False)
            is_reparse_point = bool(
                getattr(metadata, "st_file_attributes", 0)
                & getattr(stat, "FILE_ATTRIBUTE_REPARSE_POINT", 0x00000400)
            )
            if stat.S_ISLNK(metadata.st_mode) or is_reparse_point:
                raise PinnedEngineError("The LibreOffice runtime directory contains a symbolic link or reparse point that cannot be safely fingerprinted.")
            encoded_path = relative.replace(os.sep, "/").encode("utf-8", "strict")
            if stat.S_ISDIR(metadata.st_mode):
                digest.update(
                    b"D\0"
                    + encoded_path
                    + b"\0"
                    + str(stat.S_IMODE(metadata.st_mode)).encode("ascii")
                    + b"\0"
                )
                pending.append((Path(child.path), relative))
                continue
            if not stat.S_ISREG(metadata.st_mode):
                raise PinnedEngineError("The LibreOffice runtime directory contains a non-regular entry that cannot be safely fingerprinted.")

            is_pinned_engine_entry = (
                relative_directory == ""
                and comparable == excluded
                and excluded_file_identity is not None
                and excluded_file_identity != (0, 0)
                and (metadata.st_dev, metadata.st_ino) == excluded_file_identity
            )

            if metadata.st_size < 0 or bytes_seen + metadata.st_size > MAX_RUNTIME_DIRECTORY_BYTES:
                raise PinnedEngineError("The LibreOffice runtime directory exceeds the 4 GiB content inspection limit.")
            flags = (
                os.O_RDONLY
                | getattr(os, "O_CLOEXEC", 0)
                | getattr(os, "O_NOFOLLOW", 0)
                | getattr(os, "O_NONBLOCK", 0)
            )
            descriptor = os.open(child.path, flags)
            try:
                opened = os.fstat(descriptor)
                if (
                    not stat.S_ISREG(opened.st_mode)
                    or (opened.st_dev, opened.st_ino) != (metadata.st_dev, metadata.st_ino)
                    or opened.st_size != metadata.st_size
                ):
                    raise PinnedEngineError("A LibreOffice runtime component changed while its identity was being inspected.")
                if is_pinned_engine_entry:
                    current_path = os.stat(child.path, follow_symlinks=False)
                    current_is_reparse_point = bool(
                        getattr(current_path, "st_file_attributes", 0)
                        & getattr(stat, "FILE_ATTRIBUTE_REPARSE_POINT", 0x00000400)
                    )
                    if (
                        stat.S_ISLNK(current_path.st_mode)
                        or current_is_reparse_point
                        or not stat.S_ISREG(current_path.st_mode)
                        or (current_path.st_dev, current_path.st_ino) != excluded_file_identity
                    ):
                        raise PinnedEngineError("The pinned engine directory entry changed while its identity was being inspected.")
                    continue

                digest.update(
                    b"F\0"
                    + encoded_path
                    + b"\0"
                    + str(stat.S_IMODE(opened.st_mode)).encode("ascii")
                    + b"\0"
                    + str(opened.st_size).encode("ascii")
                    + b"\0"
                )
                component_bytes = 0
                with os.fdopen(descriptor, "rb", closefd=False) as stream:
                    for chunk in iter(lambda: stream.read(1024 * 1024), b""):
                        component_bytes += len(chunk)
                        if component_bytes > metadata.st_size or bytes_seen + component_bytes > MAX_RUNTIME_DIRECTORY_BYTES:
                            raise PinnedEngineError("The LibreOffice runtime directory changed or exceeded the 4 GiB content inspection limit.")
                        digest.update(chunk)
                after = os.fstat(descriptor)
                if after.st_size != opened.st_size or after.st_mtime_ns != opened.st_mtime_ns or component_bytes != opened.st_size:
                    raise PinnedEngineError("A LibreOffice runtime component changed while its identity was being inspected.")
                bytes_seen += component_bytes
                digest.update(b"\0")
            finally:
                os.close(descriptor)

    return digest.hexdigest()


def open_readonly_regular_file(path: Path):
    if sys.platform.startswith("linux"):
        path_descriptor = os.open(
            path,
            getattr(os, "O_PATH", 0x200000) | getattr(os, "O_CLOEXEC", 0),
        )
        descriptor = None
        try:
            path_status = os.fstat(path_descriptor)
            if not stat.S_ISREG(path_status.st_mode):
                raise PinnedEngineError("The selected path is not a regular file.")
            descriptor = os.open(
                f"/proc/self/fd/{path_descriptor}",
                os.O_RDONLY | getattr(os, "O_NONBLOCK", 0) | getattr(os, "O_CLOEXEC", 0),
            )
            content_status = os.fstat(descriptor)
            if (
                not stat.S_ISREG(content_status.st_mode)
                or (path_status.st_dev, path_status.st_ino) != (content_status.st_dev, content_status.st_ino)
            ):
                raise PinnedEngineError("The opened content file does not match the pinned regular-file identity.")
            stream = os.fdopen(descriptor, "rb")
            descriptor = None
            return stream
        finally:
            if descriptor is not None:
                os.close(descriptor)
            os.close(path_descriptor)

    if sys.platform == "darwin":
        flags = (
            os.O_RDONLY
            | getattr(os, "O_NOFOLLOW", 0x100)
            | getattr(os, "O_NONBLOCK", 0)
            | getattr(os, "O_CLOEXEC", 0)
        )
        descriptor = os.open(path, flags)
        try:
            if not stat.S_ISREG(os.fstat(descriptor).st_mode):
                raise PinnedEngineError("The selected path is not a regular file.")
            stream = os.fdopen(descriptor, "rb")
            descriptor = -1
            return stream
        finally:
            if descriptor >= 0:
                os.close(descriptor)

    if os.name != "nt":
        path.stat()
        raise PinnedEngineError("Safe regular-file inspection is supported only on Windows, Linux, and macOS.")

    stream = path.open("rb")
    if not stat.S_ISREG(os.fstat(stream.fileno()).st_mode):
        stream.close()
        raise PinnedEngineError("The selected path is not a regular file.")
    return stream


def resolve_windows_handle_path(handle: int) -> str:
    import ctypes
    from ctypes import wintypes

    kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)
    get_final_path = kernel32.GetFinalPathNameByHandleW
    get_final_path.argtypes = [wintypes.HANDLE, wintypes.LPWSTR, wintypes.DWORD, wintypes.DWORD]
    get_final_path.restype = wintypes.DWORD
    capacity = 512
    while capacity <= 32768:
        buffer = ctypes.create_unicode_buffer(capacity)
        length = get_final_path(wintypes.HANDLE(handle), buffer, capacity, 0)
        if length == 0:
            raise OSError(ctypes.get_last_error(), "Could not resolve the opened engine handle to a stable Windows path.")
        if length < capacity:
            return buffer.value
        capacity = int(length) + 1
    raise OSError("The resolved Windows engine path exceeds the supported buffer limit.")


def open_pinned_engine(executable: Path, expected_sha256: str):
    if not re.fullmatch(r"[0-9a-fA-F]{64}", expected_sha256):
        raise PinnedEngineError("A trusted 64-character engine SHA-256 pin is required before launch.")

    stream = None
    source_stream = None
    runtime_directory_descriptor = None
    runtime_directory_path: Path | None = None
    engine_file_identity: tuple[int, int] | None = None
    descriptor = None
    launch_target = str(executable)
    launch_kwargs: dict[str, Any] = {}
    opened_engine_path = str(executable)
    try:
        if os.name == "nt":
            import ctypes
            import msvcrt
            from ctypes import wintypes

            kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)
            create_file = kernel32.CreateFileW
            create_file.argtypes = [
                wintypes.LPCWSTR,
                wintypes.DWORD,
                wintypes.DWORD,
                wintypes.LPVOID,
                wintypes.DWORD,
                wintypes.DWORD,
                wintypes.HANDLE,
            ]
            create_file.restype = wintypes.HANDLE
            handle = create_file(str(executable), 0x80000000, 0x00000001, None, 3, 0x00000080, None)
            if handle == wintypes.HANDLE(-1).value:
                raise OSError(ctypes.get_last_error(), "Could not open the engine without write/delete sharing.")
            close_handle = kernel32.CloseHandle
            close_handle.argtypes = [wintypes.HANDLE]
            close_handle.restype = wintypes.BOOL
            try:
                launch_target = resolve_windows_handle_path(int(handle))
                descriptor = msvcrt.open_osfhandle(int(handle), os.O_RDONLY | getattr(os, "O_BINARY", 0))
            except Exception:
                close_handle(handle)
                raise
            try:
                stream = os.fdopen(descriptor, "rb")
            except Exception:
                os.close(descriptor)
                raise
            opened_engine_path = launch_target
            runtime_directory_path = Path(opened_engine_path).parent
            opened_stat = os.fstat(stream.fileno())
            engine_file_identity = (opened_stat.st_dev, opened_stat.st_ino)
        elif sys.platform.startswith("linux"):
            import fcntl

            source_stream = open_readonly_regular_file(executable)
            source_descriptor = source_stream.fileno()
            source_stat = os.fstat(source_descriptor)
            engine_file_identity = (source_stat.st_dev, source_stat.st_ino)
            if source_stat.st_nlink != 1:
                raise PinnedEngineError("The selected engine must have one installed directory entry before resolving sibling runtime files.")
            if source_stat.st_size > MAX_ENGINE_SNAPSHOT_BYTES:
                raise PinnedEngineError(
                    f"The selected engine exceeds the {MAX_ENGINE_SNAPSHOT_BYTES // (1024 * 1024)} MiB snapshot limit."
                )
            opened_engine_path = Path(os.readlink(f"/proc/self/fd/{source_descriptor}"))
            if not opened_engine_path.is_absolute() or str(opened_engine_path).endswith(" (deleted)"):
                raise PinnedEngineError("The opened engine has no installed target path for resolving sibling runtime files.")
            program_directory = opened_engine_path.parent
            runtime_directory_descriptor = os.open(
                program_directory,
                os.O_RDONLY | getattr(os, "O_DIRECTORY", 0x10000) | getattr(os, "O_CLOEXEC", 0),
            )
            runtime_directory_path = Path(f"/proc/self/fd/{runtime_directory_descriptor}")
            candidate_descriptor = os.open(
                runtime_directory_path / opened_engine_path.name,
                getattr(os, "O_PATH", 0x200000)
                | getattr(os, "O_CLOEXEC", 0)
                | getattr(os, "O_NOFOLLOW", 0x20000),
            )
            try:
                candidate_stat = os.fstat(candidate_descriptor)
                if candidate_stat.st_nlink != 1:
                    raise PinnedEngineError("The selected runtime directory contains an engine hard link with ambiguous sibling runtime files.")
                if (source_stat.st_dev, source_stat.st_ino) != (candidate_stat.st_dev, candidate_stat.st_ino):
                    raise PinnedEngineError("The selected runtime directory no longer contains the opened engine image.")
            finally:
                os.close(candidate_descriptor)
            if not hasattr(os, "memfd_create"):
                raise PinnedEngineError("Linux memfd snapshots are unavailable on this Python runtime.")
            descriptor = os.memfd_create(
                "paperbatch-u0-engine",
                getattr(os, "MFD_ALLOW_SEALING", 0x0002),
            )
            stream = os.fdopen(descriptor, "w+b")
            copied_bytes = 0
            while True:
                read_limit = min(1024 * 1024, MAX_ENGINE_SNAPSHOT_BYTES - copied_bytes + 1)
                chunk = source_stream.read(read_limit)
                if not chunk:
                    break
                if copied_bytes + len(chunk) > MAX_ENGINE_SNAPSHOT_BYTES:
                    raise PinnedEngineError(
                        f"The selected engine exceeds the {MAX_ENGINE_SNAPSHOT_BYTES // (1024 * 1024)} MiB snapshot limit."
                    )
                stream.write(chunk)
                copied_bytes += len(chunk)
            stream.flush()
            os.fchmod(descriptor, 0o500)
            seals = 0x0001 | 0x0002 | 0x0004 | 0x0008  # seal, shrink, grow, and write
            try:
                fcntl.fcntl(descriptor, 1033, seals)  # F_ADD_SEALS
            except OSError as error:
                raise PinnedEngineError(f"Could not seal the Linux engine snapshot: {error}") from error
            launch_target = f"/proc/self/fd/{descriptor}"
            if not os.path.exists(launch_target):
                raise PinnedEngineError("Linux procfs does not expose the sealed engine snapshot descriptor.")
            child_environment = os.environ.copy()
            for variable in tuple(child_environment):
                if variable.startswith("LD_"):
                    child_environment.pop(variable, None)
            child_environment.pop("URE_BOOTSTRAP", None)
            child_environment["LD_LIBRARY_PATH"] = str(runtime_directory_path)
            fundamental = runtime_directory_path / "fundamentalrc"
            if fundamental.is_file():
                child_environment["URE_BOOTSTRAP"] = fundamental.as_uri()
            launch_kwargs = {
                "executable": launch_target,
                "pass_fds": (descriptor, runtime_directory_descriptor),
                "cwd": str(runtime_directory_path),
                "env": child_environment,
            }
        else:
            raise PinnedEngineError("Pinned engine launch is supported only on Linux and Windows.")

        pinned_stat = os.fstat(stream.fileno())
        if not stat.S_ISREG(pinned_stat.st_mode):
            raise PinnedEngineError("The pinned engine handle is not a regular file.")
        if pinned_stat.st_size > MAX_ENGINE_SNAPSHOT_BYTES:
            raise PinnedEngineError(
                f"The selected engine exceeds the {MAX_ENGINE_SNAPSHOT_BYTES // (1024 * 1024)} MiB snapshot limit."
            )
        observed_sha256 = sha256_stream(stream)
        if observed_sha256.casefold() != expected_sha256.casefold():
            raise PinnedEngineError("The opened engine image does not match the trusted SHA-256 pin.", observed_sha256)
        if runtime_directory_path is None:
            raise PinnedEngineError("The selected engine has no runtime directory for component verification.")
        runtime_sha256 = runtime_directory_sha256(
            runtime_directory_path,
            Path(opened_engine_path).name,
            engine_file_identity,
        )
        identity_binding = (
            "sealed Linux memfd engine snapshot; sibling runtime directory held by descriptor and fingerprinted after each command"
            if sys.platform.startswith("linux")
            else "final engine path resolved from read-only Win32 handle; sibling runtime directory fingerprinted after each command"
        )
        result = PinnedEngine(
            stream,
            launch_target,
            launch_kwargs,
            observed_sha256,
            str(opened_engine_path),
            identity_binding,
            runtime_directory_descriptor,
            str(runtime_directory_path),
            runtime_sha256,
            engine_file_identity,
            source_stream,
        )
        stream = None
        runtime_directory_descriptor = None
        source_stream = None
        return result
    except Exception:
        if source_stream is not None:
            source_stream.close()
        if stream is not None:
            stream.close()
        elif descriptor is not None:
            try:
                os.close(descriptor)
            except OSError:
                pass
        if runtime_directory_descriptor is not None:
            os.close(runtime_directory_descriptor)
        raise


def deny_child_network() -> None:
    """Install a Linux x86_64 seccomp filter denying socket and io_uring syscalls."""
    if not sys.platform.startswith("linux") or platform.machine().lower() not in {"x86_64", "amd64"}:
        raise OSError("The no-network LibreOffice sandbox requires Linux x86_64 seccomp.")

    import ctypes
    import errno

    class SockFilter(ctypes.Structure):
        _fields_ = [("code", ctypes.c_ushort), ("jt", ctypes.c_ubyte), ("jf", ctypes.c_ubyte), ("k", ctypes.c_uint)]

    class SockFprog(ctypes.Structure):
        _fields_ = [("length", ctypes.c_ushort), ("filter", ctypes.POINTER(SockFilter))]

    instructions = [
        SockFilter(0x20, 0, 0, 4),
        SockFilter(0x15, 1, 0, 0xC000003E),
        SockFilter(0x06, 0, 0, 0x80000000),
        SockFilter(0x20, 0, 0, 0),
        SockFilter(0x45, 0, 1, 0x40000000),
        SockFilter(0x06, 0, 0, 0x00050000 | errno.EPERM),
    ]
    instructions.extend((
        # LibreOffice uses local Unix sockets for its own IPC. Only permit
        # creating those; Internet and other socket families remain blocked.
        SockFilter(0x15, 0, 3, 41),  # socket: skip domain check if not matched
        SockFilter(0x20, 0, 0, 16),   # seccomp_data.args[0] (domain)
        SockFilter(0x15, 1, 0, 1),    # AF_UNIX may proceed
        SockFilter(0x06, 0, 0, 0x00050000 | errno.EPERM),
        SockFilter(0x15, 0, 3, 53),  # socketpair: same family restriction
        SockFilter(0x20, 0, 0, 16),
        SockFilter(0x15, 1, 0, 1),
        SockFilter(0x06, 0, 0, 0x00050000 | errno.EPERM),
    ))
    # Socket lifecycle calls remain available for those local sockets. The
    # subprocess is launched with close_fds and only the pinned engine/runtime
    # descriptors passed through, so no pre-existing network socket is exposed.
    blocked_syscalls = (425, 426, 427)
    for syscall_number in blocked_syscalls:
        instructions.append(SockFilter(0x15, 0, 1, syscall_number))
        instructions.append(SockFilter(0x06, 0, 0, 0x00050000 | errno.EPERM))
    instructions.append(SockFilter(0x06, 0, 0, 0x7FFF0000))
    program = (SockFilter * len(instructions))(*instructions)
    filter_program = SockFprog(len(instructions), program)
    libc = ctypes.CDLL(None, use_errno=True)
    libc.prctl.argtypes = [ctypes.c_int, ctypes.c_ulong, ctypes.c_void_p, ctypes.c_ulong, ctypes.c_ulong]
    libc.prctl.restype = ctypes.c_int
    if libc.prctl(38, 1, None, 0, 0) != 0:
        error = ctypes.get_errno()
        raise OSError(error, os.strerror(error))
    if libc.prctl(22, 2, ctypes.byref(filter_program), 0, 0) != 0:
        error = ctypes.get_errno()
        raise OSError(error, os.strerror(error))


def run_bounded_process(
    command: list[str],
    *,
    timeout_seconds: float,
    max_log_chars: int,
    **popen_kwargs: Any,
) -> subprocess.CompletedProcess:
    """Drain child pipes continuously while retaining only bounded log prefixes."""
    captured: dict[str, str] = {"stdout": "", "stderr": ""}
    truncated = {"stdout": False, "stderr": False}
    streams: dict[str, Any] = {}
    popen_kwargs = dict(popen_kwargs)
    popen_kwargs.update({
        "stdout": subprocess.PIPE,
        "stderr": subprocess.PIPE,
        "text": True,
        "encoding": "utf-8",
        "errors": "replace",
    })
    if os.name == "posix":
        popen_kwargs["start_new_session"] = True
    process = subprocess.Popen(command, **popen_kwargs)
    streams["stdout"] = process.stdout
    streams["stderr"] = process.stderr

    def drain(name: str) -> None:
        pieces: list[str] = []
        kept = 0
        stream = streams[name]
        while True:
            chunk = stream.read(4096)
            if not chunk:
                break
            remaining = max_log_chars - kept
            if remaining > 0:
                retained = chunk[:remaining]
                pieces.append(retained)
                kept += len(retained)
            if len(chunk) > max(remaining, 0):
                truncated[name] = True
        captured[name] = "".join(pieces)

    readers = [threading.Thread(target=drain, args=(name,), daemon=True) for name in streams]
    for reader in readers:
        reader.start()
    timed_out = False
    try:
        process.wait(timeout=timeout_seconds)
    except subprocess.TimeoutExpired:
        timed_out = True
        if os.name == "posix":
            try:
                os.killpg(process.pid, signal.SIGKILL)
            except ProcessLookupError:
                pass
        else:
            process.kill()
        process.wait()
    for reader in readers:
        reader.join()
    for stream in streams.values():
        stream.close()
    for name in captured:
        if truncated[name]:
            marker = " [truncated]"
            captured[name] = captured[name][:max(0, max_log_chars - len(marker))] + marker[:max_log_chars]
    if timed_out:
        raise subprocess.TimeoutExpired(command, timeout_seconds, output=captured["stdout"], stderr=captured["stderr"])
    return subprocess.CompletedProcess(command, process.returncode, captured["stdout"], captured["stderr"])


def invoke(
    executable: Path,
    args: list[str],
    profile: Path,
    timeout_seconds: float,
    *,
    expected_engine_sha256: str | None = None,
    pinned_engine: PinnedEngine | None = None,
    max_output_file_bytes: int | None = None,
    max_log_chars: int | None = None,
    disable_network: bool = False,
    temporary_directory: Path | None = None,
    use_launcher: bool = False,
    deadline_monotonic: float | None = None,
) -> dict[str, Any]:
    profile.mkdir(parents=True, exist_ok=True)
    command = [
        str(executable),
        "--headless",
        "--nologo",
        "--nodefault",
        "--nofirststartwizard",
        f"-env:UserInstallation={profile.resolve().as_uri()}",
        *args,
    ]
    execution_target = str(executable)
    started = time.monotonic()
    owns_pinned_engine = pinned_engine is None
    launch_attempted = False
    completed_process: subprocess.CompletedProcess[str] | None = None
    try:
        if pinned_engine is None and expected_engine_sha256 is None:
            raise PinnedEngineError("No trusted engine SHA-256 pin was supplied before launch.")
        if pinned_engine is None:
            pinned_engine = open_pinned_engine(executable, expected_engine_sha256)
        execution_target = pinned_engine.launch_target
        command[0] = pinned_engine.launch_target
        pinned_engine.verify_runtime_directory(verify_engine_image=use_launcher)
        launch_kwargs = dict(pinned_engine.launch_kwargs)
        if use_launcher:
            if not sys.platform.startswith("linux") or pinned_engine.runtime_directory_path is None:
                raise PinnedEngineError("The LibreOffice launcher is supported only in a verified Linux runtime directory.")
            if temporary_directory is None:
                raise PinnedEngineError("The LibreOffice launcher requires an isolated temporary directory.")
            launcher_path = pinned_engine.create_launcher_overlay(temporary_directory / "lo-program")
            launcher_flags = os.O_RDONLY | getattr(os, "O_CLOEXEC", 0) | getattr(os, "O_NOFOLLOW", 0)
            launcher_fd = os.open(launcher_path, launcher_flags)
            try:
                launcher_status = os.fstat(launcher_fd)
                if not stat.S_ISREG(launcher_status.st_mode) or not (launcher_status.st_mode & 0o111):
                    raise PinnedEngineError("The verified LibreOffice runtime launcher is not an executable regular file.")
            finally:
                os.close(launcher_fd)
            execution_target = str(launcher_path)
            command[0] = execution_target
            launch_kwargs["executable"] = execution_target
        if temporary_directory is not None:
            temporary_directory.mkdir(parents=True, exist_ok=True)
            child_environment = dict(launch_kwargs.get("env", os.environ))
            for variable in ("TMPDIR", "TMP", "TEMP"):
                child_environment[variable] = str(temporary_directory.resolve())
            launch_kwargs["env"] = child_environment
        if disable_network and not sys.platform.startswith("linux"):
            raise PinnedEngineError("The corpus conversion requires the Linux no-network sandbox.")
        if (max_output_file_bytes is not None and sys.platform.startswith("linux")) or disable_network:
            def prepare_child() -> None:
                if max_output_file_bytes is not None:
                    import resource
                    resource.setrlimit(resource.RLIMIT_FSIZE, (max_output_file_bytes, max_output_file_bytes))
                if disable_network:
                    deny_child_network()

            launch_kwargs["preexec_fn"] = prepare_child
        effective_timeout = timeout_seconds
        if deadline_monotonic is not None:
            effective_timeout = min(effective_timeout, deadline_monotonic - time.monotonic())
            if effective_timeout <= 0:
                return {
                    "command": command,
                    "exitCode": None,
                    "stdout": "",
                    "stderr": "total runtime budget exhausted before LibreOffice launch",
                    "durationSeconds": round(time.monotonic() - started, 3),
                    "timedOut": False,
                    "launchAttempted": False,
                    "budgetExpiredBeforeLaunch": True,
                    "identityVerified": True,
                    "identitySha256": pinned_engine.sha256,
                    "identityBinding": pinned_engine.identity_binding,
                    "runtimeDirectorySha256": pinned_engine.verify_runtime_directory(verify_engine_image=use_launcher),
                    "requestedExecutablePath": str(executable),
                    "executionTarget": execution_target,
                }
        if max_log_chars is None:
            launch_attempted = True
            result = subprocess.run(
                command,
                **launch_kwargs,
                check=False,
                capture_output=True,
                text=True,
                encoding="utf-8",
                errors="replace",
                timeout=effective_timeout,
            )
        else:
            launch_attempted = True
            result = run_bounded_process(
                command,
                **launch_kwargs,
                timeout_seconds=effective_timeout,
                max_log_chars=max_log_chars,
            )
        completed_process = result
        return {
            "command": command,
            "exitCode": result.returncode,
            "stdout": result.stdout.strip(),
            "stderr": result.stderr.strip(),
            "durationSeconds": round(time.monotonic() - started, 3),
            "timedOut": False,
            "launchAttempted": True,
            "identityVerified": True,
            "identitySha256": pinned_engine.sha256,
            "identityBinding": pinned_engine.identity_binding,
            "runtimeDirectorySha256": pinned_engine.verify_runtime_directory(verify_engine_image=use_launcher),
            "requestedExecutablePath": str(executable),
            "executionTarget": execution_target,
        }
    except subprocess.TimeoutExpired as error:
        runtime_sha256 = None
        identity_failure = None
        try:
            if pinned_engine is None:
                raise PinnedEngineError("No pinned engine remained available to validate after the timed-out command.")
            runtime_sha256 = pinned_engine.verify_runtime_directory()
        except OSError as runtime_error:
            identity_failure = str(runtime_error)
        return {
            "command": command,
            "exitCode": None,
            "stdout": (error.stdout or b"").decode("utf-8", "replace").strip() if isinstance(error.stdout, bytes) else (error.stdout or "").strip(),
            "stderr": (error.stderr or b"").decode("utf-8", "replace").strip() if isinstance(error.stderr, bytes) else (error.stderr or "").strip(),
            "durationSeconds": round(time.monotonic() - started, 3),
            "timedOut": True,
            "launchAttempted": True,
            "identityVerified": identity_failure is None,
            "identitySha256": pinned_engine.sha256 if pinned_engine is not None else None,
            "identityBinding": pinned_engine.identity_binding if pinned_engine is not None else None,
            "runtimeDirectorySha256": runtime_sha256,
            "identityFailure": identity_failure,
            "requestedExecutablePath": str(executable),
            "executionTarget": execution_target,
        }
    except PinnedEngineError as error:
        completed_stderr = completed_process.stderr.strip() if completed_process is not None else ""
        if completed_process is not None:
            detail = f"identity verification failed: {error}"
            completed_stderr = f"{completed_stderr}\n{detail}" if completed_stderr else detail
            if max_log_chars is not None and len(completed_stderr) > max_log_chars:
                marker = " [truncated]"
                completed_stderr = completed_stderr[:max(0, max_log_chars - len(marker))] + marker[:max_log_chars]
        return {
            "command": command,
            "exitCode": completed_process.returncode if completed_process is not None else None,
            "stdout": completed_process.stdout.strip() if completed_process is not None else "",
            "stderr": completed_stderr or str(error),
            "durationSeconds": round(time.monotonic() - started, 3),
            "timedOut": False,
            "launchAttempted": launch_attempted,
            "identityVerified": False,
            "identitySha256": pinned_engine.sha256 if pinned_engine is not None else error.observed_sha256,
            "identityFailure": str(error),
            "runtimeDirectorySha256": pinned_engine.runtime_directory_sha256 if pinned_engine is not None else None,
            "requestedExecutablePath": str(executable),
            "executionTarget": execution_target,
        }
    except OSError as error:
        completed_stderr = completed_process.stderr.strip() if completed_process is not None else ""
        if completed_process is not None:
            detail = f"post-process verification failed: {error}"
            completed_stderr = f"{completed_stderr}\n{detail}" if completed_stderr else detail
            if max_log_chars is not None and len(completed_stderr) > max_log_chars:
                marker = " [truncated]"
                completed_stderr = completed_stderr[:max(0, max_log_chars - len(marker))] + marker[:max_log_chars]
        return {
            "command": command,
            "exitCode": completed_process.returncode if completed_process is not None else None,
            "stdout": completed_process.stdout.strip() if completed_process is not None else "",
            "stderr": completed_stderr or str(error),
            "durationSeconds": round(time.monotonic() - started, 3),
            "timedOut": False,
            "launchAttempted": launch_attempted,
            "identityVerified": False,
            "identityFailure": str(error),
            "runtimeDirectorySha256": pinned_engine.runtime_directory_sha256 if pinned_engine is not None else None,
            "requestedExecutablePath": str(executable),
            "executionTarget": execution_target,
        }
    except subprocess.SubprocessError as error:
        completed_stderr = completed_process.stderr.strip() if completed_process is not None else ""
        if completed_process is not None:
            detail = f"post-process verification failed: {error}"
            completed_stderr = f"{completed_stderr}\n{detail}" if completed_stderr else detail
            if max_log_chars is not None and len(completed_stderr) > max_log_chars:
                marker = " [truncated]"
                completed_stderr = completed_stderr[:max(0, max_log_chars - len(marker))] + marker[:max_log_chars]
        return {
            "command": command,
            "exitCode": completed_process.returncode if completed_process is not None else None,
            "stdout": completed_process.stdout.strip() if completed_process is not None else "",
            "stderr": completed_stderr or str(error),
            "durationSeconds": round(time.monotonic() - started, 3),
            "timedOut": False,
            "launchAttempted": launch_attempted,
            "identityVerified": False,
            "identityFailure": str(error),
            "runtimeDirectorySha256": pinned_engine.runtime_directory_sha256 if pinned_engine is not None else None,
            "requestedExecutablePath": str(executable),
            "executionTarget": execution_target,
        }
    finally:
        if owns_pinned_engine and pinned_engine is not None:
            pinned_engine.close()


def environment_metadata(include_device_identifiers: bool) -> dict[str, Any]:
    environment = {
        "operatingSystem": platform.platform(),
        "architecture": platform.machine(),
        "pythonImplementation": sys.implementation.name,
        "pythonVersion": platform.python_version(),
        "runtimeBitness": 8 * struct.calcsize("P"),
    }
    environment_id = os.environ.get("PAPERBATCH_PROBE_ENVIRONMENT_ID")
    if include_device_identifiers and environment_id:
        environment["environmentId"] = environment_id
    return environment


def unavailable_report(reason: str, include_device_identifiers: bool = False) -> dict[str, Any]:
    return {
        "schemaVersion": "1.0",
        "generatedAtUtc": datetime.now(timezone.utc).isoformat(),
        "environment": environment_metadata(include_device_identifiers),
        "libreOffice": {"status": "unavailable", "source": "environment", "limitation": reason},
        "export": {"status": "unavailable", "source": "native", "limitation": "No LibreOffice executable was available."},
        "coexistence": {"status": "unavailable", "source": "native", "limitation": "No controlled LibreOffice coexistence session was supplied; the probe does not attach to an existing Office/WPS process."},
        "nativeCompatibilityGate": "open",
        "validatedNativeBehaviors": [],
    }


def blocked_report(reason: str, executable: Path, include_device_identifiers: bool) -> dict[str, Any]:
    return {
        "schemaVersion": "1.0",
        "generatedAtUtc": datetime.now(timezone.utc).isoformat(),
        "environment": environment_metadata(include_device_identifiers),
        "libreOffice": {
            "status": "blocked",
            "source": "native",
            "executablePath": str(executable),
            "limitation": reason,
        },
        "export": {
            "status": "blocked",
            "source": "native",
            "limitation": "Export was skipped because the selected executable could not be inspected.",
        },
        "coexistence": {
            "status": "not-run",
            "source": "native",
            "limitation": "No controlled LibreOffice coexistence session was supplied.",
        },
        "nativeCompatibilityGate": "open",
        "validatedNativeBehaviors": [],
    }


def run_probe(
    launcher_binary: str | None,
    timeout_seconds: int,
    include_device_identifiers: bool = False,
    engine_binary: str | None = None,
    expected_engine_sha256: str | None = None,
) -> tuple[dict[str, Any], int]:
    selected_launcher = launcher_binary or shutil.which("soffice") or shutil.which("libreoffice")
    if not engine_binary:
        if selected_launcher:
            report = blocked_report(
                "A separate --engine-binary path and trusted --expected-engine-sha256 pin are required; the launcher is never executed.",
                Path(selected_launcher),
                include_device_identifiers,
            )
            report["libreOffice"]["launcherPath"] = selected_launcher
            return report, 1

        return unavailable_report(
            "No LibreOffice launcher was found and no explicit --engine-binary was supplied.",
            include_device_identifiers,
        ), 2

    selected_path = Path(engine_binary).expanduser()
    try:
        engine = selected_path.resolve(strict=True)
    except (FileNotFoundError, NotADirectoryError):
        return unavailable_report(
            f"The selected engine binary does not exist: {selected_path}",
            include_device_identifiers,
        ), 2
    except OSError as error:
        return blocked_report(str(error), selected_path, include_device_identifiers), 1

    try:
        engine_status = engine.stat()
    except (FileNotFoundError, NotADirectoryError):
        return unavailable_report(
            f"The selected engine binary does not exist: {engine}",
            include_device_identifiers,
        ), 2
    except OSError as error:
        return blocked_report(str(error), engine, include_device_identifiers), 1

    if not stat.S_ISREG(engine_status.st_mode):
        return blocked_report(
            "The selected engine path is not a regular file.",
            engine,
            include_device_identifiers,
        ), 1

    pin_limitation = None
    if expected_engine_sha256 is None:
        pin_limitation = "No trusted --expected-engine-sha256 pin was supplied; --version output alone cannot identify the engine binary."
    elif not re.fullmatch(r"[0-9a-fA-F]{64}", expected_engine_sha256):
        pin_limitation = "The supplied expected engine SHA-256 is not 64 hexadecimal characters."

    if pin_limitation:
        report = blocked_report(pin_limitation, engine, include_device_identifiers)
        report["libreOffice"]["engineBinaryPath"] = str(engine)
        report["libreOffice"]["engineSha256"] = None
        report["libreOffice"]["expectedEngineSha256"] = expected_engine_sha256
        report["libreOffice"]["launcherPath"] = selected_launcher
        return report, 1

    try:
        pinned_engine = open_pinned_engine(engine, expected_engine_sha256)
    except PinnedEngineError as error:
        report = blocked_report(str(error), engine, include_device_identifiers)
        report["libreOffice"].update({
            "engineBinaryPath": str(engine),
            "engineSha256": error.observed_sha256,
            "expectedEngineSha256": expected_engine_sha256.lower(),
            "launcherPath": selected_launcher,
        })
        return report, 1
    except OSError as error:
        return blocked_report(str(error), engine, include_device_identifiers), 1

    engine_sha256 = pinned_engine.sha256

    report: dict[str, Any] = {
        "schemaVersion": "1.0",
        "generatedAtUtc": datetime.now(timezone.utc).isoformat(),
        "environment": environment_metadata(include_device_identifiers),
        "libreOffice": {
            "status": "not-run",
            "source": "native",
            "engineBinaryPath": str(engine),
            "engineSha256": engine_sha256,
            "expectedEngineSha256": expected_engine_sha256.lower(),
            "identityVerification": "the directly invoked engine binary SHA-256 matched the caller-supplied trusted digest",
            "runtimeDirectorySha256": pinned_engine.runtime_directory_sha256,
            "runtimeDirectoryInspection": "bounded sibling program-directory manifest; only the top-level regular entry matching the pinned engine's opened-file identity is excluded",
            "launcherPath": selected_launcher,
        },
        "export": {"status": "not-run", "source": "native"},
        "coexistence": {
            "status": "not-run",
            "source": "native",
            "limitation": "A private profile is used for this export, but no controlled coexistence session was supplied; no existing Office/WPS process is used as a target.",
        },
        "nativeCompatibilityGate": "open",
        "validatedNativeBehaviors": [],
    }

    report["libreOffice"].update({
        "engineBinaryPath": pinned_engine.engine_path,
        "engineSha256": pinned_engine.sha256,
        "runtimeDirectoryBinding": "sibling program directory held by an open Linux descriptor; a bounded content manifest is checked after each command"
        if sys.platform.startswith("linux") else "engine path resolved from a read-only Win32 handle; a bounded sibling program-directory content manifest is checked after each command",
    })
    try:
        return run_export_with_pinned_engine(
            engine,
            timeout_seconds,
            expected_engine_sha256,
            report,
            pinned_engine,
        )
    finally:
        pinned_engine.close()


def run_export_with_pinned_engine(
    engine: Path,
    timeout_seconds: int,
    expected_engine_sha256: str,
    report: dict[str, Any],
    pinned_engine: PinnedEngine,
) -> tuple[dict[str, Any], int]:
    with tempfile.TemporaryDirectory(prefix="paperbatch-u0-lo-") as temporary_root:
        root = Path(temporary_root)
        version_result = invoke(
            engine,
            ["--version"],
            root / "version-profile",
            timeout_seconds,
            expected_engine_sha256=expected_engine_sha256,
            pinned_engine=pinned_engine,
        )
        version_output = version_result["stdout"] or version_result["stderr"]
        version_command_ok = (
            not version_result["timedOut"]
            and version_result["exitCode"] == 0
            and bool(version_output)
            and version_result.get("identityVerified", True)
        )
        version_identity_ok = bool(re.match(r"^LibreOffice(?:Dev)?\s+\S+", version_output, flags=re.IGNORECASE))
        version_ok = version_command_ok and version_identity_ok
        if not version_ok:
            limitation = (
                "The selected engine binary's --version output does not identify as LibreOffice or LibreOfficeDev."
                if version_command_ok and not version_identity_ok
                else version_result.get("identityFailure")
                or "The selected engine binary did not return a successful LibreOffice version identity."
            )
        report["libreOffice"] = {
            **report["libreOffice"],
            "status": "verified" if version_ok else "blocked",
            "version": version_output if version_ok else None,
            "versionCommand": version_result,
            "profileIsolation": "unique temporary UserInstallation URI",
        }
        if not version_ok:
            report["libreOffice"]["limitation"] = limitation
            report["export"] = {
                "status": "blocked",
                "source": "native",
                "limitation": "Export was skipped because the directly invoked engine binary was not verified as LibreOffice.",
            }
            return report, 1

        fixture = root / "input.docx"
        output_dir = root / "output"
        output_dir.mkdir()
        write_fixture(fixture)
        export_result = invoke(
            engine,
            ["--convert-to", "pdf", "--outdir", str(output_dir), str(fixture)],
            root / "export-profile",
            timeout_seconds,
            expected_engine_sha256=expected_engine_sha256,
            pinned_engine=pinned_engine,
        )
        pdf = output_dir / "input.pdf"
        (
            pdf_size,
            pdf_has_header,
            pdf_structure_valid,
            fixture_text_show_present,
            output_hash,
        ) = inspect_pdf(pdf)
        export_ok = (
            not export_result["timedOut"]
            and export_result["exitCode"] == 0
            and export_result.get("identityVerified", True)
            and pdf_structure_valid
            and fixture_text_show_present
            and output_hash is not None
        )
        failure_reasons: list[str] = []
        if export_result["timedOut"]:
            failure_reasons.append("The LibreOffice export command timed out.")
        exit_code = export_result.get("exitCode")
        if exit_code != 0:
            if exit_code is None:
                failure_reasons.append("The LibreOffice export command did not return an exit code.")
            else:
                failure_reasons.append(f"The LibreOffice export command exited with exit code {exit_code}.")
        if not export_result.get("identityVerified", True):
            failure_reasons.append(
                export_result.get("identityFailure")
                or "LibreOffice engine identity was not verified during export."
            )
        if not pdf_structure_valid:
            failure_reasons.append("The PDF output did not pass structural validation.")
        if not fixture_text_show_present:
            failure_reasons.append("The expected fixture text show operator was not found in page content.")
        if output_hash is None:
            failure_reasons.append("The PDF output could not be read and hashed.")
        report["export"] = {
            "status": "verified" if export_ok else "blocked",
            "sourceFormat": "DOCX",
            "targetFormat": "PDF",
            "inputSha256": sha256_file(fixture),
            "outputBytes": pdf_size,
            "outputSha256": output_hash,
            "outputHasPdfHeader": pdf_has_header,
            "outputStructureValid": pdf_structure_valid,
            "fixtureTextShowOperatorPresent": fixture_text_show_present,
            "command": export_result,
            "profileIsolation": "unique temporary UserInstallation URI",
            "limitation": None if export_ok else "; ".join(failure_reasons),
        }
        if export_ok:
            report["validatedNativeBehaviors"] = ["LibreOffice DOCX-to-PDF export"]

    exit_code = 0 if version_ok and export_ok else 1
    return report, exit_code


def find_last_pdf_keyword_position(data: bytes, keyword: bytes, limit: int) -> int | None:
    """Find an own-line PDF keyword token while ignoring matching text in comments."""
    position = data.rfind(keyword, 0, limit)
    while position >= 0:
        line_start = max(data.rfind(b"\n", 0, position), data.rfind(b"\r", 0, position)) + 1
        if not data[line_start:position].strip(PdfSyntaxReader._WHITESPACE):
            reader = PdfSyntaxReader(data, position, limit, allow_regular_keywords=True)
            try:
                kind, value = reader.read_value()
            except ValueError:
                kind, value = "", None
            if kind == "keyword" and value == keyword:
                return position
        position = data.rfind(keyword, 0, position)
    return None


def inspect_pdf(
    path: Path,
    *,
    expected_text: str | None = EXPECTED_FIXTURE_TEXT,
) -> tuple[int | None, bool, bool, bool, str | None]:
    try:
        with open_readonly_regular_file(path) as stream:
            file_status = os.fstat(stream.fileno())
            size = file_status.st_size
            if not stat.S_ISREG(file_status.st_mode):
                return size, False, False, False, None
            if size > MAX_PDF_INSPECTION_BYTES:
                return size, False, False, False, None
            data = stream.read(MAX_PDF_INSPECTION_BYTES + 1)
            if len(data) != size:
                return size, data.startswith(b"%PDF-"), False, False, None
    except OSError:
        return None, False, False, False, None

    output_sha256 = hashlib.sha256(data).hexdigest()
    if size < 32:
        return size, data.startswith(b"%PDF-"), False, False, output_sha256

    header = data[:8]
    if not re.fullmatch(rb"%PDF-\d\.\d", header):
        return size, header.startswith(b"%PDF-"), False, False, output_sha256

    eof_position = data.rfind(b"%%EOF")
    if eof_position < 0 or data[eof_position + len(b"%%EOF"):].strip(PDF_WHITESPACE):
        return size, True, False, False, output_sha256

    startxref_position = find_last_pdf_keyword_position(data, b"startxref", eof_position)
    if startxref_position is None:
        return size, True, False, False, output_sha256
    match = re.fullmatch(
        rb"startxref" + PDF_WHITESPACE_PATTERN + rb"+(\d+)",
        data[startxref_position:eof_position].strip(PDF_WHITESPACE),
    )
    if match is None:
        return size, True, False, False, output_sha256

    try:
        xref_offset = parse_bounded_pdf_integer(match.group(1))
    except ValueError:
        return size, True, False, False, output_sha256
    if xref_offset >= startxref_position:
        return size, True, False, False, output_sha256
    expected_content = []
    structure_valid = classic_xref_is_valid(
        data,
        xref_offset,
        startxref_position,
        expected_text=expected_text,
        expected_content_observation=expected_content,
    )
    return size, True, structure_valid, bool(expected_content and expected_content[0]), output_sha256


class PdfSyntaxReader:
    """Small bounded reader for PDF values used by the compatibility smoke check."""

    _WHITESPACE = PDF_WHITESPACE
    _DELIMITERS = b"()<>[]{}/%"

    def __init__(
        self,
        data: bytes,
        offset: int = 0,
        limit: int | None = None,
        *,
        allow_regular_keywords: bool = False,
    ) -> None:
        self.data = data
        self.position = offset
        self.limit = len(data) if limit is None else min(limit, len(data))
        self.tokens_read = 0
        self.allow_regular_keywords = allow_regular_keywords

    def _skip_space_and_comments(self) -> None:
        while self.position < self.limit:
            byte = self.data[self.position]
            if byte in self._WHITESPACE:
                self.position += 1
            elif byte == ord("%"):
                self.position += 1
                while self.position < self.limit and self.data[self.position] not in b"\r\n":
                    self.position += 1
            else:
                return

    def _read_name(self) -> bytes:
        if self.position >= self.limit or self.data[self.position] != ord("/"):
            raise ValueError("expected PDF name")
        self.position += 1
        value = bytearray()
        while self.position < self.limit:
            byte = self.data[self.position]
            if byte in self._WHITESPACE or byte in self._DELIMITERS:
                break
            if byte == ord("#"):
                if self.position + 2 >= self.limit:
                    raise ValueError("truncated PDF name escape")
                try:
                    value.append(int(self.data[self.position + 1:self.position + 3], 16))
                except ValueError as error:
                    raise ValueError("invalid PDF name escape") from error
                self.position += 3
                continue
            value.append(byte)
            self.position += 1
        return bytes(value)

    def _read_regular_token(self) -> bytes:
        start = self.position
        while self.position < self.limit:
            byte = self.data[self.position]
            if byte in self._WHITESPACE or byte in self._DELIMITERS:
                break
            self.position += 1
        if start == self.position:
            raise ValueError("expected PDF token")
        return self.data[start:self.position]

    def has_only_space_and_comments_remaining(self) -> bool:
        self._skip_space_and_comments()
        return self.position == self.limit

    def skip_space_and_comments(self) -> None:
        self._skip_space_and_comments()

    def read_regular_token(self) -> bytes:
        return self._read_regular_token()

    def _skip_literal_string(self) -> None:
        self.position += 1
        nesting = 1
        while self.position < self.limit:
            byte = self.data[self.position]
            self.position += 1
            if byte == ord("\\"):
                if self.position < self.limit:
                    if self.data[self.position] == ord("\r") and self.position + 1 < self.limit and self.data[self.position + 1] == ord("\n"):
                        self.position += 2
                    else:
                        self.position += 1
            elif byte == ord("("):
                nesting += 1
            elif byte == ord(")"):
                nesting -= 1
                if nesting == 0:
                    return
        raise ValueError("unterminated PDF literal string")

    def _skip_hex_string(self) -> None:
        self.position += 1
        while self.position < self.limit:
            byte = self.data[self.position]
            self.position += 1
            if byte == ord(">"):
                return
            if byte not in self._WHITESPACE and byte not in b"0123456789abcdefABCDEF":
                raise ValueError("invalid PDF hex string")
        raise ValueError("unterminated PDF hex string")

    def _read_array(self, depth: int) -> tuple[str, object]:
        self.position += 1
        values: list[tuple[str, object]] = []
        while True:
            self._skip_space_and_comments()
            if self.position >= self.limit:
                raise ValueError("unterminated PDF array")
            if self.data[self.position] == ord("]"):
                self.position += 1
                return "array", values
            values.append(self.read_value(depth + 1))

    def _read_dictionary(self, depth: int) -> tuple[str, object]:
        self.position += 2
        values: dict[bytes, tuple[str, object]] = {}
        while True:
            self._skip_space_and_comments()
            if self.position + 1 < self.limit and self.data[self.position:self.position + 2] == b">>":
                self.position += 2
                return "dictionary", values
            key = self._read_name()
            if key in values:
                raise ValueError("duplicate PDF dictionary key")
            values[key] = self.read_value(depth + 1)

    def read_value(self, depth: int = 0) -> tuple[str, object]:
        if depth > MAX_PDF_PARSE_DEPTH:
            raise ValueError("PDF value nesting limit exceeded")
        self.tokens_read += 1
        if self.tokens_read > MAX_PDF_PARSE_TOKENS:
            raise ValueError("PDF token limit exceeded")
        self._skip_space_and_comments()
        if self.position >= self.limit:
            raise ValueError("missing PDF value")

        if self.position + 1 < self.limit and self.data[self.position:self.position + 2] == b"<<":
            return self._read_dictionary(depth)
        byte = self.data[self.position]
        if byte == ord("["):
            return self._read_array(depth)
        if byte == ord("/"):
            return "name", self._read_name()
        if byte == ord("("):
            self._skip_literal_string()
            return "string", None
        if byte == ord("<"):
            self._skip_hex_string()
            return "string", None

        token = self._read_regular_token()
        if re.fullmatch(rb"[+-]?\d+", token):
            number = parse_bounded_pdf_integer(token)
            token_end = self.position
            try:
                self._skip_space_and_comments()
                second = self._read_regular_token()
                if not re.fullmatch(rb"\d+", second):
                    raise ValueError("not an indirect reference")
                generation = parse_bounded_pdf_integer(second)
                self._skip_space_and_comments()
                marker = self._read_regular_token()
                if marker != b"R":
                    raise ValueError("not an indirect reference")
                if number < 0 or generation > MAX_PDF_GENERATION:
                    raise ValueError("invalid PDF indirect reference")
                return "reference", (number, generation)
            except ValueError:
                self.position = token_end
                return "integer", number
        if PDF_REAL_NUMBER.fullmatch(token):
            return "real", token
        if token in (b"true", b"false", b"null") or self.allow_regular_keywords:
            return "keyword", token
        raise ValueError("invalid PDF value keyword")


def read_pdf_dictionary(data: bytes, offset: int = 0) -> dict[bytes, tuple[str, object]]:
    reader = PdfSyntaxReader(data, offset)
    reader._skip_space_and_comments()
    kind, value = reader.read_value()
    if kind != "dictionary":
        raise ValueError("expected PDF dictionary")
    if not reader.has_only_space_and_comments_remaining():
        raise ValueError("unexpected syntax after PDF dictionary")
    return value  # type: ignore[return-value]


def read_pdf_line(data: bytes, position: int) -> tuple[bytes, int] | None:
    if position >= len(data):
        return None

    line_feed = data.find(b"\n", position)
    carriage_return = data.find(b"\r", position)
    endings = [end for end in (line_feed, carriage_return) if end >= 0]
    if not endings:
        return data[position:], len(data)

    end = min(endings)
    next_position = end + 1
    if data[end:end + 1] == b"\r" and data[next_position:next_position + 1] == b"\n":
        next_position += 1
    return data[position:end], next_position


def classic_xref_is_valid(
    data: bytes,
    xref_offset: int,
    startxref_position: int,
    *,
    expected_text: str | None = None,
    expected_content_observation: list[bool] | None = None,
) -> bool:
    if data[xref_offset:xref_offset + 4] != b"xref":
        return False

    section = data[xref_offset:startxref_position]
    first_line = read_pdf_line(section, 0)
    if first_line is None or first_line[0].strip(PDF_WHITESPACE) != b"xref":
        return False

    entries: dict[int, tuple[int, int, bytes]] = {}
    position = first_line[1]
    trailer_dictionary: dict[bytes, tuple[str, object]] | None = None
    while position < len(section):
        current_line = read_pdf_line(section, position)
        if current_line is None:
            break
        line_bytes, position = current_line
        line = line_bytes.strip(PDF_WHITESPACE)
        if line == b"trailer":
            try:
                trailer_dictionary = read_pdf_dictionary(section, position)
            except ValueError:
                return False
            break

        subsection = re.fullmatch(
            rb"(\d+)" + PDF_WHITESPACE_PATTERN + rb"+(\d+)",
            line,
        )
        if subsection is None:
            return False
        try:
            first_object = parse_bounded_pdf_integer(subsection.group(1))
            entry_count = parse_bounded_pdf_integer(subsection.group(2))
        except ValueError:
            return False
        if entry_count < 1 or first_object + entry_count > MAX_PDF_OBJECTS:
            return False

        for entry_index in range(entry_count):
            entry_line = read_pdf_line(section, position)
            if entry_line is None:
                return False
            row_bytes, position = entry_line
            row = row_bytes.strip(PDF_WHITESPACE)
            match = re.fullmatch(
                rb"(\d{10})" + PDF_WHITESPACE_PATTERN + rb"+(\d{5})"
                + PDF_WHITESPACE_PATTERN + rb"+([nf])",
                row,
            )
            object_number = first_object + entry_index
            if match is None or object_number in entries:
                return False
            object_generation = parse_bounded_pdf_integer(match.group(2))
            if object_generation > MAX_PDF_GENERATION:
                return False
            entries[object_number] = (
                parse_bounded_pdf_integer(match.group(1)),
                object_generation,
                match.group(3),
            )

    if trailer_dictionary is None:
        return False
    object_zero_entry = entries.get(0)
    if object_zero_entry is None or object_zero_entry[1] != 65535 or object_zero_entry[2] != b"f":
        return False
    size_value = trailer_dictionary.get(b"Size")
    root_value = trailer_dictionary.get(b"Root")
    if size_value is None or size_value[0] != "integer" or root_value is None or root_value[0] != "reference":
        return False

    object_count = size_value[1]
    root_number, root_generation = root_value[1]
    if object_count < 1 or object_count > MAX_PDF_OBJECTS or root_number >= object_count:
        return False
    if entries and max(entries) >= object_count:
        return False

    for object_number, (object_offset, generation, state) in entries.items():
        if state != b"n":
            continue
        if object_offset >= xref_offset:
            return False
        object_header = PDF_INDIRECT_OBJECT_HEADER.match(data, object_offset)
        if object_header is None:
            return False
        try:
            header_object_number = parse_bounded_pdf_integer(object_header.group(1))
            header_generation = parse_bounded_pdf_integer(object_header.group(2))
        except ValueError:
            return False
        if header_object_number != object_number or header_generation != generation or header_generation > MAX_PDF_GENERATION:
            return False

    if not classic_xref_object_layout_is_valid(data, entries, xref_offset):
        return False

    root_entry = entries.get(root_number)
    if root_entry is None or root_entry[1] != root_generation or root_entry[2] != b"n":
        return False
    root_offset = root_entry[0]
    root_end = data.find(b"endobj", root_offset, xref_offset)
    if root_end < 0:
        return False
    root_object = data[root_offset:root_end]
    object_body = PDF_INDIRECT_OBJECT_HEADER.match(root_object)
    if object_body is None:
        return False
    try:
        root_dictionary = read_pdf_dictionary(root_object, object_body.end())
    except ValueError:
        return False
    root_type = root_dictionary.get(b"Type")
    if root_type != ("name", b"Catalog"):
        return False
    page_text_observation: list[bool] = []
    has_page_content = pdf_page_tree_has_nonempty_content(
        data,
        entries,
        xref_offset,
        root_dictionary,
        expected_text=expected_text,
        expected_text_observation=page_text_observation,
    )
    if expected_content_observation is not None:
        expected_content_observation.append(
            has_page_content
            and expected_text is not None
            and bool(page_text_observation and page_text_observation[0])
        )
    return has_page_content


def read_indirect_dictionary(
    data: bytes,
    entries: dict[int, tuple[int, int, bytes]],
    reference: tuple[int, int],
    xref_offset: int,
) -> dict[bytes, tuple[str, object]] | None:
    object_number, generation = reference
    entry = entries.get(object_number)
    if entry is None or entry[1] != generation or entry[2] != b"n":
        return None
    object_offset = entry[0]
    header = PDF_INDIRECT_OBJECT_HEADER.match(data, object_offset, xref_offset)
    if header is None:
        return None
    try:
        if parse_bounded_pdf_integer(header.group(1)) != object_number:
            return None
        if parse_bounded_pdf_integer(header.group(2)) != generation:
            return None
    except ValueError:
        return None
    body_start = header.end()
    object_end = data.find(b"endobj", body_start, xref_offset)
    if object_end < 0:
        return None
    try:
        return read_pdf_dictionary(data[body_start:object_end])
    except ValueError:
        return None


def read_indirect_simple_value(
    data: bytes,
    entries: dict[int, tuple[int, int, bytes]],
    reference: tuple[int, int],
    xref_offset: int,
) -> tuple[str, object] | None:
    object_number, generation = reference
    entry = entries.get(object_number)
    if entry is None or entry[1] != generation or entry[2] != b"n":
        return None
    object_offset = entry[0]
    header = PDF_INDIRECT_OBJECT_HEADER.match(data, object_offset, xref_offset)
    if header is None:
        return None
    try:
        if parse_bounded_pdf_integer(header.group(1)) != object_number:
            return None
        if parse_bounded_pdf_integer(header.group(2)) != generation:
            return None
    except ValueError:
        return None
    body_start = header.end()
    object_end = data.find(b"endobj", body_start, xref_offset)
    if object_end < 0:
        return None
    reader = PdfSyntaxReader(data[body_start:object_end])
    try:
        value = reader.read_value()
    except ValueError:
        return None
    return value if reader.has_only_space_and_comments_remaining() else None


def read_indirect_stream_payload(
    data: bytes,
    entries: dict[int, tuple[int, int, bytes]],
    reference: tuple[int, int],
    xref_offset: int,
) -> bytes | None:
    stream = read_indirect_stream(data, entries, reference, xref_offset)
    return None if stream is None else stream[1]


def read_indirect_stream(
    data: bytes,
    entries: dict[int, tuple[int, int, bytes]],
    reference: tuple[int, int],
    xref_offset: int,
) -> tuple[dict[bytes, tuple[str, object]], bytes] | None:
    stream = read_indirect_stream_with_end(data, entries, reference, xref_offset)
    return None if stream is None else (stream[0], stream[1])


def read_indirect_stream_with_end(
    data: bytes,
    entries: dict[int, tuple[int, int, bytes]],
    reference: tuple[int, int],
    xref_offset: int,
) -> tuple[dict[bytes, tuple[str, object]], bytes, int] | None:
    object_number, generation = reference
    entry = entries.get(object_number)
    if entry is None or entry[1] != generation or entry[2] != b"n":
        return None
    object_offset = entry[0]
    header = PDF_INDIRECT_OBJECT_HEADER.match(data, object_offset, xref_offset)
    if header is None:
        return None
    try:
        if parse_bounded_pdf_integer(header.group(1)) != object_number:
            return None
        if parse_bounded_pdf_integer(header.group(2)) != generation:
            return None
    except ValueError:
        return None

    reader = PdfSyntaxReader(data, header.end(), xref_offset)
    try:
        kind, value = reader.read_value()
        if kind != "dictionary":
            return None
        stream_dictionary = value
        reader.skip_space_and_comments()
        if reader.read_regular_token() != b"stream":
            return None
    except ValueError:
        return None

    if data[reader.position:reader.position + 2] == b"\r\n":
        stream_start = reader.position + 2
    elif data[reader.position:reader.position + 1] in (b"\r", b"\n"):
        stream_start = reader.position + 1
    else:
        return None

    stream_length = stream_dictionary.get(b"Length")
    if stream_length is None:
        return None
    if stream_length[0] == "reference":
        stream_length = read_indirect_simple_value(data, entries, stream_length[1], xref_offset)
    if stream_length is None or stream_length[0] != "integer" or stream_length[1] < 0:
        return None
    stream_end = stream_start + stream_length[1]
    if stream_end > xref_offset:
        return None

    marker_start = stream_end
    if data[marker_start:marker_start + 2] == b"\r\n":
        marker_start += 2
    elif data[marker_start:marker_start + 1] in (b"\r", b"\n"):
        marker_start += 1
    if data[marker_start:marker_start + len(b"endstream")] != b"endstream":
        return None
    object_end_reader = PdfSyntaxReader(data, marker_start + len(b"endstream"), xref_offset)
    try:
        object_end_reader.skip_space_and_comments()
        if object_end_reader.position >= object_end_reader.limit:
            return None
        if object_end_reader.read_regular_token() != b"endobj":
            return None
    except ValueError:
        return None
    return stream_dictionary, data[stream_start:stream_end], object_end_reader.position


def classic_xref_object_layout_is_valid(
    data: bytes,
    entries: dict[int, tuple[int, int, bytes]],
    xref_offset: int,
) -> bool:
    objects = sorted(
        (entry[0], object_number, entry)
        for object_number, entry in entries.items()
        if entry[2] == b"n"
    )
    if not objects:
        return False
    header_line = read_pdf_line(data, 0)
    if header_line is None:
        return False
    prefix_reader = PdfSyntaxReader(data, header_line[1], objects[0][0])
    if not prefix_reader.has_only_space_and_comments_remaining():
        return False

    for index, (object_offset, object_number, entry) in enumerate(objects):
        next_offset = objects[index + 1][0] if index + 1 < len(objects) else xref_offset
        header = PDF_INDIRECT_OBJECT_HEADER.match(data, object_offset, xref_offset)
        if header is None:
            return False
        try:
            if parse_bounded_pdf_integer(header.group(1)) != object_number:
                return False
            if parse_bounded_pdf_integer(header.group(2)) != entry[1]:
                return False
            reader = PdfSyntaxReader(data, header.end(), xref_offset)
            value_kind, _value = reader.read_value()
            reader.skip_space_and_comments()
            following_token = reader.read_regular_token()
        except ValueError:
            return False

        if value_kind == "dictionary" and following_token == b"stream":
            stream = read_indirect_stream_with_end(
                data,
                entries,
                (object_number, entry[1]),
                xref_offset,
            )
            if stream is None:
                return False
            object_end = stream[2]
        elif following_token == b"endobj":
            object_end = reader.position
        else:
            return False

        if object_end >= next_offset or next_offset > xref_offset:
            return False
        gap_reader = PdfSyntaxReader(data, object_end, next_offset)
        if not gap_reader.has_only_space_and_comments_remaining():
            return False

    return True


def decode_pdf_stream(dictionary: dict[bytes, tuple[str, object]], payload: bytes) -> bytes | None:
    filter_value = dictionary.get(b"Filter")
    if filter_value is None:
        return payload if len(payload) <= MAX_PDF_DECODED_STREAM_BYTES else None
    if filter_value != ("name", b"FlateDecode"):
        return None

    try:
        decompressor = zlib.decompressobj()
        decoded = decompressor.decompress(payload, MAX_PDF_DECODED_STREAM_BYTES + 1)
        if len(decoded) > MAX_PDF_DECODED_STREAM_BYTES or decompressor.unconsumed_tail or not decompressor.eof:
            return None
        decoded += decompressor.flush()
        return decoded if len(decoded) <= MAX_PDF_DECODED_STREAM_BYTES else None
    except zlib.error:
        return None


def _read_pdf_content_string(data: bytes, start: int) -> tuple[int, str, object] | None:
    if data[start] == ord("<"):
        if data[start:start + 2] == b"<<":
            return start + 2, "other", b"<<"
        end = data.find(b">", start + 1)
        if end < 0:
            return None
        hex_data = re.sub(rb"[\x00\x09\x0a\x0c\x0d\x20]", b"", data[start + 1:end])
        if not re.fullmatch(rb"[0-9a-fA-F]*", hex_data):
            return end + 1, "other", b""
        if len(hex_data) % 2:
            hex_data += b"0"
        try:
            return end + 1, "string", ("hex", bytes.fromhex(hex_data.decode("ascii")))
        except ValueError:
            return end + 1, "other", b""

    index = start + 1
    nesting = 1
    value = bytearray()
    while index < len(data) and nesting:
        byte = data[index]
        index += 1
        if byte == ord("\\"):
            if index >= len(data):
                return None
            escaped = data[index]
            index += 1
            simple_escapes = {
                ord("n"): ord("\n"), ord("r"): ord("\r"), ord("t"): ord("\t"),
                ord("b"): ord("\b"), ord("f"): ord("\f"),
            }
            if escaped in simple_escapes:
                value.append(simple_escapes[escaped])
            elif escaped in (ord("\n"), ord("\r")):
                if escaped == ord("\r") and index < len(data) and data[index] == ord("\n"):
                    index += 1
            elif ord("0") <= escaped <= ord("7"):
                octal = bytearray((escaped,))
                for _ in range(2):
                    if index < len(data) and ord("0") <= data[index] <= ord("7"):
                        octal.append(data[index])
                        index += 1
                    else:
                        break
                value.append(int(octal, 8) & 0xFF)
            else:
                value.append(escaped)
        elif byte == ord("("):
            nesting += 1
            value.append(byte)
        elif byte == ord(")"):
            nesting -= 1
            if nesting:
                value.append(byte)
        else:
            value.append(byte)
    return (index, "string", ("literal", bytes(value))) if nesting == 0 else None


def _pdf_content_tokens(data: bytes) -> list[tuple[str, object]]:
    tokens: list[tuple[str, object]] = []
    delimiters = b"\x00\x09\x0a\x0c\x0d ()<>[]{}/%{}"
    index = 0

    def append_token(kind: str, value: object) -> None:
        if len(tokens) >= MAX_PDF_PARSE_TOKENS:
            raise PdfContentLimitError("PDF page content exceeds the token inspection limit")
        tokens.append((kind, value))

    while index < len(data):
        byte = data[index]
        if byte in b"\x00\x09\x0a\x0c\x0d\x20":
            index += 1
            continue
        if byte == ord("%"):
            endings = [position for position in (data.find(b"\n", index + 1), data.find(b"\r", index + 1)) if position >= 0]
            index = min(endings) + 1 if endings else len(data)
            continue
        if byte in (ord("("), ord("<")):
            parsed = _read_pdf_content_string(data, index)
            if parsed is None:
                break
            index, kind, value = parsed
            append_token(kind, value)
            continue
        if byte == ord("/"):
            index += 1
            start = index
            while index < len(data) and data[index] not in delimiters:
                index += 1
            name = re.sub(rb"#([0-9a-fA-F]{2})", lambda m: bytes((int(m.group(1), 16),)), data[start:index])
            append_token("name", name)
            continue
        if byte == ord("["):
            index += 1
            append_token("array-start", None)
            continue
        if byte == ord("]"):
            index += 1
            append_token("array-end", None)
            continue
        if data[index:index + 2] == b">>":
            index += 2
            append_token("other", b">>")
            continue
        if byte == ord(")"):
            index += 1
            append_token("other", b")")
            continue

        start = index
        while index < len(data) and data[index] not in delimiters:
            index += 1
        if start == index:
            index += 1
            append_token("other", bytes((byte,)))
            continue
        word = data[start:index]
        if re.fullmatch(rb"[+-]?(?:\d+(?:\.\d*)?|\.\d+)", word):
            append_token("number", word)
        elif word in (b"true", b"false", b"null"):
            append_token("operand", word)
        elif word == b"BI":
            # Inline-image data is binary and can contain arbitrary operator-looking bytes.
            # Fail closed for the remainder of this stream rather than misread it as text.
            append_token("inline-image", None)
            break
        else:
            append_token("word", word)
    return tokens


def extract_pdf_shown_string_tokens(data: bytes) -> list[tuple[bytes | None, str, bytes]]:
    shown: list[tuple[bytes | None, str, bytes]] = []
    operands: list[tuple[str, object]] = []
    arrays: list[list[tuple[str, object]]] = []
    current_font: bytes | None = None
    font_size: float | None = None
    render_mode = 0
    graphics_state_stack: list[tuple[bytes | None, float | None, int]] = []
    in_text_object = False

    def add_operand(kind: str, value: object) -> None:
        (arrays[-1] if arrays else operands).append((kind, value))

    def add_shown(item: tuple[str, object]) -> None:
        if font_size is None or font_size <= 0 or current_font is None or render_mode in (3, 7):
            return
        items = item[1] if item[0] == "array" else [item]
        for text_item in items:
            if text_item[0] == "string":
                token_kind, value = text_item[1]
                shown.append((current_font, token_kind, value))

    for kind, value in _pdf_content_tokens(data):
        if kind == "inline-image":
            break
        if kind == "array-start":
            arrays.append([])
            continue
        if kind == "array-end":
            if not arrays:
                operands.clear()
                continue
            array = arrays.pop()
            (arrays[-1] if arrays else operands).append(("array", array))
            continue
        if kind in ("string", "name", "number", "operand"):
            add_operand(kind, value)
            continue
        if kind != "word" or arrays:
            operands.clear()
            continue

        if value == b"q":
            graphics_state_stack.append((current_font, font_size, render_mode))
        elif value == b"Q":
            if graphics_state_stack:
                current_font, font_size, render_mode = graphics_state_stack.pop()
            else:
                current_font, font_size, render_mode = None, None, 3
        elif value == b"BT":
            in_text_object = True
        elif value == b"ET":
            in_text_object = False
        elif value == b"Tf":
            if in_text_object:
                if len(operands) >= 2 and operands[-2][0] == "name" and operands[-1][0] == "number":
                    try:
                        font_size = float(operands[-1][1])
                        current_font = operands[-2][1] if font_size > 0 else None
                    except (OverflowError, ValueError):
                        current_font, font_size = None, None
                else:
                    current_font, font_size = None, None
        elif value == b"Tr":
            if in_text_object:
                if operands and operands[-1][0] == "number":
                    try:
                        mode = float(operands[-1][1])
                        render_mode = int(mode) if mode.is_integer() and 0 <= mode <= 7 else 3
                    except (OverflowError, ValueError):
                        render_mode = 3
                else:
                    render_mode = 3
        elif (
            value in (b"Tj", b"'")
            and in_text_object
            and len(operands) == 1
            and operands[-1][0] == "string"
        ):
            add_shown(operands[-1])
        elif (
            value == b"TJ"
            and in_text_object
            and len(operands) == 1
            and operands[-1][0] == "array"
        ):
            array_items = operands[-1][1]
            if all(item[0] in ("string", "number") for item in array_items):
                add_shown(operands[-1])
        elif (
            value == b'"'
            and in_text_object
            and len(operands) == 3
            and operands[-3][0] == "number"
            and operands[-2][0] == "number"
            and operands[-1][0] == "string"
        ):
            add_shown(operands[-1])
        operands.clear()
    return shown


def skip_postscript_literal_string(stream: bytes, position: int) -> int:
    depth = 1
    position += 1
    while position < len(stream):
        byte = stream[position]
        if byte == ord("\\"):
            position += 1
            if position < len(stream):
                if stream[position] == ord("\r") and position + 1 < len(stream) and stream[position + 1] == ord("\n"):
                    position += 2
                else:
                    position += 1
            continue
        if byte == ord("("):
            depth += 1
        elif byte == ord(")"):
            depth -= 1
            if depth == 0:
                return position + 1
        position += 1
    return len(stream)


def tokenize_cmap(stream: bytes) -> list[tuple[str, bytes]]:
    whitespace = b"\x00\x09\x0a\x0c\x0d\x20"
    delimiters = b"()<>[]{}/%{}"
    tokens: list[tuple[str, bytes]] = []
    containers: list[bytes] = []
    position = 0

    while position < len(stream) and len(tokens) < MAX_PDF_PARSE_TOKENS:
        byte = stream[position]
        if byte in whitespace:
            position += 1
            continue
        if byte == ord("%"):
            position += 1
            while position < len(stream) and stream[position] not in b"\r\n":
                position += 1
            continue
        if byte == ord("("):
            position = skip_postscript_literal_string(stream, position)
            continue
        if byte == ord("<") and position + 1 < len(stream) and stream[position + 1] == ord("<"):
            if len(containers) >= MAX_PDF_PARSE_DEPTH:
                return tokens
            containers.append(b">>")
            position += 2
            continue
        if byte == ord(">") and position + 1 < len(stream) and stream[position + 1] == ord(">"):
            if not containers or containers[-1] != b">>":
                return tokens
            containers.pop()
            position += 2
            continue
        if byte == ord("<"):
            end = stream.find(b">", position + 1)
            if end < 0:
                return tokens
            if not containers:
                tokens.append(("hex", stream[position + 1:end]))
            position = end + 1
            continue
        if byte in (ord("{"), ord("[")):
            if len(containers) >= MAX_PDF_PARSE_DEPTH:
                return tokens
            containers.append(b"}" if byte == ord("{") else b"]")
            position += 1
            continue
        if byte in (ord("}"), ord("]")):
            if not containers or containers[-1] != bytes((byte,)):
                return tokens
            containers.pop()
            position += 1
            continue
        if byte in (ord(">"), ord(")")):
            return tokens
        if byte == ord("/"):
            start = position + 1
            position = start
            while position < len(stream) and stream[position] not in whitespace and stream[position] not in delimiters:
                position += 1
            if not containers:
                tokens.append(("name", stream[start:position]))
            continue

        start = position
        while position < len(stream) and stream[position] not in whitespace and stream[position] not in delimiters:
            position += 1
        if start == position:
            position += 1
        elif not containers:
            tokens.append(("word", stream[start:position]))

    if position < len(stream):
        return []
    return tokens


def parse_tounicode_bfchar_map(stream: bytes) -> dict[bytes, str | None]:
    tokens = tokenize_cmap(stream)
    body_tokens = cmap_body_tokens(tokens)
    if body_tokens is None:
        return {}
    return parse_tounicode_bfchar_tokens(body_tokens)


def cmap_body_tokens(tokens: list[tuple[str, bytes]]) -> list[tuple[str, bytes]] | None:
    begins = [index for index, token in enumerate(tokens) if token == ("word", b"begincmap")]
    ends = [index for index, token in enumerate(tokens) if token == ("word", b"endcmap")]
    if len(begins) != 1 or len(ends) != 1 or begins[0] >= ends[0]:
        return None
    return tokens[begins[0] + 1 : ends[0]]


def parse_tounicode_bfchar_tokens(tokens: list[tuple[str, bytes]]) -> dict[bytes, str | None]:
    unicode_map: dict[bytes, str | None] = {}
    for index, token in enumerate(tokens):
        if token != ("word", b"beginbfchar") or index == 0:
            continue
        count_token = tokens[index - 1]
        if count_token[0] != "word" or re.fullmatch(rb"[0-9]{1,20}", count_token[1]) is None:
            continue
        count = int(count_token[1])
        if count > MAX_PDF_PARSE_TOKENS // 2:
            continue
        cursor = index + 1
        entries: list[tuple[bytes, str]] = []
        for _ in range(count):
            if cursor + 1 >= len(tokens) or tokens[cursor][0] != "hex" or tokens[cursor + 1][0] != "hex":
                entries = []
                break
            try:
                source_bytes = bytes.fromhex(tokens[cursor][1].decode("ascii"))
                target_text = bytes.fromhex(tokens[cursor + 1][1].decode("ascii")).decode("utf-16-be")
            except (UnicodeDecodeError, ValueError):
                entries = []
                break
            if not source_bytes:
                entries = []
                break
            entries.append((source_bytes, target_text))
            cursor += 2
        if cursor >= len(tokens) or tokens[cursor] != ("word", b"endbfchar") or len(entries) != count:
            continue
        for source_bytes, target_text in entries:
            if source_bytes not in unicode_map:
                unicode_map[source_bytes] = target_text
            elif unicode_map[source_bytes] != target_text:
                unicode_map[source_bytes] = None
    return unicode_map


def decode_pdf_shown_text(
    value: bytes,
    unicode_map: dict[bytes, str | None],
    *,
    allow_ascii: bool = False,
    code_width: int | None = None,
) -> str:
    if not unicode_map:
        return value.decode("ascii") if allow_ascii and value.isascii() else "\ufffd"
    source_widths = {len(source) for source in unicode_map if source}
    if code_width is None:
        if len(source_widths) != 1:
            return "\ufffd"
        code_width = next(iter(source_widths))
    if code_width < 1 or source_widths != {code_width} or len(value) % code_width:
        return "\ufffd"

    decoded: list[str] = []
    for position in range(0, len(value), code_width):
        source = value[position:position + code_width]
        decoded.append(unicode_map.get(source) or "\ufffd")
    return "".join(decoded)


def pdf_streams_contain_expected_text(
    streams: list[bytes],
    expected_text: str,
    font_cmaps: dict[bytes, bytes] | None = None,
    font_names: set[bytes] | None = None,
    ascii_font_names: set[bytes] | None = None,
    parsed_cmap_cache: dict[bytes, dict[bytes, str | None] | None] | None = None,
    font_code_widths: dict[bytes, int] | None = None,
) -> bool:
    total_content_bytes = sum(map(len, streams))
    if total_content_bytes > MAX_PDF_PAGE_CONTENT_BYTES:
        return False

    maps_by_font: dict[bytes, dict[bytes, str | None]] = {}
    parsed_maps_by_cmap = parsed_cmap_cache if parsed_cmap_cache is not None else {}
    total_cmap_bytes = 0
    for font_name, cmap in (font_cmaps or {}).items():
        if cmap not in parsed_maps_by_cmap:
            total_cmap_bytes += len(cmap)
            if total_cmap_bytes > MAX_PDF_TOTAL_CMAP_BYTES:
                return False
            parsed_maps_by_cmap[cmap] = parse_tounicode_bfchar_map(cmap)
        cmap_map = parsed_maps_by_cmap[cmap]
        if cmap_map is None:
            return False
        maps_by_font[font_name] = cmap_map
    known_fonts = font_names or set()
    ascii_fonts = ascii_font_names or set()
    decoded_parts = []
    try:
        shown_strings = extract_pdf_shown_string_tokens(b"".join(streams))
    except PdfContentLimitError:
        return False
    for font_name, _token_kind, value in shown_strings:
        if font_name is None or font_name not in known_fonts:
            continue
        decoded_parts.append(
            decode_pdf_shown_text(
                value,
                maps_by_font.get(font_name, {}),
                allow_ascii=font_name in ascii_fonts,
                code_width=(font_code_widths or {}).get(font_name),
            )
        )
    normalize = lambda value: " ".join(value.split()).casefold()
    return normalize(expected_text) in normalize("".join(decoded_parts))


def pdf_content_references_contain_expected_text(
    data: bytes,
    entries: dict[int, tuple[int, int, bytes]],
    xref_offset: int,
    content_references: list[tuple[int, int]],
    font_names: set[bytes],
    ascii_font_names: set[bytes],
    font_cmap_references: list[tuple[bytes, tuple[int, int]]],
    expected_text: str,
    font_code_widths: dict[bytes, int] | None = None,
    cmap_state: PdfCMapInspectionState | None = None,
    content_state: PdfContentInspectionState | None = None,
) -> bool:
    page_streams = []
    total_content_bytes = 0
    state = content_state or PdfContentInspectionState()
    for reference in content_references:
        decoded = state.read_content(data, entries, reference, xref_offset)
        if decoded is None:
            return False
        total_content_bytes += len(decoded)
        if state.exceeded or total_content_bytes > MAX_PDF_PAGE_CONTENT_BYTES:
            return False
        page_streams.append(decoded)

    font_cmaps: dict[bytes, bytes] = {}
    cmap_state = cmap_state or PdfCMapInspectionState()
    for font_name, reference in font_cmap_references:
        decoded = cmap_state.read_cmap(data, entries, reference, xref_offset)
        if cmap_state.exceeded:
            return False
        if decoded is not None and cmap_state.parsed_map(decoded) is not None:
            font_cmaps[font_name] = decoded
    return pdf_streams_contain_expected_text(
        page_streams,
        expected_text,
        font_cmaps,
        font_names,
        ascii_font_names,
        cmap_state.parsed_by_cmap,
        font_code_widths,
    )


def resolve_pdf_integer(
    value: tuple[str, object],
    data: bytes,
    entries: dict[int, tuple[int, int, bytes]],
    xref_offset: int,
) -> int | None:
    if value[0] == "reference":
        resolved = read_indirect_simple_value(data, entries, value[1], xref_offset)
        if resolved is None:
            return None
        value = resolved
    return value[1] if value[0] == "integer" else None  # type: ignore[return-value]


def pdf_page_tree_has_nonempty_content(
    data: bytes,
    entries: dict[int, tuple[int, int, bytes]],
    xref_offset: int,
    catalog: dict[bytes, tuple[str, object]],
    *,
    expected_text: str | None = None,
    expected_text_observation: list[bool] | None = None,
) -> bool:
    pages_reference = catalog.get(b"Pages")
    if pages_reference is None or pages_reference[0] != "reference":
        return False
    visited: set[tuple[int, int]] = set()
    found_expected_text = False
    cmap_state = PdfCMapInspectionState()
    content_state = PdfContentInspectionState()

    def resolve_dictionary(value: tuple[str, object] | None) -> dict[bytes, tuple[str, object]] | None:
        if value is None:
            return None
        if value[0] == "dictionary":
            return value[1]
        if value[0] == "reference":
            return read_indirect_dictionary(data, entries, value[1], xref_offset)
        return None

    def capture_page_fonts(
        resources_value: tuple[str, object] | None,
    ) -> tuple[set[bytes], set[bytes], list[tuple[bytes, tuple[int, int]]], dict[bytes, int]]:
        font_names: set[bytes] = set()
        ascii_font_names: set[bytes] = set()
        references: list[tuple[bytes, tuple[int, int]]] = []
        code_widths: dict[bytes, int] = {}
        resources = resolve_dictionary(resources_value)
        if resources is None:
            return font_names, ascii_font_names, references, code_widths
        fonts = resolve_dictionary(resources.get(b"Font"))
        if fonts is None:
            return font_names, ascii_font_names, references, code_widths
        for font_name, font_value in fonts.items():
            font = resolve_dictionary(font_value)
            if font is None or font.get(b"Type") != ("name", b"Font"):
                continue
            font_names.add(font_name)
            subtype = font.get(b"Subtype")
            base_font = font.get(b"BaseFont")
            encoding = font.get(b"Encoding")
            encoding_name = None
            if encoding is not None and encoding[0] == "name":
                encoding_name = encoding[1]
            elif encoding is not None and encoding[0] == "dictionary":
                encoding_dictionary = encoding[1]
                base_encoding = encoding_dictionary.get(b"BaseEncoding")
                if b"Differences" not in encoding_dictionary and base_encoding is not None and base_encoding[0] == "name":
                    encoding_name = base_encoding[1]
            is_simple_font = subtype in (("name", b"Type1"), ("name", b"TrueType"))
            has_safe_encoding = encoding_name in (b"StandardEncoding", b"WinAnsiEncoding")
            if base_font is not None and base_font[0] == "name" and is_simple_font and has_safe_encoding:
                ascii_font_names.add(font_name)
            to_unicode = font.get(b"ToUnicode")
            if to_unicode is not None and to_unicode[0] == "reference":
                if subtype == ("name", b"Type0"):
                    if encoding not in (("name", b"Identity-H"), ("name", b"Identity-V")):
                        continue
                    code_widths[font_name] = 2
                elif is_simple_font:
                    code_widths[font_name] = 1
                else:
                    continue
                entry = (font_name, to_unicode[1])
                if entry not in references:
                    references.append(entry)
        return font_names, ascii_font_names, references, code_widths

    def inspect_node(
        reference: tuple[int, int],
        depth: int,
        inherited_resources: tuple[str, object] | None = None,
    ) -> int | None:
        nonlocal found_expected_text
        if depth > MAX_PDF_PARSE_DEPTH or reference in visited or len(visited) >= MAX_PDF_OBJECTS:
            return None
        visited.add(reference)
        node = read_indirect_dictionary(data, entries, reference, xref_offset)
        if node is None:
            return None
        node_type = node.get(b"Type")
        effective_resources = node.get(b"Resources", inherited_resources)
        if node_type == ("name", b"Page"):
            contents = node.get(b"Contents")
            if contents is None:
                return None
            if contents[0] == "reference":
                content_references = [contents[1]]
            elif contents[0] == "array" and all(item[0] == "reference" for item in contents[1]):
                content_references = [item[1] for item in contents[1]]
            else:
                return None
            if not any(
                content_state.has_nonempty_payload(data, entries, content_reference, xref_offset)
                for content_reference in content_references
            ):
                return None
            if expected_text is not None and not found_expected_text:
                font_names, ascii_font_names, font_cmap_references, font_code_widths = capture_page_fonts(
                    effective_resources
                )
                found_expected_text = pdf_content_references_contain_expected_text(
                    data,
                    entries,
                    xref_offset,
                    content_references,
                    font_names,
                    ascii_font_names,
                    font_cmap_references,
                    expected_text,
                    font_code_widths=font_code_widths,
                    cmap_state=cmap_state,
                    content_state=content_state,
                )
            return 1

        if node_type != ("name", b"Pages"):
            return None
        count_value = node.get(b"Count")
        kids_value = node.get(b"Kids")
        if count_value is None or kids_value is None or kids_value[0] != "array":
            return None
        declared_count = resolve_pdf_integer(count_value, data, entries, xref_offset)
        kids = kids_value[1]
        if declared_count is None or declared_count < 1 or not kids:
            return None
        actual_count = 0
        for child in kids:
            if child[0] != "reference":
                return None
            child_count = inspect_node(child[1], depth + 1, effective_resources)
            if child_count is None:
                return None
            actual_count += child_count
        return actual_count if actual_count == declared_count else None

    page_count = inspect_node(pages_reference[1], 0)
    valid_page_tree = page_count is not None and page_count > 0
    if expected_text_observation is not None:
        expected_text_observation.append(valid_page_tree and found_expected_text)
    return valid_page_tree


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--launcher-binary", help="Optional soffice launcher path for inventory; this file is never executed.")
    parser.add_argument("--engine-binary", help="Direct LibreOffice engine path (for example, soffice.bin).")
    parser.add_argument(
        "--expected-engine-sha256",
        help="Trusted engine SHA-256 from an independent test-image manifest; required before launch.",
    )
    parser.add_argument("--timeout-seconds", type=int, default=60)
    parser.add_argument("--output", type=Path, help="Write JSON to an existing parent directory instead of stdout.")
    parser.add_argument("--include-device-identifiers", action="store_true", help="Include PAPERBATCH_PROBE_ENVIRONMENT_ID when set.")
    args = parser.parse_args(argv)
    if args.timeout_seconds < 1 or args.timeout_seconds > 300:
        parser.error("--timeout-seconds must be between 1 and 300")

    report, exit_code = run_probe(
        args.launcher_binary,
        args.timeout_seconds,
        args.include_device_identifiers,
        args.engine_binary,
        args.expected_engine_sha256,
    )
    output = json.dumps(report, ensure_ascii=False, indent=2) + "\n"
    if args.output:
        args.output.write_text(output, encoding="utf-8")
    else:
        sys.stdout.write(output)
    return exit_code


if __name__ == "__main__":
    raise SystemExit(main())
