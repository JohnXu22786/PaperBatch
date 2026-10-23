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
import shutil
import subprocess
import sys
import threading
from urllib.parse import urlsplit

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
    + _ipp_attribute(0x45, "printer-uri-supported", b"ipp://127.0.0.1/printers/synthetic")
    + b"\x03"
)


def _manifest() -> dict:
    value = json.loads(MANIFEST.read_text(encoding="utf-8"))
    if value.get("schemaVersion") != 1 or set(value.get("outcomes", [])) != OUTCOMES:
        raise ValueError("simulation manifest schema or outcomes are invalid")
    return value


def build_report(
    scenarios: list[dict],
    *,
    commit: str,
    os_name: str,
    runner: str,
    runner_image: str,
    runner_image_version: str,
    python_version: str,
    additional_tool_versions: dict[str, str] | None = None,
) -> dict:
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
        "toolVersions": {"python": python_version, **(additional_tool_versions or {})},
        "generatedAtUtc": datetime.now(timezone.utc).isoformat(),
        "scenarios": scenarios,
    }


def _valid_get_printer_attributes_request(body: bytes) -> bool:
    supported_versions = {b"\x01\x01", b"\x02\x00", b"\x02\x01", b"\x02\x02"}
    if (
        len(body) < 9
        or body[:2] not in supported_versions
        or body[2:4] != b"\x00\x0b"
        or body[8] != 0x01
    ):
        return False
    group = _read_attribute_group(body, 9, {0x03})
    if group is None:
        return False
    attributes, offset, _ = group
    printer_uri = attributes.get("printer-uri")
    if not isinstance(printer_uri, tuple) or printer_uri[0] != 0x45:
        return False
    try:
        parsed_uri = urlsplit(printer_uri[1].decode("ascii"))
        loopback_uri = (
            parsed_uri.scheme == "ipp"
            and parsed_uri.hostname == "127.0.0.1"
            and parsed_uri.path.startswith("/")
            and not parsed_uri.username
            and not parsed_uri.password
        )
    except (UnicodeDecodeError, ValueError):
        loopback_uri = False
    return offset == len(body) and loopback_uri and all(
        attributes.get(name) == expected
        for name, expected in {
            "attributes-charset": (0x47, b"utf-8"),
            "attributes-natural-language": (0x48, b"en"),
        }.items()
    )


def _response_for_request(request: bytes) -> bytes:
    group = _read_attribute_group(request, 9, {0x03})
    if group is None:
        return b"\x02\x00\x00\x02\x00\x00\x00\x01\x03"
    attributes, _, _ = group
    printer_uri = attributes.get("printer-uri", (0x45, b"ipp://127.0.0.1/printers/synthetic"))[1]
    return (
        request[:2] + b"\x00\x00" + request[4:8] + b"\x01"
        + _ipp_attribute(0x47, "attributes-charset", b"utf-8")
        + _ipp_attribute(0x48, "attributes-natural-language", b"en")
        + b"\x04"
        + _ipp_attribute(0x42, "printer-name", b"PaperBatch synthetic")
        + _ipp_attribute(0x45, "printer-uri-supported", printer_uri)
        + b"\x03"
    )


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


def _valid_get_printer_attributes_response(body: bytes, request: bytes = REQUEST) -> bool:
    if len(body) < 10 or body[:2] != request[:2] or body[2:4] != b"\x00\x00" or body[4:8] != request[4:8] or body[8] != 0x01:
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
    request_group = _read_attribute_group(request, 9, {0x03})
    if request_group is None:
        return False
    request_attributes, _, _ = request_group
    return offset == len(body) and all(
        printer_attributes.get(name) == expected
        for name, expected in {
            "printer-name": (0x42, b"PaperBatch synthetic"),
            "printer-uri-supported": request_attributes.get("printer-uri"),
        }.items()
    )


def _fake_ipp_server(observations: list[dict]) -> ThreadingHTTPServer:
    class Handler(BaseHTTPRequestHandler):
        protocol_version = "HTTP/1.1"

        def do_POST(self):  # noqa: N802
            length = int(self.headers.get("Content-Length", "-1"))
            body = self.rfile.read(length)
            valid = self.path == "/ipp/print" and self.headers.get("Content-Type") == "application/ipp" and _valid_get_printer_attributes_request(body)
            observations.append({"validRequest": valid, "requestBytes": len(body)})
            response = _response_for_request(body) if valid else b"\x02\x00\x00\x02\x00\x00\x00\x01\x03"
            self.send_response(200 if valid else 400)
            self.send_header("Content-Type", "application/ipp")
            self.send_header("Content-Length", str(len(response)))
            self.end_headers()
            self.wfile.write(response)

        def log_message(self, *_args):
            return

    return ThreadingHTTPServer(("127.0.0.1", 0), Handler)


def run_smoke(lane: str) -> dict:
    config = _manifest()["lanes"].get(lane)
    if config is None:
        raise ValueError(f"unsupported lane: {lane}")
    observations: list[dict] = []

    server = _fake_ipp_server(observations)
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
        "endpoint": config["endpoint"],
        "client": config["client"],
        "simulationOnly": True,
        "evidenceLimit": config["evidenceLimit"],
        "assertions": {"requestAccepted": bool(observations and observations[0]["validRequest"]), "responseStatus": status, "responseMatched": _valid_get_printer_attributes_response(received)},
    }


def run_macos_cups_smoke() -> tuple[dict, str]:
    config = _manifest()["lanes"]["macos"]["additionalScenarios"][0]
    ipptool = shutil.which("ipptool")
    if ipptool is None:
        return ({
            "scenarioId": config["scenarioId"],
            "outcome": "unsupported",
            "endpoint": config["endpoint"],
            "client": config["client"],
            "simulationOnly": True,
            "evidenceLimit": "macOS CUPS protocol scenario not run: system ipptool is unavailable. No PaperBatch, printer, or paper output was tested.",
        }, "unavailable")

    try:
        version_result = subprocess.run([ipptool, "--version"], capture_output=True, timeout=3, check=False)
        version_text = (version_result.stdout or version_result.stderr).decode("utf-8", errors="replace").strip()
        if version_result.returncode != 0:
            version_text = ""
        version = version_text[:120] if version_text else "unknown"
    except (OSError, subprocess.SubprocessError):
        version = "unknown"
    observations: list[dict] = []
    server = _fake_ipp_server(observations)
    thread = threading.Thread(target=server.serve_forever, daemon=True)
    thread.start()
    uri = f"ipp://127.0.0.1:{server.server_port}/ipp/print"
    try:
        result = subprocess.run(
            [ipptool, "-t", "-v", "-T", "5", "-d", f"printer_uri={uri}", uri, str(ROOT / "tools" / "ipp-lab" / "query-printer.test")],
            capture_output=True,
            timeout=10,
            check=False,
            cwd=ROOT,
        )
        passed = result.returncode == 0 and len(observations) == 1 and observations[0]["validRequest"]
        scenario = {
            "scenarioId": config["scenarioId"],
            "outcome": "passed" if passed else "failed",
            "endpoint": config["endpoint"],
            "client": config["client"],
            "simulationOnly": True,
            "evidenceLimit": config["evidenceLimit"],
            "assertions": {"requestAccepted": bool(observations and observations[0]["validRequest"]), "ipptoolExitCode": result.returncode},
        }
        if not passed:
            scenario["failure"] = "The system CUPS client did not complete the bounded synthetic query-printer exchange."
        return scenario, version
    except (OSError, subprocess.SubprocessError) as error:
        return ({
            "scenarioId": config["scenarioId"],
            "outcome": "failed",
            "endpoint": config["endpoint"],
            "client": config["client"],
            "simulationOnly": True,
            "evidenceLimit": config["evidenceLimit"],
            "failureType": type(error).__name__,
            "failure": "The bounded system CUPS client exchange could not complete.",
        }, version)
    finally:
        server.shutdown()
        server.server_close()
        thread.join(timeout=3)


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
        lane_config = _manifest()["lanes"][lane]
        expected_scenarios = [lane_config, *lane_config.get("additionalScenarios", [])]
        raw_report = lane_reports.get(lane, "")
        job_result = job_results.get(lane, "unknown")
        if raw_report:
            try:
                lane_report = json.loads(raw_report)
                if not isinstance(lane_report, dict) or not isinstance(lane_report.get("scenarios"), list):
                    raise ValueError("lane report has an invalid shape")
                if lane_report.get("commit") != commit or len(lane_report["scenarios"]) != len(expected_scenarios):
                    raise ValueError("lane report commit or scenario count is invalid")
                required_provenance = ("os", "runner", "runnerImage", "runnerImageVersion")
                if any(not isinstance(lane_report.get(key), str) or not lane_report[key] for key in required_provenance):
                    raise ValueError("lane report is missing OS or runner provenance")
                tool_versions = lane_report.get("toolVersions")
                if (
                    not isinstance(tool_versions, dict)
                    or not tool_versions.get("python")
                    or any(not isinstance(name, str) or not name or not isinstance(version, str) or not version
                           for name, version in tool_versions.items())
                ):
                    raise ValueError("lane report is missing tool version provenance")
                reported = lane_report["scenarios"]
                by_id = {scenario.get("scenarioId"): scenario for scenario in reported if isinstance(scenario, dict)}
                if len(by_id) != len(reported):
                    raise ValueError("lane report contains duplicate or invalid scenario IDs")
                for expected in expected_scenarios:
                    scenario = by_id.get(expected["scenarioId"])
                    if not isinstance(scenario, dict):
                        raise ValueError("lane report is missing an expected scenario")
                    if any(scenario.get(key) != expected[key] for key in ("scenarioId", "endpoint", "client")):
                        raise ValueError("lane report scenario identity is invalid")
                    if scenario.get("outcome") not in OUTCOMES or not scenario.get("evidenceLimit"):
                        raise ValueError("lane report outcome or evidence limit is invalid")
                    scenario["os"] = lane_report["os"]
                    scenario["runnerImage"] = lane_report["runnerImage"]
                    scenario["toolVersions"] = {name: tool_versions[name] for name in sorted(tool_versions)}
                    scenario["jobResult"] = job_result
                    if job_result != "success" and scenario["outcome"] == "passed":
                        scenario["scenarioOutcome"] = "passed"
                        scenario["outcome"] = "not_run" if job_result in {"skipped", "cancelled"} else "failed"
                        scenario["reason"] = f"Scenario exchange reported passed, but hosted job result was {job_result}."
                    scenario["runnerProvenance"] = {
                        key: lane_report.get(key)
                        for key in ("os", "runner", "runnerImage", "runnerImageVersion", "toolVersions")
                    }
                    scenario["runnerProvenance"]["toolVersions"] = scenario["toolVersions"]
                    scenarios.append(scenario)
                continue
            except (json.JSONDecodeError, ValueError, TypeError, KeyError):
                job_result = "invalid_report"
        outcome = "unsupported" if job_result == "skipped" else "not_run"
        for expected in expected_scenarios:
            scenarios.append({
                "scenarioId": expected["scenarioId"],
                "outcome": outcome,
                "jobResult": job_result,
                "endpoint": expected["endpoint"],
                "client": expected["client"],
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
            "endpoint": config["endpoint"],
            "client": config["client"],
            "simulationOnly": True,
            "evidenceLimit": config["evidenceLimit"],
            "failureType": type(error).__name__,
            "failure": "Synthetic loopback exchange could not complete.",
        }
    scenarios = [smoke]
    if args.lane == "macos":
        cups_scenario, ipptool_version = run_macos_cups_smoke()
        scenarios.append(cups_scenario)
    else:
        ipptool_version = None
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
        additional_tool_versions={"ipptool": ipptool_version} if ipptool_version else None,
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
