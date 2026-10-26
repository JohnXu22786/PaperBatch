#!/usr/bin/env python3
"""Render a bounded, exact-run GitHub Actions job summary."""

from __future__ import annotations

import argparse
import json
import os
import re
from pathlib import Path
import platform
import subprocess
import sys
from typing import Any


MAX_REPORT_BYTES = 256 * 1024
MAX_SUMMARY_BYTES = 12_000
SHA_PATTERN = re.compile(r"^[0-9a-f]{40,64}$")
JOB_RESULTS = {"success", "failure", "cancelled", "skipped", "timed_out", "action_required", "unknown"}
STEP_RESULTS = JOB_RESULTS | {"not_run"}


def read_report(path: Path, *, max_bytes: int = MAX_REPORT_BYTES) -> dict[str, Any]:
    raw = path.read_bytes()
    if len(raw) > max_bytes:
        raise ValueError(f"report exceeds the {max_bytes} byte limit")
    value = json.loads(raw.decode("utf-8"))
    if not isinstance(value, dict):
        raise ValueError("report must be a JSON object")
    return value


def _clean(value: Any, limit: int = 240) -> str:
    text = str(value).replace("\r", " ").replace("\n", " ").replace("`", "'")
    text = " ".join(text.split())
    if len(text) > limit:
        return text[: limit - 15] + " [truncated]"
    return text


def _run_identity(report: dict[str, Any]) -> tuple[str | None, str | None, str | None]:
    identity = report.get("runIdentity")
    if isinstance(identity, dict):
        return identity.get("workflow"), str(identity.get("runId")), str(identity.get("runAttempt"))
    current = report.get("currentRun")
    if isinstance(current, dict):
        return current.get("workflow"), str(current.get("runId")), str(current.get("runAttempt"))
    return None, None, None


def _validate_report(report: dict[str, Any], context: dict[str, str]) -> None:
    commit = report.get("commit")
    current = report.get("currentRun")
    if isinstance(current, dict):
        commit = current.get("candidateCommit")
    if commit != context["source_commit"]:
        raise ValueError("machine report commit does not match summary run")
    workflow, run_id, run_attempt = _run_identity(report)
    if (workflow, run_id, run_attempt) != (
        context["workflow"], context["run_id"], context["run_attempt"]
    ):
        raise ValueError("machine report workflow run identity does not match summary run")


def _report_lines(report: dict[str, Any]) -> list[str]:
    lines = ["### Machine report results"]
    if isinstance(report.get("status"), str):
        lines.append(f"Report outcome: `{_clean(report['status'], 32)}`")
    if isinstance(report.get("os"), str) and isinstance(report.get("runnerImage"), str):
        lines.append(
            f"Report runner: `{_clean(report['os'], 100)}` / "
            f"`{_clean(report.get('runner', 'unknown'), 120)}`; image "
            f"`{_clean(report['runnerImage'], 120)}` `{_clean(report.get('runnerImageVersion', 'unknown'), 80)}`"
        )
    tool_versions = report.get("toolVersions")
    if isinstance(tool_versions, dict):
        versions = ", ".join(
            f"{_clean(name, 40)} `{_clean(version, 80)}`"
            for name, version in list(tool_versions.items())[:8]
        )
        if versions:
            lines.append("Report tools: " + versions)
    current = report.get("currentRun")
    if isinstance(current, dict):
        lines.append(f"Exact-head CI outcome: `{_clean(current.get('exactHeadCiOutcome', 'unknown'))}`")
        job_results = current.get("jobResults", {})
        if isinstance(job_results, dict):
            lines.append("Job conclusions: " + "; ".join(
                f"`{_clean(name, 80)}` `{_clean(result, 32)}`"
                for name, result in list(job_results.items())[:16]
            ))

    batch_print = report.get("batchPrint")
    if isinstance(batch_print, dict):
        artifact = batch_print.get("finalArtifact")
        artifact_pages = artifact.get("pageCount", "unknown") if isinstance(artifact, dict) else "unknown"
        lines.append(
            "Batch print: "
            f"sources={_clean(batch_print.get('sourceCount', 'unknown'), 16)}, "
            f"pages={_clean(artifact_pages, 16)}, "
            f"segments={_clean(batch_print.get('segmentCount', 'unknown'), 16)}, "
            f"jobs={_clean(batch_print.get('jobCount', 'unknown'), 16)}, "
            f"simulated submission `{_clean(batch_print.get('simulatedSubmission', 'unknown'), 32)}`"
        )
    partial_failure = report.get("partialFailure")
    if isinstance(partial_failure, dict):
        lines.append(
            "Batch partial failure: "
            f"segments={_clean(partial_failure.get('segmentCount', 'unknown'), 16)}, "
            f"jobs={_clean(partial_failure.get('jobCount', 'unknown'), 16)}, "
            f"outcomes `{_clean(', '.join(map(str, partial_failure.get('outcomes', []))), 100)}`, "
            f"automatic resubmit `{_clean(partial_failure.get('automaticResubmit', 'unknown'), 16)}`"
        )

    scenarios = report.get("scenarios")
    if isinstance(scenarios, list):
        lines.append(f"Scenarios reported: {len(scenarios)}")
        for item in scenarios[:20]:
            if not isinstance(item, dict):
                continue
            lines.append(
                f"- `{_clean(item.get('scenarioId', item.get('id', 'unknown')), 100)}`: "
                f"`{_clean(item.get('outcome', item.get('status', 'unknown')), 32)}`"
            )
            limit = item.get("evidenceLimit")
            if limit:
                lines.append(f"  Evidence limit: {_clean(limit, 240)}")
            assertions = item.get("assertions")
            if isinstance(assertions, list):
                lines.append(f"  Assertions recorded: {len(assertions)}; " + ", ".join(_clean(value, 80) for value in assertions[:12]))
                if len(assertions) > 12:
                    lines.append(f"  {len(assertions) - 12} additional assertions omitted.")
    package_tree = report.get("packageTreeComparison")
    if isinstance(package_tree, dict):
        lines.append(f"Package tree comparison: `{_clean(package_tree.get('status', 'unknown'), 32)}`; distinct artifacts={_clean(package_tree.get('distinctArtifacts', 'unknown'), 16)}")
        if len(scenarios) > 20:
            lines.append(f"- {len(scenarios) - 20} additional scenarios omitted from this bounded summary.")

    package = report.get("packageManifestSha256")
    if package:
        lines.append(f"Package manifest SHA-256: `{_clean(package, 80)}`")
    observations = report.get("observations")
    if isinstance(observations, dict):
        for name in ("printers", "registry"):
            item = observations.get(name)
            if isinstance(item, dict):
                lines.append(f"{name.title()} snapshot: `{_clean(item.get('status', 'unknown'), 32)}`")
    for section in ("composition", "pageEdit", "formFixture"):
        item = report.get(section)
        if isinstance(item, dict):
            lines.append(f"{section}: `{_clean(item.get('status', 'unknown'), 32)}`; {_clean(item.get('reason', 'No reason recorded.'), 180)}")
    for section in ("spooler", "queues"):
        item = report.get(section)
        if isinstance(item, dict):
            status = item.get("status", "unknown")
            count = item.get("count")
            suffix = f"; count {count}" if isinstance(count, int) else ""
            lines.append(f"{section.title()} evidence: `{_clean(status, 32)}`{suffix}")
    runner = report.get("runner")
    if isinstance(runner, dict):
        versions = ", ".join(
            f"{_clean(name, 40)} `{_clean(value, 80)}`"
            for name, value in list(runner.items())[:6]
            if value is not None
        )
        if versions:
            lines.append("Report runner: " + versions)
    binary = report.get("binary")
    if isinstance(binary, dict):
        version = binary.get("version") or binary.get("productVersion")
        digest = binary.get("sha256") or binary.get("binarySha256")
        if version:
            lines.append(f"Reported binary version: `{_clean(version, 100)}`")
        if digest:
            lines.append(f"Reported binary SHA-256: `{_clean(digest, 100)}`")
    for limit in report.get("evidenceLimits", [])[:8] if isinstance(report.get("evidenceLimits"), list) else []:
        lines.append(f"Evidence limit: {_clean(limit)}")
    for limit in report.get("notValidated", [])[:8] if isinstance(report.get("notValidated"), list) else []:
        lines.append(f"Not validated: {_clean(limit)}")
    return lines


def render_summary(
    context: dict[str, str],
    *,
    title: str,
    steps: dict[str, str] | None = None,
    report: dict[str, Any] | None = None,
    report_required: bool = False,
    details: list[str] | None = None,
    evidence_limits: list[str] | None = None,
    max_bytes: int = MAX_SUMMARY_BYTES,
) -> str:
    required = ("source_commit", "workflow", "run_id", "run_attempt", "job_result", "job_name")
    if any(not context.get(name) for name in required):
        raise ValueError("summary requires source commit, workflow, run ID/attempt, and job identity")
    if not SHA_PATTERN.fullmatch(context["source_commit"]):
        raise ValueError("summary source commit must be a full hexadecimal Git SHA")
    if not context["run_id"].isdecimal() or not context["run_attempt"].isdecimal():
        raise ValueError("summary run ID and attempt must be numeric")
    if context["job_result"] not in JOB_RESULTS:
        raise ValueError("summary job conclusion is unknown")
    if max_bytes < 512:
        raise ValueError("summary byte limit is too small")

    lines = [
        f"## {_clean(title, 120)}",
        "",
        f"Source commit: `{context['source_commit']}`",
        f"Workflow run: `{_clean(context['workflow'], 120)}` / `{context['run_id']}` / attempt {context['run_attempt']}",
        f"Job: `{_clean(context['job_name'], 160)}`",
        f"Job conclusion: `{context['job_result']}`",
        f"Runner: `{_clean(context.get('runner_os', 'unknown'), 80)}` / `{_clean(context.get('runner_name', 'unknown'), 120)}`; image `{_clean(context.get('runner_image', 'unknown'), 120)}` `{_clean(context.get('runner_image_version', 'unknown'), 80)}`",
        f"Tools: Python `{_clean(context.get('python_version', 'unavailable'), 80)}`; .NET `{_clean(context.get('dotnet_version', 'unavailable'), 80)}`",
    ]

    if steps:
        invalid = {name: result for name, result in steps.items() if result not in STEP_RESULTS}
        if invalid:
            raise ValueError("summary step result is unknown")
        lines.extend(["", "### Step conclusions"])
        lines.extend(f"- `{_clean(name, 100)}`: `{result}`" for name, result in list(steps.items())[:24])
        if len(steps) > 24:
            lines.append(f"- {len(steps) - 24} additional step conclusions omitted.")

    if report is None and report_required:
        lines.extend(["", "Machine report: unavailable; this is not a pass and no report contents were inspected."])
    elif report is not None:
        _validate_report(report, context)
        lines.extend(["", *_report_lines(report)])

    if details:
        lines.extend(["", "### Results"])
        lines.extend(f"- {_clean(detail)}" for detail in details[:24])
        if len(details) > 24:
            lines.append(f"- {len(details) - 24} additional result lines omitted.")

    limits = evidence_limits or [
        "Hosted synthetic/managed checks do not establish Office/WPS compatibility or physical print output.",
        "Only the bounded report fields listed above were summarized; artifact contents are not claimed inspected.",
    ]
    lines.extend(["", "### Evidence limits"])
    lines.extend(f"- {_clean(item)}" for item in limits[:8])
    if len(limits) > 8:
        lines.append(f"- {len(limits) - 8} additional evidence limits omitted.")

    summary = "\n".join(lines) + "\n"
    encoded = summary.encode("utf-8")
    if len(encoded) <= max_bytes:
        return summary

    header = "\n".join(lines[:8]) + "\n\n"
    tail_start = next((index for index, line in enumerate(lines) if line == "### Evidence limits"), len(lines) - 1)
    tail = "\n".join(lines[tail_start:]) + "\n"
    marker = "\n[Additional summary details omitted to stay within the byte limit.]\n\n"
    reserve = len((header + marker + tail).encode("utf-8"))
    if reserve > max_bytes:
        raise ValueError("identity and evidence-limit sections exceed the summary byte limit")
    body = "\n".join(lines[8:tail_start])
    budget = max_bytes - reserve
    body_bytes = body.encode("utf-8")[:budget]
    body = body_bytes.decode("utf-8", errors="ignore")
    return header + body + marker + tail


def _version(command: list[str]) -> str:
    try:
        result = subprocess.run(command, capture_output=True, text=True, timeout=5, check=False)
    except (OSError, subprocess.SubprocessError):
        return "unavailable"
    if result.returncode != 0:
        return "unavailable"
    return _clean((result.stdout or result.stderr).strip().splitlines()[0] if (result.stdout or result.stderr).strip() else "unavailable", 80)


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--title", required=True)
    parser.add_argument("--report", type=Path)
    parser.add_argument("--report-required", action="store_true")
    parser.add_argument("--summary-path", type=Path)
    args = parser.parse_args()

    context = {
        "source_commit": os.environ.get("PB_SOURCE_COMMIT", os.environ.get("GITHUB_SHA", "")),
        "workflow": os.environ.get("GITHUB_WORKFLOW", ""),
        "run_id": os.environ.get("GITHUB_RUN_ID", ""),
        "run_attempt": os.environ.get("GITHUB_RUN_ATTEMPT", ""),
        "job_result": os.environ.get("PB_JOB_RESULT", "unknown"),
        "job_name": os.environ.get("PB_JOB_NAME", "unknown job"),
        "runner_os": os.environ.get("RUNNER_OS", "unknown"),
        "runner_name": os.environ.get("RUNNER_NAME", "unknown"),
        "runner_image": os.environ.get("ImageOS", platform.platform()),
        "runner_image_version": os.environ.get("ImageVersion", "unknown"),
        "python_version": _version([sys.executable, "--version"]),
        "dotnet_version": _version(["dotnet", "--version"]),
    }
    steps = json.loads(os.environ.get("PB_STEP_OUTCOMES", "{}"))
    if not isinstance(steps, dict):
        raise ValueError("PB_STEP_OUTCOMES must be a JSON object")
    report = None
    if args.report is not None and args.report.is_file():
        report = read_report(args.report)
    summary = render_summary(
        context,
        title=args.title,
        steps={str(name): str(value) for name, value in steps.items()},
        report=report,
        report_required=args.report_required,
    )
    output = args.summary_path or (Path(os.environ["GITHUB_STEP_SUMMARY"]) if os.environ.get("GITHUB_STEP_SUMMARY") else None)
    if output is None:
        raise ValueError("GITHUB_STEP_SUMMARY or --summary-path is required")
    with output.open("a", encoding="utf-8", newline="\n") as stream:
        stream.write(summary)
    return 1 if args.report_required and report is None else 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except (OSError, ValueError, json.JSONDecodeError) as error:
        print(f"CI job summary failed: {error}", file=sys.stderr)
        raise SystemExit(1)
