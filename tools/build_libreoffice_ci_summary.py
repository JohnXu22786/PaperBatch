#!/usr/bin/env python3
"""Publish a byte-bounded exact-run summary for the LibreOffice corpus."""

from __future__ import annotations

import json
import os
import sys
from pathlib import Path
from typing import Any

MAX_REPORT_BYTES = 2 * 1024 * 1024
MAX_SUMMARY_BYTES = 12_000
EVIDENCE_LIMIT = (
    "LibreOffice execution does not establish PaperBatch import support, Microsoft Office/WPS compatibility, human visual fidelity, "
    "OEM/device support, or physical output."
)


def _clean(value: Any, limit: int) -> str:
    text = str(value).replace("\r", " ").replace("\n", " ").replace("|", "\\|").replace("`", "'")
    return text[:limit]


def render(report_path: Path) -> tuple[str, int]:
    lines = ["## Synthetic OOXML LibreOffice execution", ""]
    lines.append(f"Exact run: commit `{os.environ.get('PB_SOURCE_COMMIT', 'unknown')}`; workflow `{os.environ.get('GITHUB_WORKFLOW', 'unknown')}`; run `{os.environ.get('GITHUB_RUN_ID', 'unknown')}` attempt `{os.environ.get('GITHUB_RUN_ATTEMPT', 'unknown')}`.")
    lines.append(f"Runner: `{os.environ.get('RUNNER_OS', 'unknown')}` / `{os.environ.get('RUNNER_NAME', 'unknown')}`; image `{os.environ.get('ImageOS', 'unknown')}` `{os.environ.get('ImageVersion', 'unknown')}`.")
    if not report_path.is_file():
        lines.extend(["", "LibreOffice corpus report: unavailable; this run is not a pass.", "", EVIDENCE_LIMIT])
        return _bound("\n".join(lines) + "\n"), 1
    data = report_path.read_bytes()
    if len(data) > MAX_REPORT_BYTES:
        lines.extend(["", "LibreOffice corpus report: rejected because it exceeds the report byte limit.", "", EVIDENCE_LIMIT])
        return _bound("\n".join(lines) + "\n"), 1
    try:
        report = json.loads(data)
    except (UnicodeDecodeError, json.JSONDecodeError):
        lines.extend(["", "LibreOffice corpus report: invalid JSON; this run is not a pass.", "", EVIDENCE_LIMIT])
        return _bound("\n".join(lines) + "\n"), 1
    if not isinstance(report, dict):
        lines.extend(["", "LibreOffice corpus report: invalid schema; this run is not a pass.", "", EVIDENCE_LIMIT])
        return _bound("\n".join(lines) + "\n"), 1
    run = report.get("run", {})
    engine = report.get("engine", {})
    counts = report.get("counts", {})
    run_identity = report.get("runIdentity")
    if (not isinstance(run, dict) or not isinstance(engine, dict) or not isinstance(counts, dict)
            or not isinstance(run_identity, dict)):
        lines.extend(["", "LibreOffice corpus report: invalid schema; this run is not a pass.", "", EVIDENCE_LIMIT])
        return _bound("\n".join(lines) + "\n"), 1
    installer = run.get("installer", {})
    if not isinstance(installer, dict):
        installer = {}
    lines.extend([
        "",
        f"Outcome: `{report.get('status', 'unknown')}`; engine `{engine.get('version', 'unverified')}`; engine SHA-256 `{engine.get('binarySha256', 'unknown')}`.",
        f"Installer: `{installer.get('status', 'unavailable')}`; package `{installer.get('package', 'unknown')}` from `{installer.get('sourceUrl', 'unknown')}`; SHA-256 `{installer.get('packageSha256', 'unknown')}`; integrity `{installer.get('integrityMethod', 'unknown')}`; signer fingerprint `{installer.get('signingKeyFingerprint', 'unknown')}`.",
        f"Process isolation: {_clean(run.get('networkIsolation', 'unknown'), 240)}.",
        f"Synthetic inputs: {counts.get('syntheticSourceSpecimens', 0)} total; {counts.get('selectedForLibreOffice', 0)} selected; {counts.get('successfulLibreOfficeConversions', 0)} converted; {counts.get('failedLibreOfficeConversions', 0)} failed.",
        "",
        "| Input ID | Status | Exit | Timeout | PDF bytes | PDF SHA-256 | Page inspection |",
        "|---|---:|---:|---:|---:|---|---|",
    ])
    results = report.get("results", [])
    if not isinstance(results, list):
        results = []
    for item in results[:30]:
        if not isinstance(item, dict):
            continue
        page = item.get("pageInspection", {})
        if not isinstance(page, dict):
            page = {}
        lines.append(f"| `{item.get('inputId', 'unknown')}` | {item.get('status', 'unknown')} | {item.get('exitCode', 'n/a')} | {item.get('timedOut', False)} | {item.get('outputPdfBytes', 'n/a')} | `{item.get('outputPdfSha256', 'n/a')}` | {page.get('status', 'unknown')}; valid={page.get('structureValid', False)}; pages={page.get('pageCount', 'n/a')} |")
    if len(results) > 30:
        lines.append(f"\n{len(results) - 30} additional scenario rows are in the machine report.")
    lines.extend(["", EVIDENCE_LIMIT])
    valid = (
        report.get("status") == "passed"
        and isinstance(installer, dict) and installer.get("status") == "verified"
        and installer.get("version") == "26.2.2.2"
        and installer.get("signingKeyFingerprint") == "C2839ECAD9408FBE9531C3E9F434A1EFAFEEAEA3"
        and isinstance(engine, dict) and str(engine.get("version", "")).startswith("LibreOffice 26.2.2.2")
        and report.get("commit") == os.environ.get("PB_SOURCE_COMMIT")
        and run_identity.get("workflow") == os.environ.get("GITHUB_WORKFLOW")
        and run_identity.get("runId") == os.environ.get("GITHUB_RUN_ID")
        and run_identity.get("runAttempt") == os.environ.get("GITHUB_RUN_ATTEMPT")
        and isinstance(run, dict) and run.get("os") == os.environ.get("RUNNER_OS")
        and run.get("runner") == os.environ.get("RUNNER_NAME")
        and run.get("runnerImage") == os.environ.get("ImageOS")
        and run.get("runnerImageVersion") == os.environ.get("ImageVersion")
    )
    if not valid:
        lines.insert(4, "Exact-run report, installer provenance, or corpus outcome did not validate; this run is not a pass.")
    return _bound("\n".join(lines) + "\n"), 0 if valid else 1


def _bound(text: str) -> str:
    encoded = text.encode("utf-8")
    if len(encoded) <= MAX_SUMMARY_BYTES:
        return text
    marker = "\n[Additional report rows omitted to keep this summary bounded.]\n"
    marker_bytes = marker.encode("utf-8")
    return encoded[:MAX_SUMMARY_BYTES - len(marker_bytes)].decode("utf-8", "ignore") + marker


def main() -> int:
    if len(sys.argv) != 2:
        raise SystemExit("usage: build_libreoffice_ci_summary.py REPORT")
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
