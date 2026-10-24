"""Run the Windows CI Python suite and expose bounded, sanitized failures."""

from __future__ import annotations

import os
import re
import subprocess
import sys
import tempfile
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


def _read_bounded_capture(capture) -> str:
    capture.seek(0, os.SEEK_END)
    size = capture.tell()
    if size <= MAX_CAPTURE_BYTES:
        capture.seek(0)
        return capture.read().decode("utf-8", errors="replace")

    head_bytes = MAX_CAPTURE_BYTES // 3
    tail_bytes = MAX_CAPTURE_BYTES - head_bytes
    capture.seek(0)
    head = capture.read(head_bytes).decode("utf-8", errors="replace")
    capture.seek(-tail_bytes, os.SEEK_END)
    tail = capture.read(tail_bytes).decode("utf-8", errors="replace")
    return head + OMISSION_MARKER + tail


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

    with tempfile.TemporaryFile(mode="w+b") as capture:
        result = subprocess.run(command, cwd=repository_root, stdout=capture, stderr=subprocess.STDOUT, check=False)
        output = _read_bounded_capture(capture)

    sanitized = sanitize_output(
        output,
        workspace=os.environ.get("GITHUB_WORKSPACE"),
        runner_temp=os.environ.get("RUNNER_TEMP"),
        home=str(Path.home()),
    )
    diagnostic = bound_output(sanitized)
    if result.returncode:
        _write_failure_summary(result.returncode, diagnostic)
    else:
        sys.stdout.write(diagnostic)
    return result.returncode


if __name__ == "__main__":
    raise SystemExit(main())
