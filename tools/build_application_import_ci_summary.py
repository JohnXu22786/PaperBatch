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
EXPECTED_BASE = {
    "synthetic_two_page.pdf": ("pdf", "accepted"),
    "synthetic_one_pixel.png": ("png", "accepted"),
    "synthetic_sections_table.docx": ("docx", "document"),
    "synthetic_mixed_page_sizes.xlsx": ("xlsx", "document"),
    "synthetic_two_slides.pptx": ("pptx", "document"),
    "synthetic_text.rtf": ("rtf", "document"),
    "synthetic_text.odt": ("odt", "document"),
    "synthetic_table.ods": ("ods", "document"),
    "synthetic_slide.odp": ("odp", "document"),
    "synthetic_truncated_package.docx": ("docx", "rejected"),
}


def _matches_pinned_engine_version(observed: Any, pinned: Any) -> bool:
    if not isinstance(observed, str) or not isinstance(pinned, str):
        return False
    if observed == pinned:
        return True
    suffix = observed[len(pinned):] if observed.startswith(pinned) else ""
    return bool(re.fullmatch(r"(?: Build [0-9]+| [0-9]+\(Build:[0-9]+\))", suffix))


def render(path: Path) -> tuple[str, int]:
    lines = ["## PaperBatch production document conversion and import", ""]
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
        for item in outcomes[:len(EXPECTED_BASE)]:
            if isinstance(item, dict):
                lines.append(
                    f"| `{item.get('fixture', 'unknown')}` | {item.get('format', 'unknown')} | {item.get('applicationOutcome', 'unknown')} "
                    f"| {item.get('queueStatus', 'unknown')} | {item.get('rendererStatus') or 'not invoked'} |"
                )
    if not valid:
        lines.insert(4, "Application import report identity, outcomes, or evidence limits failed validation; this run is not a pass.")
    failure = report.get("failure") if isinstance(report, dict) else None
    if isinstance(failure, str) and failure:
        bounded_failure = " ".join(failure.split())[:500].replace("|", "\\|").replace("`", "'")
        lines.insert(5, f"Import test stopped: `{bounded_failure}`")
    engine = report.get("libreOffice") if isinstance(report, dict) else None
    if isinstance(engine, dict):
        lines.append(f"LibreOffice: `{engine.get('version', 'unknown')}`; engine SHA-256 `{engine.get('engineSha256', 'unknown')}`.")
        lines.append("This run exercised PaperBatch's production queue-import path with the configured pinned LibreOffice renderer. PDF/PNG and supported document formats produced persisted artifacts; malformed OOXML was rejected before rendering.")
    else:
        lines.append("This run exercised PaperBatch's production queue-import path without a configured LibreOffice renderer. PDF/PNG inputs produced persisted artifacts; document formats remained unsupported.")
    lines.append("LibreOffice conversion is not Microsoft Office/WPS equivalence or visual-fidelity evidence. PDFium remains separately required for page inspection, composition, and batch preparation. No printer or physical-output claim is made.")
    mixed = report.get("mixedBatchPreparation") if isinstance(report, dict) else None
    if isinstance(mixed, dict):
        lines.append(
            f"Windows mixed batch: {mixed.get('sourceCount', 0)} ordered sources; {mixed.get('segmentCount', 0)} segment(s), "
            f"{mixed.get('jobCount', 0)} job(s); fake backend submissions {mixed.get('fakeBackendSubmissions', 0)}; "
            f"replay submissions {mixed.get('replaySubmissions', 0)}. Physical output was not produced."
        )
    return _bound("\n".join(lines) + "\n"), 0 if valid else 1


def _valid_report(report: Any) -> bool:
    if not isinstance(report, dict) or report.get("schemaVersion") != 1 or report.get("evidenceType") != "paperbatch-production-queue-import":
        return False
    if report.get("failure") is not None:
        return False
    if report.get("sourceCommit") != os.environ.get("PB_SOURCE_COMMIT"):
        return False
    for field, env_name in (("workflow", "GITHUB_WORKFLOW"), ("runId", "GITHUB_RUN_ID"),
                            ("runAttempt", "GITHUB_RUN_ATTEMPT"), ("runnerOs", "RUNNER_OS")):
        if report.get(field) != os.environ.get(env_name):
            return False
    configured = os.environ.get("RUNNER_OS") in ("Linux", "Windows")
    preparation_available = report.get("preparationAvailable")
    if not isinstance(preparation_available, bool):
        return False
    expected = {name: (format_name, ("converted" if configured else "unsupported") if result == "document" else result)
                for name, (format_name, result) in EXPECTED_BASE.items()}
    outcomes = report.get("outcomes")
    if not isinstance(outcomes, list) or len(outcomes) != len(expected):
        return False
    found: dict[str, dict[str, Any]] = {}
    for item in outcomes:
        if not isinstance(item, dict) or not isinstance(item.get("fixture"), str) or item["fixture"] in found:
            return False
        found[item["fixture"]] = item
    if set(found) != set(expected):
        return False
    for fixture, (format_name, outcome) in expected.items():
        item = found[fixture]
        if item.get("format") != format_name or item.get("applicationOutcome") != outcome:
            return False
        if not re.fullmatch(r"[0-9a-f]{64}", str(item.get("sha256", ""))):
            return False
        renderer = item.get("rendererStatus")
        queue_status = item.get("queueStatus")
        if not isinstance(queue_status, str):
            return False
        expected_status_prefix = "Ready to prepare · " if preparation_available else "Artifact available · "
        if outcome in ("accepted", "converted") and (not queue_status.startswith(expected_status_prefix) or renderer != "Succeeded"):
            return False
        if outcome == "unsupported" and (queue_status != "Unsupported format" or renderer != "Unsupported"):
            return False
        if outcome == "rejected" and (queue_status != "Needs attention" or item.get("intakeDiagnostic") != "MalformedContainer" or renderer is not None):
            return False
    limits = report.get("evidenceLimits")
    engine = report.get("libreOffice")
    if configured:
        pinned_version = os.environ.get("PAPERBATCH_LO_EXPECTED_VERSION")
        observed_version = engine.get("version") if isinstance(engine, dict) else None
        if (not isinstance(engine, dict) or not isinstance(pinned_version, str)
                or not _matches_pinned_engine_version(observed_version, pinned_version)
                or engine.get("engineSha256") != os.environ.get("PAPERBATCH_LO_ENGINE_SHA256")
                or not re.fullmatch(r"[0-9a-f]{64}", str(engine.get("engineSha256", "")))):
            return False
    elif engine is not None:
        return False
    mixed = report.get("mixedBatchPreparation")
    if os.environ.get("RUNNER_OS") == "Windows":
        if not isinstance(mixed, dict):
            return False
        prepared_artifact = mixed.get("preparedArtifact")
        source_order = mixed.get("sourceOrder")
        expected_source_order = [item["sha256"] for item in report["outcomes"]
                                 if item["applicationOutcome"] in ("accepted", "converted")]
        if (mixed.get("sourceCount") != 9 or mixed.get("segmentCount") != 1
                or mixed.get("jobCount") != 1 or mixed.get("fakeBackendSubmissions") != 1
                or mixed.get("replaySubmissions") != 0
                or not isinstance(source_order, list) or source_order != expected_source_order
                or not isinstance(prepared_artifact, dict)
                or not re.fullmatch(r"[0-9a-f]{64}", str(prepared_artifact.get("sha256", "")))
                or type(prepared_artifact.get("lengthBytes")) is not int or prepared_artifact["lengthBytes"] <= 0
                or type(prepared_artifact.get("pageCount")) is not int or prepared_artifact["pageCount"] <= 0):
            return False
    elif mixed is not None:
        return False
    return (isinstance(limits, list) and len(limits) >= 4
            and any("PDFium remains separately required" in str(item) for item in limits)
            and any("Microsoft Office/WPS equivalence" in str(item) for item in limits))


def _finish(lines: list[str], message: str, status: int) -> tuple[str, int]:
    lines.extend(["", message, ""])
    return _bound("\n".join(lines)), status


def _bound(text: str) -> str:
    encoded = text.encode("utf-8")
    if len(encoded) <= MAX_SUMMARY_BYTES:
        return text
    marker = "\n[Summary truncated at its byte limit.]\n"
    return encoded[:MAX_SUMMARY_BYTES - len(marker.encode("utf-8"))].decode("utf-8", "ignore") + marker


def _escape_workflow_command_data(text: str) -> str:
    return text.replace("%", "%25").replace("\r", "%0D").replace("\n", "%0A")


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
    if status:
        detail = next((line[len("Import test stopped: `"):-1]
                       for line in summary.splitlines()
                       if line.startswith("Import test stopped: `") and line.endswith("`")),
                      "Application import evidence validation failed; see the bounded report summary.")
        print(f"::error::{_escape_workflow_command_data(detail)}", file=sys.stderr)
    return status


if __name__ == "__main__":
    raise SystemExit(main())
