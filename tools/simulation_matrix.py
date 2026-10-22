#!/usr/bin/env python3
"""Run a synthetic loopback protocol smoke and emit bounded provenance JSON."""

from __future__ import annotations

import argparse
from datetime import datetime, timezone
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
import json
import os
from pathlib import Path
import platform
import re
import subprocess
import sys
import threading

ROOT = Path(__file__).resolve().parents[1]
MANIFEST = ROOT / "tools" / "simulation" / "scenarios.json"
OUTCOMES = {"passed", "failed", "blocked", "unsupported", "not_run"}
def _ipp_attribute(value_tag: int, name: str, value: bytes) -> bytes:
    encoded_name = name.encode("ascii")
    return bytes((value_tag,)) + len(encoded_name).to_bytes(2, "big") + encoded_name + len(value).to_bytes(2, "big") + value


REQUEST = (
    b"\x02\x00\x00\x0b\x00\x00\x00\x01\x01"
    + _ipp_attribute(0x47, "attributes-charset", b"utf-8")
    + _ipp_attribute(0x48, "attributes-natural-language", b"en")
    + _ipp_attribute(0x45, "printer-uri", b"ipp://127.0.0.1/printers/synthetic")
    + b"\x03"
)
RESPONSE = (
    b"\x02\x00\x00\x00\x00\x00\x00\x01\x01"
    + _ipp_attribute(0x47, "attributes-charset", b"utf-8")
    + _ipp_attribute(0x48, "attributes-natural-language", b"en")
    + b"\x04"
    + _ipp_attribute(0x42, "printer-name", b"PaperBatch synthetic")
    + b"\x03"
)


def _manifest() -> dict:
    value = json.loads(MANIFEST.read_text(encoding="utf-8"))
    if value.get("schemaVersion") != 1 or set(value.get("outcomes", [])) != OUTCOMES:
        raise ValueError("simulation manifest schema or outcomes are invalid")
    return value


def build_report(scenarios: list[dict], *, commit: str, os_name: str, runner: str, runner_image: str, runner_image_version: str, python_version: str) -> dict:
    if not re.fullmatch(r"[0-9a-f]{40,64}", commit):
        raise ValueError("report commit must be a full hexadecimal Git SHA")
    if not os_name or not runner or not runner_image or not runner_image_version or not python_version:
        raise ValueError("report provenance fields must be non-empty")
    for scenario in scenarios:
        if not scenario.get("scenarioId") or scenario.get("outcome") not in OUTCOMES or not scenario.get("evidenceLimit"):
            raise ValueError("each scenario needs an ID, known outcome, and evidence limit")
    return {
        "schemaVersion": 1,
        "commit": commit,
        "os": os_name,
        "runner": runner,
        "runnerImage": runner_image,
        "runnerImageVersion": runner_image_version,
        "toolVersions": {"python": python_version},
        "generatedAtUtc": datetime.now(timezone.utc).isoformat(),
        "scenarios": scenarios,
    }


def _valid_get_printer_attributes_request(body: bytes) -> bool:
    if len(body) < 9 or body[:8] != b"\x02\x00\x00\x0b\x00\x00\x00\x01" or body[8] != 0x01:
        return False
    offset = 9
    attributes = {}
    while offset < len(body):
        value_tag = body[offset]
        offset += 1
        if value_tag == 0x03:
            return offset == len(body) and all(
                attributes.get(name) == expected
                for name, expected in {
                    "attributes-charset": (0x47, b"utf-8"),
                    "attributes-natural-language": (0x48, b"en"),
                    "printer-uri": (0x45, b"ipp://127.0.0.1/printers/synthetic"),
                }.items()
            )
        if offset + 2 > len(body):
            return False
        name_length = int.from_bytes(body[offset : offset + 2], "big")
        offset += 2
        if offset + name_length + 2 > len(body):
            return False
        name = body[offset : offset + name_length].decode("ascii", errors="ignore")
        offset += name_length
        value_length = int.from_bytes(body[offset : offset + 2], "big")
        offset += 2
        if offset + value_length > len(body):
            return False
        value = body[offset : offset + value_length]
        offset += value_length
        attributes[name] = (value_tag, value)
    return False


def _read_attribute_group(body: bytes, offset: int, ending_tags: set[int]) -> tuple[dict, int, int] | None:
    attributes = {}
    while offset < len(body):
        value_tag = body[offset]
        offset += 1
        if value_tag in ending_tags:
            return attributes, offset, value_tag
        if offset + 2 > len(body):
            return None
        name_length = int.from_bytes(body[offset : offset + 2], "big")
        offset += 2
        if offset + name_length + 2 > len(body):
            return None
        name = body[offset : offset + name_length].decode("ascii", errors="ignore")
        offset += name_length
        value_length = int.from_bytes(body[offset : offset + 2], "big")
        offset += 2
        if offset + value_length > len(body):
            return None
        value = body[offset : offset + value_length]
        offset += value_length
        attributes[name] = (value_tag, value)
    return None


def _valid_get_printer_attributes_response(body: bytes) -> bool:
    if len(body) < 10 or body[:2] != b"\x02\x00" or body[2:4] != b"\x00\x00" or body[4:8] != b"\x00\x00\x00\x01" or body[8] != 0x01:
        return False
    operation_group = _read_attribute_group(body, 9, {0x04})
    if operation_group is None:
        return False
    operation_attributes, offset, _ = operation_group
    if operation_attributes.get("attributes-charset") != (0x47, b"utf-8") or operation_attributes.get("attributes-natural-language") != (0x48, b"en"):
        return False
    printer_group = _read_attribute_group(body, offset, {0x03})
    if printer_group is None:
        return False
    printer_attributes, offset, _ = printer_group
    return offset == len(body) and printer_attributes.get("printer-name") == (0x42, b"PaperBatch synthetic")


def run_smoke(lane: str) -> dict:
    config = _manifest()["lanes"].get(lane)
    if config is None:
        raise ValueError(f"unsupported lane: {lane}")
    observations: list[dict] = []

    class Handler(BaseHTTPRequestHandler):
        def do_POST(self):  # noqa: N802
            length = int(self.headers.get("Content-Length", "-1"))
            body = self.rfile.read(length)
            valid = self.path == "/ipp/print" and self.headers.get("Content-Type") == "application/ipp" and _valid_get_printer_attributes_request(body) and body == REQUEST
            observations.append({"validRequest": valid, "requestBytes": len(body)})
            response = RESPONSE if valid else b"\x02\x00\x00\x02\x00\x00\x00\x01\x03"
            self.send_response(200 if valid else 400)
            self.send_header("Content-Type", "application/ipp")
            self.send_header("Content-Length", str(len(response)))
            self.end_headers()
            self.wfile.write(response)

        def log_message(self, *_args):
            return

    server = ThreadingHTTPServer(("127.0.0.1", 0), Handler)
    server_thread = threading.Thread(target=server.serve_forever, daemon=True)
    server_thread.start()
    status = 0
    received = b""
    try:
        import http.client
        connection = http.client.HTTPConnection("127.0.0.1", server.server_port, timeout=3)
        connection.request("POST", "/ipp/print", body=REQUEST, headers={"Content-Type": "application/ipp"})
        response = connection.getresponse()
        status, received = response.status, response.read(4096)
        connection.close()
    finally:
        server.shutdown()
        server.server_close()
        server_thread.join(timeout=3)
    passed = len(observations) == 1 and observations[0]["validRequest"] and status == 200 and _valid_get_printer_attributes_response(received)
    return {
        "scenarioId": config["scenarioId"],
        "outcome": "passed" if passed else "failed",
        "endpoint": "fake_loopback_endpoint",
        "client": config["client"],
        "simulationOnly": True,
        "evidenceLimit": config["evidenceLimit"],
        "assertions": {"requestAccepted": bool(observations and observations[0]["validRequest"]), "responseStatus": status, "responseMatched": _valid_get_printer_attributes_response(received)},
    }


def native_runner_stub() -> dict:
    config = _manifest()["nativeOfficeWps"]
    return {
        "scenarioId": config["scenarioId"],
        "outcome": config["outcome"],
        "simulationOnly": True,
        "evidenceLimit": config["reason"] + " The runner contract requires trusted protected-main/manual dispatch, exact main checkout validation, a disposable snapshot, no repository secrets, and minimum allowlisted network. No native Office/WPS compatibility was tested.",
    }


def _commit() -> str:
    value = os.environ.get("GITHUB_SHA")
    if value:
        return value
    try:
        return subprocess.check_output(["git", "rev-parse", "HEAD"], cwd=ROOT, text=True, stderr=subprocess.DEVNULL).strip()
    except (OSError, subprocess.CalledProcessError):
        raise RuntimeError("cannot determine the report commit SHA")


def aggregate_scenarios(lane_reports: dict[str, str], job_results: dict[str, str], *, commit: str) -> list[dict]:
    scenarios = []
    for lane in ("linux", "windows", "macos"):
        raw_report = lane_reports.get(lane, "")
        job_result = job_results.get(lane, "unknown")
        if raw_report:
            try:
                lane_report = json.loads(raw_report)
                if not isinstance(lane_report, dict) or not isinstance(lane_report.get("scenarios"), list):
                    raise ValueError("lane report has an invalid shape")
                if lane_report.get("commit") != commit or len(lane_report["scenarios"]) != 1:
                    raise ValueError("lane report commit or scenario count is invalid")
                required_provenance = ("os", "runner", "runnerImage", "runnerImageVersion")
                if any(not isinstance(lane_report.get(key), str) or not lane_report[key] for key in required_provenance):
                    raise ValueError("lane report is missing OS or runner provenance")
                if not isinstance(lane_report.get("toolVersions"), dict) or not lane_report["toolVersions"].get("python"):
                    raise ValueError("lane report is missing tool version provenance")
                scenario = lane_report["scenarios"][0]
                if not isinstance(scenario, dict):
                    raise ValueError("lane scenario has an invalid shape")
                if scenario.get("scenarioId") != _manifest()["lanes"][lane]["scenarioId"]:
                    raise ValueError("lane report scenario ID is invalid")
                if scenario.get("outcome") not in OUTCOMES or not scenario.get("evidenceLimit"):
                    raise ValueError("lane report outcome or evidence limit is invalid")
                scenario["jobResult"] = job_result
                if job_result != "success" and scenario["outcome"] == "passed":
                    scenario["scenarioOutcome"] = "passed"
                    scenario["outcome"] = "not_run" if job_result in {"skipped", "cancelled"} else "failed"
                    scenario["reason"] = f"Scenario exchange reported passed, but hosted job result was {job_result}."
                scenario["runnerProvenance"] = {
                    key: lane_report.get(key)
                    for key in ("os", "runner", "runnerImage", "runnerImageVersion", "toolVersions")
                }
                scenarios.append(scenario)
                continue
            except (json.JSONDecodeError, ValueError, TypeError, KeyError):
                job_result = "invalid_report"
        outcome = "unsupported" if job_result == "skipped" else "not_run"
        scenarios.append({
            "scenarioId": _manifest()["lanes"][lane]["scenarioId"],
            "outcome": outcome,
            "jobResult": job_result,
            "client": _manifest()["lanes"][lane]["client"],
            "simulationOnly": True,
            "reason": f"Hosted lane produced no usable report (job result: {job_result}); check runner availability/quota and lane logs. This is not a passed run.",
            "evidenceLimit": "No scenario execution evidence was produced for this OS lane.",
        })
    scenarios.append(native_runner_stub())
    return scenarios


def _aggregate_exit_code(scenarios: list[dict]) -> int:
    failed_results = {"failure", "cancelled", "timed_out", "action_required", "invalid_report"}
    return 1 if any(scenario.get("outcome") == "failed" or scenario.get("jobResult") in failed_results for scenario in scenarios) else 0


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--lane", choices=("linux", "windows", "macos"))
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--include-native-stub", action="store_true")
    parser.add_argument("--aggregate", action="store_true")
    parser.add_argument("--publish-output", action="store_true")
    args = parser.parse_args()
    if args.aggregate:
        reports = {lane: os.environ.get(f"U9_{lane.upper()}_REPORT", "") for lane in ("linux", "windows", "macos")}
        results = {lane: os.environ.get(f"U9_{lane.upper()}_RESULT", "unknown") for lane in ("linux", "windows", "macos")}
        scenarios = aggregate_scenarios(reports, results, commit=_commit())
        report = build_report(
            scenarios,
            commit=_commit(),
            os_name=platform.platform(),
            runner=os.environ.get("RUNNER_NAME", os.environ.get("RUNNER_OS", "local")),
            runner_image=os.environ.get("ImageOS", platform.platform()),
            runner_image_version=os.environ.get("ImageVersion", "unknown"),
            python_version=platform.python_version(),
        )
        args.output.parent.mkdir(parents=True, exist_ok=True)
        args.output.write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
        print(json.dumps({"commit": report["commit"], "outcomes": [s["outcome"] for s in scenarios]}, sort_keys=True))
        return _aggregate_exit_code(scenarios)

    if args.lane is None:
        parser.error("--lane is required unless --aggregate is used")
    try:
        smoke = run_smoke(args.lane)
    except Exception as error:
        config = _manifest()["lanes"][args.lane]
        smoke = {
            "scenarioId": config["scenarioId"],
            "outcome": "failed",
            "endpoint": "fake_loopback_endpoint",
            "client": config["client"],
            "simulationOnly": True,
            "evidenceLimit": config["evidenceLimit"],
            "failureType": type(error).__name__,
            "failure": "Synthetic loopback exchange could not complete.",
        }
    scenarios = [smoke]
    if args.include_native_stub:
        scenarios.append(native_runner_stub())
    report = build_report(
        scenarios,
        commit=_commit(),
        os_name=platform.platform(),
        runner=os.environ.get("RUNNER_NAME", os.environ.get("RUNNER_OS", "local")),
        runner_image=os.environ.get("ImageOS", platform.platform()),
        runner_image_version=os.environ.get("ImageVersion", "unknown"),
        python_version=platform.python_version(),
    )
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
    if args.publish_output and os.environ.get("GITHUB_OUTPUT"):
        with Path(os.environ["GITHUB_OUTPUT"]).open("a", encoding="utf-8") as output:
            output.write("report=" + json.dumps(report, separators=(",", ":")) + "\n")
    print(json.dumps({"commit": report["commit"], "os": report["os"], "scenarioIds": [s["scenarioId"] for s in scenarios], "outcomes": [s["outcome"] for s in scenarios]}, sort_keys=True))
    return 1 if any(s["outcome"] == "failed" for s in scenarios) else 0


if __name__ == "__main__":
    raise SystemExit(main())
