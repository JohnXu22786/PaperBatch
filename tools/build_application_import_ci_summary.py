#!/usr/bin/env python3
"""Publish a bounded exact-run summary for PaperBatch production queue imports."""

from __future__ import annotations

import json
import os
import re
import sys
from pathlib import Path
from typing import Any

MAX_REPORT_BYTES = 16 * 1024
MAX_SUMMARY_BYTES = 4_000
EXPECTED = {
    "synthetic_two_page.pdf": ("pdf", "accepted"),
    "synthetic_one_pixel.png": ("png", "accepted"),
    "synthetic_sections_table.docx": ("docx", "unsupported"),
    "synthetic_mixed_page_sizes.xlsx": ("xlsx", "unsupported"),
    "synthetic_two_slides.pptx": ("pptx", "unsupported"),
    "synthetic_truncated_package.docx": ("docx", "rejected"),
}


def render(path: Path) -> tuple[str, int]:
    lines = ["## PaperBatch production document import", ""]
    lines.append(
        f"Exact run: commit `{os.environ.get('PB_SOURCE_COMMIT', 'unknown')}`; workflow `{os.environ.get('GITHUB_WORKFLOW', 'unknown')}`; "
        f"run `{os.environ.get('GITHUB_RUN_ID', 'unknown')}` attempt `{os.environ.get('GITHUB_RUN_ATTEMPT', 'unknown')}`."
    )
    lines.append(
        f"Runner: `{os.environ.get('RUNNER_OS', 'unknown')}` / `{os.environ.get('RUNNER_NAME', 'unknown')}`; "
        f"image `{os.environ.get('ImageOS', 'unknown')}` `{os.environ.get('ImageVersion', 'unknown')}`."
    )
    if not path.is_file():
        return _finish(lines, "Application import report unavailable; this run is not a pass.", 1)
    data = path.read_bytes()
    if len(data) > MAX_REPORT_BYTES:
        return _finish(lines, "Application import report exceeds its byte limit; this run is not a pass.", 1)
    try:
        report = json.loads(data)
    except (UnicodeDecodeError, json.JSONDecodeError):
        return _finish(lines, "Application import report is invalid JSON; this run is not a pass.", 1)
    valid = _valid_report(report)
    lines.extend(["", "| Fixture | Format | Application outcome | Queue status | Renderer |", "|---|---|---|---|---|"])
    outcomes: Any = report.get("outcomes", []) if isinstance(report, dict) else []
    if isinstance(outcomes, list):
        for item in outcomes[:len(EXPECTED)]:
            if isinstance(item, dict):
                lines.append(
                    f"| `{item.get('fixture', 'unknown')}` | {item.get('format', 'unknown')} | {item.get('applicationOutcome', 'unknown')} "
                    f"| {item.get('queueStatus', 'unknown')} | {item.get('rendererStatus') or 'not invoked'} |"
                )
    if not valid:
        lines.insert(4, "Application import report identity, outcomes, or evidence limits failed validation; this run is not a pass.")
    lines.extend([
        "",
        "This exercises PaperBatch's production queue-import path. PDF and PNG are accepted and produce persisted artifacts; DOCX/XLSX/PPTX pass intake checks but remain unsupported by the production renderer; malformed OOXML is rejected before rendering.",
        "LibreOffice is not invoked by this test. Standalone LibreOffice conversion does not establish PaperBatch import support. No Microsoft Office/WPS compatibility, visual fidelity, printer, or physical-output claim is made.",
    ])
    return _bound("\n".join(lines) + "\n"), 0 if valid else 1


def _valid_report(report: Any) -> bool:
    if not isinstance(report, dict) or report.get("schemaVersion") != 1 or report.get("evidenceType") != "paperbatch-production-queue-import":
        return False
    if report.get("sourceCommit") != os.environ.get("PB_SOURCE_COMMIT"):
        return False
    for field, env_name in (("workflow", "GITHUB_WORKFLOW"), ("runId", "GITHUB_RUN_ID"),
                            ("runAttempt", "GITHUB_RUN_ATTEMPT"), ("runnerOs", "RUNNER_OS")):
        if report.get(field) != os.environ.get(env_name):
            return False
    outcomes = report.get("outcomes")
    if not isinstance(outcomes, list) or len(outcomes) != len(EXPECTED):
        return False
    found: dict[str, dict[str, Any]] = {}
    for item in outcomes:
        if not isinstance(item, dict) or not isinstance(item.get("fixture"), str) or item["fixture"] in found:
            return False
        found[item["fixture"]] = item
    if set(found) != set(EXPECTED):
        return False
    for fixture, (format_name, outcome) in EXPECTED.items():
        item = found[fixture]
        if item.get("format") != format_name or item.get("applicationOutcome") != outcome:
            return False
        if not re.fullmatch(r"[0-9a-f]{64}", str(item.get("sha256", ""))):
            return False
        renderer = item.get("rendererStatus")
        queue_status = item.get("queueStatus")
        if not isinstance(queue_status, str):
            return False
        if outcome == "accepted" and (not queue_status.startswith("Ready to prepare · ") or renderer != "Succeeded"):
            return False
        if outcome == "unsupported" and (queue_status != "Unsupported format" or renderer != "Unsupported"):
            return False
        if outcome == "rejected" and (queue_status != "Needs attention" or item.get("intakeDiagnostic") != "MalformedContainer" or renderer is not None):
            return False
    limits = report.get("evidenceLimits")
    return (isinstance(limits, list) and len(limits) >= 4
            and any("standalone LibreOffice conversion does not establish PaperBatch import support" in str(item) for item in limits)
            and any("No Office/WPS compatibility" in str(item) for item in limits))


def _finish(lines: list[str], message: str, status: int) -> tuple[str, int]:
    lines.extend(["", message, ""])
    return _bound("\n".join(lines)), status


def _bound(text: str) -> str:
    encoded = text.encode("utf-8")
    if len(encoded) <= MAX_SUMMARY_BYTES:
        return text
    marker = "\n[Summary truncated at its byte limit.]\n"
    return encoded[:MAX_SUMMARY_BYTES - len(marker.encode("utf-8"))].decode("utf-8", "ignore") + marker


def main() -> int:
    if len(sys.argv) != 2:
        raise SystemExit("usage: build_application_import_ci_summary.py REPORT")
    summary, status = render(Path(sys.argv[1]))
    destination = os.environ.get("GITHUB_STEP_SUMMARY")
    if destination:
        with open(destination, "a", encoding="utf-8", newline="\n") as stream:
            stream.write(summary)
    else:
        sys.stdout.write(summary)
    return status


if __name__ == "__main__":
    raise SystemExit(main())
