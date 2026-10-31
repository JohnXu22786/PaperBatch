#!/usr/bin/env python3
"""Stream a command's output and retain only a bounded tail for CI artifacts."""

from __future__ import annotations

import argparse
import subprocess
import sys
from pathlib import Path

DEFAULT_MAX_BYTES = 32 * 1024


def run(command: list[str], output_path: Path, max_bytes: int = DEFAULT_MAX_BYTES) -> int:
    if not command or max_bytes <= 0:
        raise ValueError("a command and positive output bound are required")
    output_path.parent.mkdir(parents=True, exist_ok=True)
    tail = bytearray()
    with subprocess.Popen(command, stdout=subprocess.PIPE, stderr=subprocess.STDOUT) as child:
        assert child.stdout is not None
        while chunk := child.stdout.read(4096):
            sys.stdout.buffer.write(chunk)
            sys.stdout.buffer.flush()
            tail.extend(chunk)
            if len(tail) > max_bytes:
                del tail[:len(tail) - max_bytes]
        result = child.wait()
    output_path.write_bytes(tail)
    return result


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", required=True, type=Path)
    parser.add_argument("--max-bytes", type=int, default=DEFAULT_MAX_BYTES)
    parser.add_argument("command", nargs=argparse.REMAINDER)
    args = parser.parse_args()
    command = args.command[1:] if args.command[:1] == ["--"] else args.command
    try:
        return run(command, args.output, args.max_bytes)
    except (OSError, ValueError) as error:
        print(f"bounded command capture failed: {type(error).__name__}: {error}", file=sys.stderr)
        return 125


if __name__ == "__main__":
    raise SystemExit(main())
