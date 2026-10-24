"""Run the CI Python suite and expose bounded, sanitized failures."""

from __future__ import annotations

import os
import re
import subprocess
import sys
from pathlib import Path


MAX_DIAGNOSTIC_CHARS = 12_000
MAX_CAPTURE_BYTES = 12_000
MAX_RUN_OUTPUT_BYTES = 256_000
MAX_WORKFLOW_ANNOTATION_BYTES = 12_000
OMISSION_MARKER = "\n[... middle omitted ...]\n"
WORKFLOW_OMISSION_MARKER = "%0A[... middle omitted ...]%0A"


def sanitize_output(
    output: str,
    *,
    workspace: str | None = None,
    runner_temp: str | None = None,
    test_temp: str | None = None,
    home: str | None = None,
) -> str:
    """Hide runner-specific paths and profile names from test output."""
    path_replacements = (
        (workspace, "[WORKSPACE]"),
        (runner_temp, "[RUNNER_TEMP]"),
        (test_temp, "[TEST_TEMP]"),
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


def bound_utf8_output(output: str, max_bytes: int = MAX_CAPTURE_BYTES) -> str:
    """Keep sanitized diagnostic text within a UTF-8 byte budget."""
    marker = OMISSION_MARKER.encode("ascii")
    if max_bytes <= len(marker):
        raise ValueError("max_bytes is too small for the omission marker")

    encoded = output.encode("utf-8")
    if len(encoded) <= max_bytes:
        return output

    available = max_bytes - len(marker)
    head_bytes = available // 3
    tail_bytes = available - head_bytes
    head = encoded[:head_bytes].decode("utf-8", errors="ignore")
    tail = encoded[-tail_bytes:].decode("utf-8", errors="ignore")
    return head + marker.decode("ascii") + tail


def escape_workflow_command_data(value: str) -> list[str]:
    """Escape data characters so child output cannot add Actions commands."""
    replacements = {"%": "%25", "\r": "%0D", "\n": "%0A"}
    return [replacements.get(character, character) for character in value]


def bound_workflow_command_data(value: str, max_bytes: int) -> str:
    """Escape and byte-bound annotation data without cutting an escape token."""
    tokens = escape_workflow_command_data(value)
    if sum(len(token.encode("utf-8")) for token in tokens) <= max_bytes:
        return "".join(tokens)

    marker_bytes = len(WORKFLOW_OMISSION_MARKER.encode("ascii"))
    if max_bytes <= marker_bytes:
        raise ValueError("max_bytes is too small for the workflow omission marker")

    available = max_bytes - marker_bytes
    head_budget = available // 3
    tail_budget = available - head_budget
    head: list[str] = []
    used = 0
    for token in tokens:
        token_size = len(token.encode("utf-8"))
        if used + token_size > head_budget:
            break
        head.append(token)
        used += token_size

    tail: list[str] = []
    used = 0
    for token in reversed(tokens):
        token_size = len(token.encode("utf-8"))
        if used + token_size > tail_budget:
            break
        tail.append(token)
        used += token_size

    return "".join(head) + WORKFLOW_OMISSION_MARKER + "".join(reversed(tail))


def format_workflow_annotation(exit_code: int, diagnostic: str) -> str:
    """Format a bounded Actions error command containing sanitized diagnostics."""
    prefix = f"::error::Python unit tests failed (exit code {exit_code})%0A"
    terminator = "\n"
    content_budget = (
        MAX_WORKFLOW_ANNOTATION_BYTES
        - len(prefix.encode("utf-8"))
        - len(terminator.encode("utf-8"))
    )
    message = bound_workflow_command_data(diagnostic, content_budget)
    return prefix + message + terminator


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


def summarize_unittest_failures(output: str) -> str:
    """Return a compact failure list from unittest's standard traceback report."""
    summaries: list[str] = []
    for section in re.split(r"(?m)^={7,}\s*$", output):
        lines = [line.strip() for line in section.splitlines() if line.strip()]
        if not lines or not lines[0].startswith(("ERROR: ", "FAIL: ")):
            continue
        summaries.append(f"{lines[0]} — {lines[-1]}")
    return "\n".join(summaries)


def run_command(
    command: list[str],
    *,
    cwd: Path | str,
    max_capture_bytes: int = MAX_RUN_OUTPUT_BYTES,
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
        "### Python unit test diagnostics\n\n"
        f"Exit code: {exit_code}\n\n"
        "```text\n"
        f"{escaped}\n"
        "```\n"
    )
    sys.stdout.write(format_workflow_annotation(exit_code, diagnostic))
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
        test_temp=os.environ.get("PAPERBATCH_CI_TEST_TEMP"),
        home=str(Path.home()),
    )
    failure_summary = summarize_unittest_failures(sanitized) if exit_code else ""
    if failure_summary:
        sanitized = "Failed tests:\n" + failure_summary + "\n\nCaptured test output:\n" + sanitized
    diagnostic = bound_utf8_output(bound_output(sanitized))
    if exit_code:
        _write_failure_summary(exit_code, diagnostic)
    else:
        sys.stdout.write(diagnostic)
    return exit_code


if __name__ == "__main__":
    raise SystemExit(main())
