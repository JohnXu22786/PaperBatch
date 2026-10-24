#!/usr/bin/env python3
"""Bind the checked-in U9 evidence summary to the exact CI candidate and run."""

from __future__ import annotations

import argparse
import json
import os
import re
from pathlib import Path
from typing import Any


ROOT = Path(__file__).resolve().parents[1]
SOURCE = ROOT / "docs" / "packaging" / "u9-final-evidence.json"
SHA_PATTERN = re.compile(r"^[0-9a-f]{40}$")
JOB_NAMES = (
    "windows-x64-lite-package",
    "build-and-test",
    "windows-pdfium-community-proof",
    "ipp-protocol-fault-lab",
)


def build_report(
    evidence: dict[str, Any],
    *,
    candidate_sha: str | None,
    run_id: str | None,
    run_attempt: str | None,
    workflow: str | None,
    job_results: dict[str, str],
) -> dict[str, Any]:
    """Return the static evidence with the current immutable CI identity."""
    if evidence.get("schemaVersion") != "paperbatch.u9-final-evidence.v1":
        raise ValueError("unsupported evidence schema")
    if candidate_sha is None:
        if run_id is not None or run_attempt is not None or workflow is not None:
            raise ValueError("CI context requires an exact candidate commit")
        return {**evidence, "currentRun": None}
    if not SHA_PATTERN.fullmatch(candidate_sha):
        raise ValueError("candidate commit must be a full lowercase SHA-1")
    if not run_id or not run_id.isdecimal() or not run_attempt or not run_attempt.isdecimal():
        raise ValueError("CI run ID and attempt are required with a candidate commit")
    if not workflow:
        raise ValueError("workflow name is required with a candidate commit")
    results = {name: job_results.get(name, "not_run") for name in JOB_NAMES}
    allowed = {"success", "failure", "cancelled", "skipped", "not_run"}
    if any(result not in allowed for result in results.values()):
        raise ValueError("unknown CI job result")
    outcome = "passed" if all(result == "success" for result in results.values()) else "failed"
    return {
        **evidence,
        "currentRun": {
            "candidateCommit": candidate_sha,
            "workflow": workflow,
            "runId": int(run_id),
            "runAttempt": int(run_attempt),
            "jobResults": results,
            "exactHeadCiOutcome": outcome,
        },
    }


def render_step_summary(report: dict[str, Any], *, max_bytes: int = 12_000) -> str:
    """Render the exact-run report without relying on artifact download access."""
    current = report.get("currentRun")
    if not isinstance(current, dict):
        raise ValueError("step summary requires exact CI run context")
    lines = [
        "## U9 exact-candidate evidence report",
        "",
        f"Source commit: `{current['candidateCommit']}`",
        f"Workflow run: `{current['workflow']}` / `{current['runId']}` / attempt {current['runAttempt']}",
        f"Exact-head CI outcome: `{current['exactHeadCiOutcome']}`",
        "",
        "### Required job conclusions",
        "",
        "| Job | Conclusion |",
        "|---|---|",
    ]
    for name, result in current["jobResults"].items():
        lines.append(f"| `{name}` | `{result}` |")
    lines.extend([
        "",
        "### Evidence limits",
        "",
        "- The checked-in U9 evidence records artifact metadata; artifact contents were not inspected by this workflow.",
        "- Job conclusions and source identity are read from this exact workflow run. Missing reports or unsuccessful jobs are not treated as passes.",
        "- These hosted checks do not establish native Office/WPS compatibility, general device support, or physical paper output.",
    ])
    text = "\n".join(lines) + "\n"
    if len(text.encode("utf-8")) > max_bytes:
        raise ValueError("U9 exact-run summary exceeds its byte limit")
    return text


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--output", required=True, type=Path)
    parser.add_argument("--summary", type=Path)
    args = parser.parse_args()

    evidence = json.loads(SOURCE.read_text(encoding="utf-8"))
    job_results = {
        name: os.environ.get(f"U9_JOB_{name.upper().replace('-', '_')}_RESULT", "not_run")
        for name in JOB_NAMES
    }
    report = build_report(
        evidence,
        candidate_sha=os.environ.get("U9_CANDIDATE_SHA"),
        run_id=os.environ.get("GITHUB_RUN_ID"),
        run_attempt=os.environ.get("GITHUB_RUN_ATTEMPT"),
        workflow=os.environ.get("GITHUB_WORKFLOW"),
        job_results=job_results,
    )
    args.output.write_text(json.dumps(report, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
    if args.summary:
        args.summary.write_text(render_step_summary(report), encoding="utf-8")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
