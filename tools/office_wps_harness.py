#!/usr/bin/env python3
"""Fail-closed contract and bounded operator-script harness for Office/WPS labs.

This module contains no Office automation or generic executable launcher. An
interactive runner must supply a trusted adapter from the protected runner
image; ordinary CI uses only the test stub adapters.
"""

from __future__ import annotations

from dataclasses import dataclass
import hashlib
from pathlib import Path
import queue
import shutil
import tempfile
import threading
import time
from typing import Callable, Protocol

OUTCOMES = {"passed", "failed", "blocked", "unsupported", "not_run"}
SAFE_REASON_CODES = {
    "contract_metadata_missing", "invalid_expected_snapshot_contract", "untrusted_event",
    "wrong_repository", "untrusted_checkout", "wrong_checkout_sha", "wrong_snapshot",
    "licensed_ephemeral_vm_required", "wrong_software_build", "profile_isolation_required",
    "repository_secrets_present", "repository_secrets_metadata_missing", "network_policy_mismatch", "timeout_out_of_range",
    "fixture_not_allowlisted", "cancelled", "harness_error", "process_ownership_lost",
    "export_outside_run_directory", "export_too_large", "invalid_export",
    "process_close_timeout", "script_completed", "step_timeout", "adapter_error",
    "profile_reset_failed", "report_bound_exceeded", "interactive_session_unavailable",
}
MAX_REPORT_BYTES = 2048
MAX_STEP_TIMEOUT_SECONDS = 120
MAX_ABORT_GRACE_SECONDS = 2
MAX_EXPORT_BYTES = 4 * 1024 * 1024
# The snapshot is prelicensed and receives its fixture before the lab starts;
# the current approved network policy is deny-all.
APPROVED_NETWORK_ALLOWLIST: tuple[str, ...] = ()
FIXTURES = {
    "u9.office_wps.synthetic_docx_smoke": {"office", "wps"},
    "u9.office_wps.synthetic_xlsx_smoke": {"office", "wps"},
    "u9.office_wps.synthetic_pptx_smoke": {"office", "wps"},
}


@dataclass(frozen=True)
class RunnerExpectation:
    repository: str
    workflow_ref: str
    default_ref: str
    main_sha: str
    snapshot_profile: str
    snapshot_id: str
    image_digest: str
    product_builds: dict[str, str]
    network_allowlist: tuple[str, ...]


@dataclass(frozen=True)
class RunnerContext:
    event_name: str
    repository: str
    ref: str
    ref_protected: bool
    checkout_sha: str
    main_sha: str
    is_fork: bool
    workflow_ref: str


@dataclass(frozen=True)
class RunnerSnapshot:
    snapshot_profile: str
    snapshot_id: str
    image_digest: str
    ephemeral: bool
    licensed: bool
    software_builds: dict[str, str]
    profile_isolated: bool
    profile_reset: bool
    repository_secrets: tuple[str, ...]
    network_allowlist: tuple[str, ...]


@dataclass(frozen=True)
class GateResult:
    allowed: bool
    reason_code: str


@dataclass(frozen=True)
class ProcessHandle:
    """Opaque identity for one launched process; `identity` is never reported."""

    pid: int
    identity: object


class Cancelled(Exception):
    """The operator or coordinator cancelled the current scripted run."""


class Unsupported(Exception):
    """The runner cannot provide the required authorized interactive desktop."""


class UncleanAdapter(Exception):
    """Adapter call did not stop within the abort grace period."""


class ProcessOwnershipLost(Exception):
    """A process no longer belongs to the current harness run."""


class Adapter(Protocol):
    """Trusted adapter; process actions validate the stable identity atomically."""

    def abort_run(self, timeout_seconds: float) -> None: ...

    def reset_profile(self, profile_dir: Path) -> None: ...

    def wait_for_operator(self, step: str, timeout_seconds: int, cancel_event: threading.Event) -> None: ...

    def launch(self, product: str, profile_dir: Path, timeout_seconds: int, cancel_event: threading.Event) -> ProcessHandle: ...

    def owns_process(self, process: ProcessHandle) -> bool: ...

    def open_fixture(self, process: ProcessHandle, fixture_id: str, timeout_seconds: int, cancel_event: threading.Event) -> None: ...

    def export_pdf(self, process: ProcessHandle, fixture_id: str, output_dir: Path, timeout_seconds: int, cancel_event: threading.Event) -> Path: ...

    def close(self, process: ProcessHandle, timeout_seconds: int) -> None:
        """Close only if the stable process identity still matches atomically."""
        ...

    def cancel(self, process: ProcessHandle) -> None: ...

    def is_running(self, process: ProcessHandle) -> bool: ...


def authorize_native_lane(
    context: RunnerContext | None,
    expectation: RunnerExpectation | None,
    snapshot: RunnerSnapshot | None,
) -> GateResult:
    """Allow only the exact trusted protected-main checkout and VM image."""
    if context is None or expectation is None or snapshot is None:
        return GateResult(False, "contract_metadata_missing")
    profile_products = {
        "office_only": {"office"},
        "wps_only": {"wps"},
        "coexistence": {"office", "wps"},
    }
    if (
        not expectation.repository
        or expectation.default_ref != "refs/heads/main"
        or expectation.workflow_ref != f"{expectation.repository}/.github/workflows/u9-office-wps-trusted.yml@refs/heads/main"
        or not _is_sha(expectation.main_sha)
        or expectation.snapshot_profile not in profile_products
        or set(expectation.product_builds) != profile_products[expectation.snapshot_profile]
        or any(not value for value in expectation.product_builds.values())
    ):
        return GateResult(False, "invalid_expected_snapshot_contract")
    if context.event_name not in {"push", "workflow_dispatch"} or context.is_fork is not False:
        return GateResult(False, "untrusted_event")
    if context.repository != expectation.repository:
        return GateResult(False, "wrong_repository")
    if (
        context.ref != expectation.default_ref
        or context.ref != "refs/heads/main"
        or context.ref_protected is not True
        or context.workflow_ref != expectation.workflow_ref
    ):
        return GateResult(False, "untrusted_checkout")
    if (
        not _is_sha(context.checkout_sha)
        or context.checkout_sha != expectation.main_sha
        or context.main_sha != expectation.main_sha
    ):
        return GateResult(False, "wrong_checkout_sha")
    if (
        expectation.snapshot_profile not in {"office_only", "wps_only", "coexistence"}
        or snapshot.snapshot_profile != expectation.snapshot_profile
        or not expectation.snapshot_id
        or not _is_digest(expectation.image_digest)
        or snapshot.snapshot_id != expectation.snapshot_id
        or snapshot.image_digest != expectation.image_digest
    ):
        return GateResult(False, "wrong_snapshot")
    if snapshot.ephemeral is not True or snapshot.licensed is not True:
        return GateResult(False, "licensed_ephemeral_vm_required")
    if snapshot.software_builds != expectation.product_builds:
        return GateResult(False, "wrong_software_build")
    if snapshot.profile_isolated is not True or snapshot.profile_reset is not True:
        return GateResult(False, "profile_isolation_required")
    if type(snapshot.repository_secrets) is not tuple:
        return GateResult(False, "repository_secrets_metadata_missing")
    if snapshot.repository_secrets:
        return GateResult(False, "repository_secrets_present")
    if (
        expectation.network_allowlist != APPROVED_NETWORK_ALLOWLIST
        or snapshot.network_allowlist != expectation.network_allowlist
    ):
        return GateResult(False, "network_policy_mismatch")
    return GateResult(True, "trusted_snapshot_verified")


def run_interactive_harness(
    *,
    context: RunnerContext | None,
    expectation: RunnerExpectation | None,
    snapshot: RunnerSnapshot | None,
    adapter_factory: Callable[[], Adapter],
    fixture_id: str,
    product: str = "office",
    timeout_seconds: int = 60,
    cancel_event: threading.Event | None = None,
) -> dict:
    """Run the fixed launch/open/export/close/restart operator script.

    The trust and fixture checks happen before the adapter is constructed. The
    runner applies watchdog deadlines and requires run-wide adapter abort; it never accepts an
    executable path, document path, shell command, or report text from callers.
    """
    if timeout_seconds < 1 or timeout_seconds > MAX_STEP_TIMEOUT_SECONDS:
        return _result("failed", "timeout_out_of_range")
    if fixture_id not in FIXTURES or product not in FIXTURES[fixture_id]:
        return _result("blocked", "fixture_not_allowlisted")
    gate = authorize_native_lane(context, expectation, snapshot)
    if not gate.allowed:
        return _result("blocked", gate.reason_code)
    if product not in expectation.product_builds or product not in snapshot.software_builds:
        return _result("blocked", "wrong_software_build")

    cancel_event = cancel_event or threading.Event()
    if cancel_event.is_set():
        return _result("not_run", "cancelled")
    steps: list[str] = []
    adapter: Adapter | None = None
    process: ProcessHandle | None = None
    process_owned = False
    run_quiescent = True
    outcome = "failed"
    reason_code = "harness_error"
    export_bytes = 0
    export_digest = ""
    profile_dir: Path | None = None
    run_dir: Path | None = None

    try:
        adapter = adapter_factory()
        run_dir = Path(tempfile.mkdtemp(prefix="u9-office-wps-"))
        profile_dir = run_dir / "profile"
        profile_dir.mkdir()
        output_dir = run_dir / "output"
        output_dir.mkdir()

        _bounded_call(adapter, adapter.reset_profile, profile_dir, timeout_seconds=timeout_seconds, cancel_event=cancel_event)
        _check_cancel(cancel_event)
        _operator_step(adapter, "launch", timeout_seconds, cancel_event)
        steps.append("launch")
        process = _bounded_call(adapter, adapter.launch, product, profile_dir, timeout_seconds, cancel_event,
                            timeout_seconds=timeout_seconds, cancel_event=cancel_event)
        process_owned = True
        _check_owned(adapter, process, timeout_seconds, cancel_event)

        _operator_step(adapter, "open", timeout_seconds, cancel_event)
        steps.append("open")
        _bounded_call(adapter, adapter.open_fixture, process, fixture_id, timeout_seconds, cancel_event,
                      timeout_seconds=timeout_seconds, cancel_event=cancel_event)
        _check_owned(adapter, process, timeout_seconds, cancel_event)

        _operator_step(adapter, "export", timeout_seconds, cancel_event)
        steps.append("export")
        exported = Path(_bounded_call(adapter, adapter.export_pdf, process, fixture_id, output_dir, timeout_seconds, cancel_event,
                                      timeout_seconds=timeout_seconds, cancel_event=cancel_event))
        _check_owned(adapter, process, timeout_seconds, cancel_event)
        if not _is_within(exported, output_dir) or not exported.is_file():
            raise HarnessFailure("export_outside_run_directory")
        if exported.stat().st_size > MAX_EXPORT_BYTES:
            raise HarnessFailure("export_too_large")
        with exported.open("rb") as stream:
            header = stream.read(8)
            digest = hashlib.sha256(header)
            total = len(header)
            while True:
                chunk = stream.read(min(64 * 1024, MAX_EXPORT_BYTES + 1 - total))
                if not chunk:
                    break
                total += len(chunk)
                if total > MAX_EXPORT_BYTES:
                    raise HarnessFailure("export_too_large")
                digest.update(chunk)
        if not header.startswith(b"%PDF-"):
            raise HarnessFailure("invalid_export")
        export_bytes = total
        export_digest = digest.hexdigest()

        _operator_step(adapter, "close", timeout_seconds, cancel_event)
        steps.append("close")
        _check_owned(adapter, process, timeout_seconds, cancel_event)
        _bounded_call(adapter, adapter.close, process, timeout_seconds,
                      timeout_seconds=timeout_seconds, cancel_event=cancel_event)
        if _bounded_call(adapter, adapter.is_running, process, timeout_seconds=timeout_seconds, cancel_event=cancel_event):
            raise HarnessFailure("process_close_timeout")
        process = None
        process_owned = False

        _check_cancel(cancel_event)
        _operator_step(adapter, "restart", timeout_seconds, cancel_event)
        steps.append("restart")
        process = _bounded_call(adapter, adapter.launch, product, profile_dir, timeout_seconds, cancel_event,
                            timeout_seconds=timeout_seconds, cancel_event=cancel_event)
        process_owned = True
        _check_owned(adapter, process, timeout_seconds, cancel_event)
        _operator_step(adapter, "close", timeout_seconds, cancel_event)
        steps.append("close")
        _check_owned(adapter, process, timeout_seconds, cancel_event)
        _bounded_call(adapter, adapter.close, process, timeout_seconds,
                      timeout_seconds=timeout_seconds, cancel_event=cancel_event)
        if _bounded_call(adapter, adapter.is_running, process, timeout_seconds=timeout_seconds, cancel_event=cancel_event):
            raise HarnessFailure("process_close_timeout")
        process = None
        process_owned = False
        outcome, reason_code = "passed", "script_completed"
    except Cancelled:
        outcome, reason_code = "not_run", "cancelled"
    except Unsupported:
        outcome, reason_code = "unsupported", "interactive_session_unavailable"
    except TimeoutError:
        outcome, reason_code = "failed", "step_timeout"
    except ProcessOwnershipLost:
        process_owned = False
        run_quiescent = False
        outcome, reason_code = "failed", "process_ownership_lost"
    except UncleanAdapter:
        run_quiescent = False
        outcome, reason_code = "failed", "adapter_error"
    except HarnessFailure as error:
        outcome, reason_code = "failed", error.reason_code
    except Exception:
        # Exception details may contain paths, document names, or user data.
        outcome, reason_code = "failed", "adapter_error"
    finally:
        if adapter is not None and process is not None and process_owned and run_quiescent:
            try:
                _check_owned(adapter, process, timeout_seconds, threading.Event())
                _bounded_call(adapter, adapter.cancel, process,
                              timeout_seconds=timeout_seconds, cancel_event=threading.Event())
            except ProcessOwnershipLost:
                process_owned = False
                run_quiescent = False
            except UncleanAdapter:
                run_quiescent = False
            except Exception:
                pass
            try:
                if not run_quiescent:
                    raise UncleanAdapter
                still_running = _bounded_call(adapter, adapter.is_running, process,
                                              timeout_seconds=timeout_seconds, cancel_event=threading.Event())
                if still_running:
                    _check_owned(adapter, process, timeout_seconds, threading.Event())
                    _bounded_call(adapter, adapter.close, process, timeout_seconds,
                                  timeout_seconds=timeout_seconds, cancel_event=threading.Event())
                    still_running = _bounded_call(adapter, adapter.is_running, process,
                                                  timeout_seconds=timeout_seconds, cancel_event=threading.Event())
                if still_running:
                    run_quiescent = False
            except Exception:
                run_quiescent = False
        if run_quiescent and adapter is not None and profile_dir is not None:
            try:
                _bounded_call(adapter, adapter.reset_profile, profile_dir,
                              timeout_seconds=timeout_seconds, cancel_event=threading.Event())
            except UncleanAdapter:
                run_quiescent = False
                outcome, reason_code = "failed", "profile_reset_failed"
            except Exception:
                run_quiescent = False
                outcome, reason_code = "failed", "profile_reset_failed"
        if run_quiescent and run_dir is not None:
            try:
                shutil.rmtree(run_dir)
            except Exception:
                if outcome == "passed":
                    outcome, reason_code = "failed", "profile_reset_failed"

    return _result(
        outcome,
        reason_code,
        steps=steps,
        evidence_class="simulation_only",
        export_bytes=export_bytes,
        export_sha256=export_digest,
    )


class HarnessFailure(Exception):
    def __init__(self, reason_code: str):
        self.reason_code = reason_code


def _result(
    outcome: str,
    reason_code: str,
    *,
    steps: list[str] | None = None,
    evidence_class: str = "none",
    export_bytes: int = 0,
    export_sha256: str = "",
) -> dict:
    if outcome not in OUTCOMES:
        raise ValueError("unknown outcome")
    if reason_code not in SAFE_REASON_CODES:
        reason_code = "adapter_error"
    result = {
        "scenarioId": "office_wps.interactive_harness.synthetic_fixture",
        "outcome": outcome,
        "reasonCode": reason_code,
        "steps": steps or [],
        "evidenceClass": evidence_class,
        "simulationOnly": True,
        "compatibilityClaim": False,
        "exportBytes": export_bytes,
        "evidenceLimit": "Harness and synthetic fixture evidence only; compatibility requires an authorized interactive run on the exact licensed VM snapshot and software build.",
    }
    if export_sha256:
        result["exportSha256"] = export_sha256
    if len(str(result).encode("utf-8")) > MAX_REPORT_BYTES:
        return {
            "scenarioId": "office_wps.interactive_harness.synthetic_fixture",
            "outcome": "failed",
            "reasonCode": "report_bound_exceeded",
            "steps": [],
            "evidenceClass": "none",
            "simulationOnly": True,
            "compatibilityClaim": False,
            "evidenceLimit": "No bounded evidence report was produced.",
        }
    return result


def _is_sha(value: str) -> bool:
    return isinstance(value, str) and len(value) in {40, 64} and all(character in "0123456789abcdef" for character in value)


def _is_digest(value: str) -> bool:
    return isinstance(value, str) and value.startswith("sha256:") and _is_sha(value.removeprefix("sha256:")) and len(value.removeprefix("sha256:")) == 64


def _check_cancel(cancel_event: threading.Event) -> None:
    if cancel_event.is_set():
        raise Cancelled


def _operator_step(adapter: Adapter, step: str, timeout_seconds: int, cancel_event: threading.Event) -> None:
    _check_cancel(cancel_event)
    _bounded_call(adapter, adapter.wait_for_operator, step, timeout_seconds, cancel_event,
                  timeout_seconds=timeout_seconds, cancel_event=cancel_event)
    _check_cancel(cancel_event)


def _check_owned(adapter: Adapter, process: ProcessHandle, timeout_seconds: int, cancel_event: threading.Event) -> None:
    if (
        not isinstance(process, ProcessHandle)
        or type(process.pid) is not int
        or process.pid <= 0
        or process.identity is None
        or not _bounded_call(
            adapter, adapter.owns_process, process, timeout_seconds=timeout_seconds, cancel_event=cancel_event
        )
    ):
        raise ProcessOwnershipLost


def _bounded_call(
    adapter: Adapter,
    function: Callable,
    *args,
    timeout_seconds: int,
    cancel_event: threading.Event,
):
    """Watch an adapter call and abort the full run on deadline or cancellation.

    The trusted adapter must interrupt active calls, terminate processes even
    when launch has not returned a PID, and wait until run activity is quiescent.
    """
    _check_cancel(cancel_event)
    result: queue.Queue = queue.Queue(maxsize=1)

    def invoke():
        try:
            result.put((True, function(*args)))
        except BaseException as error:
            result.put((False, error))

    worker = threading.Thread(target=invoke, name="u9-office-wps-adapter", daemon=True)
    worker.start()
    deadline = time.monotonic() + timeout_seconds
    while True:
        remaining = deadline - time.monotonic()
        if remaining <= 0 or cancel_event.is_set():
            if not _abort_adapter(adapter, worker):
                raise UncleanAdapter
            worker.join(MAX_ABORT_GRACE_SECONDS)
            if cancel_event.is_set():
                raise Cancelled
            raise TimeoutError
        try:
            success, value = result.get(timeout=min(0.05, remaining))
        except queue.Empty:
            continue
        if success:
            return value
        raise value


def _abort_adapter(adapter: Adapter, worker: threading.Thread) -> bool:
    completed = threading.Event()
    succeeded = threading.Event()

    def abort():
        try:
            adapter.abort_run(MAX_ABORT_GRACE_SECONDS)
            succeeded.set()
        except Exception:
            pass
        finally:
            completed.set()

    threading.Thread(target=abort, name="u9-office-wps-abort", daemon=True).start()
    abort_completed = completed.wait(MAX_ABORT_GRACE_SECONDS)
    worker.join(MAX_ABORT_GRACE_SECONDS)
    return abort_completed and succeeded.is_set() and not worker.is_alive()


def _is_within(path: Path, parent: Path) -> bool:
    try:
        path.resolve().relative_to(parent.resolve())
        return True
    except (OSError, ValueError):
        return False
