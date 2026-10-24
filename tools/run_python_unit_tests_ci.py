"""Run the Windows CI Python suite and expose bounded, sanitized failures."""

from __future__ import annotations

import os
import re
import subprocess
import sys
from pathlib import Path


MAX_DIAGNOSTIC_CHARS = 12_000
MAX_CAPTURE_BYTES = 12_000
OMISSION_MARKER = "\n[... middle omitted ...]\n"


def sanitize_output(
    output: str,
    *,
    workspace: str | None = None,
    runner_temp: str | None = None,
    home: str | None = None,
) -> str:
    """Hide runner-specific paths and profile names from test output."""
    path_replacements = (
        (workspace, "[WORKSPACE]"),
        (runner_temp, "[RUNNER_TEMP]"),
        (home, "[USER_HOME]"),
    )
    for path, replacement in path_replacements:
        if not path:
            continue
        variants = {path, path.replace("\\", "/"), path.replace("/", "\\")}
        for variant in sorted(variants, key=len, reverse=True):
            output = re.sub(re.escape(variant), replacement, output, flags=re.IGNORECASE)

    output = re.sub(r"(?i)([A-Z]:[\\/](?:Users|Documents and Settings)[\\/])[^\\/\r\n]+", r"\1[USER]", output)
    output = re.sub(r"(?i)(/(?:home|Users)/)[^/\r\n]+", r"\1[USER]", output)
    return output


def bound_output(output: str, max_chars: int = MAX_DIAGNOSTIC_CHARS) -> str:
    """Keep the diagnostic's beginning and end within a fixed character limit."""
    if max_chars < len(OMISSION_MARKER):
        raise ValueError("max_chars is too small for the omission marker")
    if len(output) <= max_chars:
        return output

    available = max_chars - len(OMISSION_MARKER)
    head_length = available // 3
    tail_length = available - head_length
    return output[:head_length] + OMISSION_MARKER + output[-tail_length:]


class BoundedCapture:
    """Retain bounded output from the beginning and end of a child process."""

    def __init__(self, max_bytes: int = MAX_CAPTURE_BYTES) -> None:
        marker = OMISSION_MARKER.encode("ascii")
        if max_bytes <= len(marker):
            raise ValueError("max_bytes is too small for the omission marker")

        available = max_bytes - len(marker)
        self._head_limit = available // 3
        self._tail_limit = available - self._head_limit
        self._head = bytearray()
        self._tail = bytearray()
        self._marker = marker
        self._max_bytes = max_bytes
        self._truncated = False

    def append(self, chunk: bytes) -> None:
        head_space = self._head_limit - len(self._head)
        if head_space > 0:
            self._head.extend(chunk[:head_space])
            chunk = chunk[head_space:]

        if chunk:
            if len(chunk) >= self._tail_limit:
                self._tail = bytearray(chunk[-self._tail_limit :])
                self._truncated = True
            else:
                excess = len(self._tail) + len(chunk) - self._tail_limit
                if excess > 0:
                    del self._tail[:excess]
                    self._truncated = True
                self._tail.extend(chunk)

    def text(self) -> str:
        output = bytes(self._head)
        if self._truncated:
            output += self._marker
        output += bytes(self._tail)
        decoded = output.decode("utf-8", errors="replace")
        encoded = decoded.encode("utf-8")
        if len(encoded) <= self._max_bytes:
            return decoded

        available = self._max_bytes - len(self._marker)
        head_bytes = available // 3
        tail_bytes = available - head_bytes
        head = encoded[:head_bytes].decode("utf-8", errors="ignore")
        tail = encoded[-tail_bytes:].decode("utf-8", errors="ignore")
        return head + self._marker.decode("ascii") + tail


def run_command(
    command: list[str],
    *,
    cwd: Path | str,
    max_capture_bytes: int = MAX_CAPTURE_BYTES,
) -> tuple[int, str]:
    """Drain merged child output continuously into a fixed-size memory buffer."""
    capture = BoundedCapture(max_capture_bytes)
    process = subprocess.Popen(
        command,
        cwd=cwd,
        stdout=subprocess.PIPE,
        stderr=subprocess.STDOUT,
    )
    if process.stdout is None:
        raise RuntimeError("child process output pipe was not created")

    try:
        while True:
            chunk = process.stdout.read1(8192)
            if not chunk:
                break
            capture.append(chunk)
    finally:
        process.stdout.close()

    return process.wait(), capture.text()


def _write_failure_summary(exit_code: int, diagnostic: str) -> None:
    summary_path = os.environ.get("GITHUB_STEP_SUMMARY")
    escaped = diagnostic.replace("```", "` ` `")
    report = (
        "### Windows Python unit test diagnostics\n\n"
        f"Exit code: {exit_code}\n\n"
        "```text\n"
        f"{escaped}\n"
        "```\n"
    )
    if summary_path:
        with Path(summary_path).open("a", encoding="utf-8", newline="\n") as summary:
            summary.write(report)
    else:
        sys.stdout.write(report)


def main() -> int:
    repository_root = Path(__file__).resolve().parents[1]
    command = [sys.executable, "-m", "unittest", "discover", "-s", "tests", "-p", "test_*.py"]

    exit_code, output = run_command(command, cwd=repository_root)

    sanitized = sanitize_output(
        output,
        workspace=os.environ.get("GITHUB_WORKSPACE"),
        runner_temp=os.environ.get("RUNNER_TEMP"),
        home=str(Path.home()),
    )
    diagnostic = bound_output(sanitized)
    if exit_code:
        _write_failure_summary(exit_code, diagnostic)
    else:
        sys.stdout.write(diagnostic)
    return exit_code


if __name__ == "__main__":
    raise SystemExit(main())
