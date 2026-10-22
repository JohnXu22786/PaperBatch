#!/usr/bin/env python3
"""Run bounded IPP protocol scenarios against the pinned CUPS test receiver."""

from __future__ import annotations

import argparse
import collections
import json
import os
import re
import select
import signal
import socket
import socketserver
import struct
import subprocess
import sys
import tempfile
import threading
import time
from pathlib import Path
from typing import Any, Callable


LAB_DIRECTORY = Path(__file__).resolve().parent / "ipp-lab"
MAX_DOCUMENT_BYTES = 512 * 1024
MAX_HTTP_HEADER_BYTES = 8 * 1024
MAX_REQUEST_BYTES = MAX_DOCUMENT_BYTES + 16 * 1024
MAX_RESPONSE_BYTES = MAX_REQUEST_BYTES + 64 * 1024
MAX_TIMEOUT_SECONDS = 5
MAX_HANDLER_SECONDS = MAX_TIMEOUT_SECONDS + 2
MAX_STARTUP_SECONDS = 5
MAX_IPPTOOL_OUTPUT_BYTES = 64 * 1024
MAX_TRACE_EVENTS = 24
FAULT_CLASSIFICATIONS = {
    "delayed-response": "uncertain-timeout",
    "connection-reset": "uncertain-transport",
    "unavailable-503": "uncertain-http",
    "auth-rejected": "rejected-auth",
    "receiver-restart": "uncertain-transport",
    "malformed-ipp": "uncertain-protocol",
    "partial-upload": "uncertain-transport",
}
SCENARIOS = ("success", "duplicate-submit", *FAULT_CLASSIFICATIONS)
OPERATION_NAMES = {
    0x0002: "Print-Job",
    0x0005: "Create-Job",
    0x0008: "Cancel-Job",
    0x0009: "Get-Job-Attributes",
    0x000A: "Get-Jobs",
    0x000B: "Get-Printer-Attributes",
}
JOB_ID_PATTERN = re.compile(rb"job-id\s+\(integer\)\s*=\s*(\d+)", re.IGNORECASE)


def validate_timeout(seconds: int) -> int:
    if isinstance(seconds, bool) or not isinstance(seconds, int):
        raise ValueError("timeout must be an integer number of seconds")
    if not 1 <= seconds <= MAX_TIMEOUT_SECONDS:
        raise ValueError(f"timeout must be between 1 and {MAX_TIMEOUT_SECONDS} seconds")
    return seconds


def validate_document_size(document: bytes) -> bytes:
    if not isinstance(document, bytes):
        raise ValueError("synthetic document must be bytes")
    if len(document) > MAX_DOCUMENT_BYTES:
        raise ValueError(f"synthetic document exceeds {MAX_DOCUMENT_BYTES} bytes")
    return document


def _free_loopback_port() -> int:
    with socket.socket(socket.AF_INET, socket.SOCK_STREAM) as sock:
        sock.bind(("127.0.0.1", 0))
        return int(sock.getsockname()[1])


def _remaining_until(deadline: float) -> float:
    remaining = deadline - time.monotonic()
    if remaining <= 0:
        raise TimeoutError("HTTP operation exceeded its deadline")
    return remaining


def _recv_until(connection: socket.socket, size: int, deadline: float) -> bytes:
    connection.settimeout(_remaining_until(deadline))
    return connection.recv(size)


def _send_until(connection: socket.socket, data: bytes, deadline: float) -> None:
    connection.settimeout(_remaining_until(deadline))
    connection.sendall(data)


def _read_http_request(
    connection: socket.socket,
    *,
    deadline: float | None = None,
) -> tuple[bytes, bytes, int]:
    if deadline is None:
        deadline = time.monotonic() + MAX_HANDLER_SECONDS
    data = bytearray()
    separator = b"\r\n\r\n"
    while separator not in data:
        chunk = _recv_until(connection, min(2048, MAX_HTTP_HEADER_BYTES + 1 - len(data)), deadline)
        if not chunk:
            raise ValueError("incomplete HTTP header")
        data.extend(chunk)
        if len(data) > MAX_HTTP_HEADER_BYTES:
            raise ValueError("HTTP header exceeds limit")

    header, buffered_body = bytes(data).split(separator, 1)
    lines = header.split(b"\r\n")
    if not lines or not lines[0].startswith(b"POST "):
        raise ValueError("expected an IPP POST request")
    headers: dict[bytes, bytes] = {}
    for line in lines[1:]:
        if b":" not in line:
            raise ValueError("malformed HTTP header")
        name, value = line.split(b":", 1)
        headers[name.strip().lower()] = value.strip()
    if b"transfer-encoding" in headers:
        raise ValueError("chunked requests are outside the lab profile")
    try:
        content_length = int(headers[b"content-length"])
    except (KeyError, ValueError) as error:
        raise ValueError("missing or invalid Content-Length") from error
    if not 8 <= content_length <= MAX_REQUEST_BYTES:
        raise ValueError("IPP request body is outside the configured bounds")

    body = bytearray(buffered_body[:content_length])
    while len(body) < content_length:
        chunk = _recv_until(connection, min(4096, content_length - len(body)), deadline)
        if not chunk:
            raise ValueError("incomplete IPP request body")
        body.extend(chunk)
    body_bytes = bytes(body)
    operation = int.from_bytes(body_bytes[2:4], "big")
    forwarded_header = _upstream_request_header(lines, content_length)
    return forwarded_header, body_bytes, operation


def _upstream_request_header(lines: list[bytes], content_length: int) -> bytes:
    kept = [lines[0]]
    for line in lines[1:]:
        name = line.partition(b":")[0].strip().lower()
        if name not in (b"connection", b"proxy-connection", b"expect"):
            kept.append(line)
    kept.append(b"Content-Length: " + str(content_length).encode("ascii"))
    kept.append(b"Connection: close")
    return b"\r\n".join(kept) + b"\r\n\r\n"


def _read_http_response(
    connection: socket.socket,
    timeout_seconds: int = MAX_TIMEOUT_SECONDS + 2,
    *,
    deadline: float | None = None,
) -> tuple[bytes, int, bytes]:
    if deadline is None:
        deadline = time.monotonic() + timeout_seconds
    data = bytearray()
    separator = b"\r\n\r\n"
    while separator not in data:
        chunk = _recv_until(connection, min(2048, MAX_HTTP_HEADER_BYTES + 1 - len(data)), deadline)
        if not chunk:
            raise ValueError("incomplete upstream HTTP response")
        data.extend(chunk)
        if len(data) > MAX_HTTP_HEADER_BYTES:
            raise ValueError("upstream HTTP header exceeds limit")

    header, buffered_body = bytes(data).split(separator, 1)
    lines = header.split(b"\r\n")
    try:
        status_code = int(lines[0].split(b" ", 2)[1])
    except (IndexError, ValueError) as error:
        raise ValueError("invalid upstream HTTP status") from error
    headers: dict[bytes, bytes] = {}
    for line in lines[1:]:
        if b":" not in line:
            raise ValueError("malformed upstream HTTP header")
        name, value = line.split(b":", 1)
        headers[name.strip().lower()] = value.strip()
    if b"transfer-encoding" in headers:
        raise ValueError("chunked upstream responses are outside the lab profile")
    try:
        content_length = int(headers[b"content-length"])
    except (KeyError, ValueError) as error:
        raise ValueError("missing or invalid upstream Content-Length") from error
    if not 0 <= content_length <= MAX_RESPONSE_BYTES:
        raise ValueError("upstream response body exceeds the configured limit")
    body = bytearray(buffered_body[:content_length])
    while len(body) < content_length:
        chunk = _recv_until(connection, min(4096, content_length - len(body)), deadline)
        if not chunk:
            raise ValueError("incomplete upstream HTTP response body")
        body.extend(chunk)
    return header, status_code, bytes(body)


def _http_response(header: bytes, body: bytes) -> bytes:
    lines = header.split(b"\r\n")
    filtered = [lines[0]]
    for line in lines[1:]:
        if line.partition(b":")[0].strip().lower() not in (
            b"content-length",
            b"connection",
            b"transfer-encoding",
        ):
            filtered.append(line)
    filtered.append(b"Content-Length: " + str(len(body)).encode("ascii"))
    filtered.append(b"Connection: close")
    return b"\r\n".join(filtered) + b"\r\n\r\n" + body


def _ipp_attribute(value_tag: int, name: str, value: bytes) -> bytes:
    encoded_name = name.encode("ascii")
    if len(encoded_name) > 0xFFFF or len(value) > 0xFFFF:
        raise ValueError("IPP attribute exceeds the supported encoding length")
    return (
        bytes((value_tag,))
        + struct.pack(">H", len(encoded_name))
        + encoded_name
        + struct.pack(">H", len(value))
        + value
    )


def _single_print_job_request(document: bytes, printer_uri: str, request_id: int = 1) -> bytes:
    operation_attributes = b"\x01"
    operation_attributes += _ipp_attribute(0x47, "attributes-charset", b"utf-8")
    operation_attributes += _ipp_attribute(0x48, "attributes-natural-language", b"en")
    operation_attributes += _ipp_attribute(0x45, "printer-uri", printer_uri.encode("ascii"))
    operation_attributes += _ipp_attribute(0x42, "requesting-user-name", b"paperbatch-lab")
    operation_attributes += _ipp_attribute(0x42, "job-name", f"paperbatch-synthetic-print-{request_id}".encode("ascii"))
    operation_attributes += _ipp_attribute(0x49, "document-format", b"application/pdf")
    ipp_message = b"\x02\x00\x00\x02" + struct.pack(">I", request_id) + operation_attributes + b"\x03" + document
    if len(ipp_message) > MAX_REQUEST_BYTES:
        raise ValueError("IPP request body exceeds the configured limit")
    return ipp_message


def _response_job_id(ipp_response: bytes) -> int | None:
    if len(ipp_response) < 9:
        return None
    offset = 8
    while offset < len(ipp_response):
        value_tag = ipp_response[offset]
        offset += 1
        if value_tag == 0x03:
            break
        if value_tag <= 0x0F:
            continue
        if offset + 2 > len(ipp_response):
            return None
        name_length = int.from_bytes(ipp_response[offset : offset + 2], "big")
        offset += 2
        if offset + name_length + 2 > len(ipp_response):
            return None
        name = ipp_response[offset : offset + name_length]
        offset += name_length
        value_length = int.from_bytes(ipp_response[offset : offset + 2], "big")
        offset += 2
        if offset + value_length > len(ipp_response):
            return None
        value = ipp_response[offset : offset + value_length]
        offset += value_length
        if name == b"job-id" and value_tag == 0x21 and value_length == 4:
            return int.from_bytes(value, "big")
    return None


def _attempt_single_print_job(
    proxy_port: int,
    receiver_port: int,
    document: bytes,
    timeout_seconds: int,
    request_id: int = 1,
    diagnostics: list[str] | None = None,
    job_ids: list[int] | None = None,
) -> bool:
    """Send one Print-Job over a raw socket so transport faults cannot trigger a library retry."""
    printer_uri = f"ipp://localhost:{receiver_port}/ipp/print"
    ipp_body = _single_print_job_request(document, printer_uri, request_id)
    header = (
        f"POST /ipp/print HTTP/1.1\r\n"
        f"Host: 127.0.0.1:{proxy_port}\r\n"
        "Content-Type: application/ipp\r\n"
        f"Content-Length: {len(ipp_body)}\r\n"
        "Connection: close\r\n\r\n"
    ).encode("ascii")
    deadline = time.monotonic() + timeout_seconds
    try:
        with socket.create_connection(("127.0.0.1", proxy_port), timeout=_remaining_until(deadline)) as client:
            _send_until(client, header + ipp_body, deadline)
            _, status_code, response_body = _read_http_response(client, deadline=deadline)
    except (OSError, ValueError, TimeoutError) as error:
        if diagnostics is not None:
            diagnostics.append(f"request {request_id}: {type(error).__name__}")
        return False
    if diagnostics is not None:
        ipp_status = int.from_bytes(response_body[2:4], "big") if len(response_body) >= 4 else None
        response_id = int.from_bytes(response_body[4:8], "big") if len(response_body) >= 8 else None
        diagnostics.append(f"request {request_id}: HTTP {status_code}, IPP status {ipp_status}, response id {response_id}")
    if status_code != 200 or len(response_body) < 8:
        return False
    accepted = (
        response_body[:2] == b"\x02\x00"
        and int.from_bytes(response_body[2:4], "big") == 0
        and int.from_bytes(response_body[4:8], "big") == request_id
    )
    if accepted and job_ids is not None:
        job_id = _response_job_id(response_body)
        if job_id is None:
            if diagnostics is not None:
                diagnostics.append(f"request {request_id}: accepted response omitted job-id")
            return False
        job_ids.append(job_id)
    return accepted


def _reset_socket(connection: socket.socket) -> None:
    try:
        connection.setsockopt(socket.SOL_SOCKET, socket.SO_LINGER, struct.pack("ii", 1, 0))
    except (AttributeError, OSError):
        pass
    try:
        connection.close()
    except OSError:
        pass


class FaultProxy:
    def __init__(
        self,
        upstream_port: int,
        scenario: str,
        timeout_seconds: int,
        trace_callback: Callable[[str, str], None],
        restart_receiver: Callable[[], None] | None = None,
    ) -> None:
        self.upstream_port = upstream_port
        self.scenario = scenario
        self.timeout_seconds = timeout_seconds
        self._lock = threading.Lock()
        self._fault_applied = False
        self._trace_callback = trace_callback
        self._restart_receiver = restart_receiver
        self._counts: collections.Counter[str] = collections.Counter()
        self._server = self._create_server()
        self.port = int(self._server.server_address[1])
        self._thread = threading.Thread(target=self._server.serve_forever, daemon=True)

    def _create_server(self) -> socketserver.ThreadingTCPServer:
        proxy = self

        class Handler(socketserver.BaseRequestHandler):
            def handle(self) -> None:
                proxy._handle(self.request)

        class Server(socketserver.ThreadingTCPServer):
            allow_reuse_address = True
            daemon_threads = False
            block_on_close = True

        server = Server(("127.0.0.1", 0), Handler)
        setattr(server, "proxy", self)
        return server

    def __enter__(self) -> FaultProxy:
        self._thread.start()
        return self

    def __exit__(self, *_: object) -> None:
        self._server.shutdown()
        self._server.server_close()
        self._thread.join(timeout=2)

    def _trace(self, phase: str, operation: str) -> None:
        self._trace_callback(phase, operation)

    def _handle(self, downstream: socket.socket) -> None:
        deadline = time.monotonic() + MAX_HANDLER_SECONDS
        operation_name = "unknown"
        upstream: socket.socket | None = None
        try:
            header, body, operation = _read_http_request(downstream, deadline=deadline)
            operation_name = OPERATION_NAMES.get(operation, "other")
            with self._lock:
                self._counts[operation_name] += 1
                inject = (
                    self.scenario in FAULT_CLASSIFICATIONS
                    and operation == 0x0002
                    and not self._fault_applied
                )
                if inject:
                    self._fault_applied = True
            self._trace("request-received", operation_name)

            if not inject:
                upstream = socket.create_connection(
                    ("127.0.0.1", self.upstream_port), timeout=_remaining_until(deadline)
                )
                _send_until(upstream, header + body, deadline)
                response_header, _, response_body = _read_http_response(upstream, deadline=deadline)
                _send_until(downstream, _http_response(response_header, response_body), deadline)
                self._trace("request-forwarded", operation_name)
                return

            self._trace("fault-applied", self.scenario)
            if self.scenario == "auth-rejected":
                _send_until(
                    downstream,
                    b"HTTP/1.1 401 Unauthorized\r\n"
                    b"WWW-Authenticate: Basic realm=\"PaperBatch simulation\"\r\n"
                    b"Content-Length: 0\r\n"
                    b"Connection: close\r\n\r\n",
                    deadline,
                )
                return

            if self.scenario == "unavailable-503":
                _send_until(
                    downstream,
                    b"HTTP/1.1 503 Service Unavailable\r\n"
                    b"Content-Type: text/plain\r\n"
                    b"Content-Length: 0\r\n"
                    b"Connection: close\r\n\r\n",
                    deadline,
                )
                return

            upstream = socket.create_connection(
                ("127.0.0.1", self.upstream_port), timeout=_remaining_until(deadline)
            )
            if self.scenario == "partial-upload":
                _send_until(upstream, header + body[: max(1, len(body) // 2)], deadline)
                _reset_socket(upstream)
                upstream = None
                _reset_socket(downstream)
                return

            _send_until(upstream, header + body, deadline)
            response_header, _, response_body = _read_http_response(upstream, deadline=deadline)
            if self.scenario == "delayed-response":
                time.sleep(min(self.timeout_seconds + 0.25, _remaining_until(deadline)))
                _send_until(downstream, _http_response(response_header, response_body), deadline)
            elif self.scenario == "connection-reset":
                _reset_socket(downstream)
            elif self.scenario == "malformed-ipp":
                downstream.sendall(_http_response(response_header, b"\x00"))
            elif self.scenario == "receiver-restart":
                if self._restart_receiver is None:
                    raise RuntimeError("receiver restart callback is unavailable")
                self._restart_receiver()
                _reset_socket(downstream)
            else:
                raise ValueError("unsupported fault scenario")
        except (BrokenPipeError, ConnectionResetError, OSError, ValueError, TimeoutError):
            self._trace("transport-closed", operation_name)
        finally:
            if upstream is not None:
                try:
                    upstream.close()
                except OSError:
                    pass
            try:
                downstream.close()
            except OSError:
                pass

    @property
    def fault_applied(self) -> bool:
        with self._lock:
            return self._fault_applied

    @property
    def counts(self) -> dict[str, int]:
        with self._lock:
            return {
                "printJobRequests": self._counts["Print-Job"],
                "createJobRequests": self._counts["Create-Job"],
                "getJobAttributesRequests": self._counts["Get-Job-Attributes"],
                "cancelJobRequests": self._counts["Cancel-Job"],
                "printerQueryRequests": self._counts["Get-Printer-Attributes"],
            }

class IppReceiver:
    def __init__(self) -> None:
        self.port = _free_loopback_port()
        self._temporary_directory = tempfile.TemporaryDirectory(prefix="paperbatch-ipp-")
        self.spool_directory = Path(self._temporary_directory.name) / "spool"
        self.spool_directory.mkdir(mode=0o700)
        self.process: subprocess.Popen[bytes] | None = None

    def __enter__(self) -> IppReceiver:
        executable = shutil_which("ippeveprinter")
        if executable is None:
            self._temporary_directory.cleanup()
            raise RuntimeError("ippeveprinter is not installed; use the pinned Docker lab image")
        self.process = subprocess.Popen(
            [
                executable,
                "-d",
                str(self.spool_directory),
                "-f",
                "application/pdf",
                "-c",
                "/bin/true",
                "-l",
                "Synthetic protocol lab",
                "-m",
                "PaperBatch test receiver",
                "-n",
                "localhost",
                "-p",
                str(self.port),
                "-r",
                "off",
                "PaperBatch synthetic receiver",
            ],
            cwd=self._temporary_directory.name,
            stdin=subprocess.DEVNULL,
            stdout=subprocess.DEVNULL,
            stderr=subprocess.DEVNULL,
            start_new_session=True,
        )
        deadline = time.monotonic() + MAX_STARTUP_SECONDS
        while time.monotonic() < deadline:
            if self.process.poll() is not None:
                self.__exit__(None, None, None)
                raise RuntimeError("ippeveprinter failed to start")
            try:
                with socket.create_connection(("127.0.0.1", self.port), timeout=0.1):
                    return self
            except OSError:
                time.sleep(0.05)
        self.__exit__(None, None, None)
        raise RuntimeError("ippeveprinter did not become ready")

    def restart(self) -> None:
        if self.process is not None and self.process.poll() is None:
            try:
                os.killpg(self.process.pid, signal.SIGTERM)
                self.process.wait(timeout=2)
            except (ProcessLookupError, subprocess.TimeoutExpired):
                try:
                    os.killpg(self.process.pid, signal.SIGKILL)
                except ProcessLookupError:
                    pass
                self.process.wait(timeout=2)
        self.process = None
        self.__enter__()

    def __exit__(self, *_: object) -> None:
        if self.process is not None and self.process.poll() is None:
            try:
                os.killpg(self.process.pid, signal.SIGTERM)
                self.process.wait(timeout=2)
            except (ProcessLookupError, subprocess.TimeoutExpired):
                try:
                    os.killpg(self.process.pid, signal.SIGKILL)
                except ProcessLookupError:
                    pass
                self.process.wait(timeout=2)
        self._temporary_directory.cleanup()


def shutil_which(command: str) -> str | None:
    for directory in os.environ.get("PATH", "").split(os.pathsep):
        path = Path(directory) / command
        if path.is_file() and os.access(path, os.X_OK):
            return str(path)
    return None


class LabRun:
    def __init__(self, scenario: str, timeout_seconds: int) -> None:
        self.scenario = scenario
        self.timeout_seconds = validate_timeout(timeout_seconds)
        self.trace: list[dict[str, str]] = []
        self._trace_lock = threading.Lock()

    def add_trace(self, phase: str, operation: str) -> None:
        with self._trace_lock:
            if len(self.trace) < MAX_TRACE_EVENTS:
                self.trace.append({"phase": phase, "operation": operation})

    def _run_ipptool(
        self,
        proxy_port: int,
        receiver_port: int,
        test_file: str,
        job_id: int | None = None,
    ) -> tuple[int, bytes]:
        ipptool = shutil_which("ipptool")
        if ipptool is None:
            raise RuntimeError("ipptool is not installed; use the pinned Docker lab image")
        target_uri = f"ipp://127.0.0.1:{proxy_port}/ipp/print"
        printer_uri = f"ipp://127.0.0.1:{receiver_port}/ipp/print"
        command = [
            ipptool,
            "-t",
            "-v",
            "-T",
            str(self.timeout_seconds),
            "-d",
            f"printer_uri={printer_uri}",
        ]
        if job_id is not None:
            command.extend(("-d", f"job_id={job_id}"))
        command.extend((target_uri, str(LAB_DIRECTORY / test_file)))

        process = subprocess.Popen(
            command,
            cwd=LAB_DIRECTORY,
            stdin=subprocess.DEVNULL,
            stdout=subprocess.PIPE,
            stderr=subprocess.DEVNULL,
            start_new_session=True,
        )
        assert process.stdout is not None
        output = bytearray()
        deadline = time.monotonic() + self.timeout_seconds + 3
        output_fd = process.stdout.fileno()
        while True:
            remaining = deadline - time.monotonic()
            if remaining <= 0:
                try:
                    os.killpg(process.pid, signal.SIGKILL)
                except ProcessLookupError:
                    pass
                process.wait(timeout=2)
                process.stdout.close()
                raise RuntimeError("ipptool exceeded its bounded run time")
            ready, _, _ = select.select([output_fd], [], [], min(remaining, 0.2))
            if ready:
                chunk = os.read(output_fd, 4096)
                if chunk:
                    if len(output) < MAX_IPPTOOL_OUTPUT_BYTES:
                        output.extend(chunk[: MAX_IPPTOOL_OUTPUT_BYTES - len(output)])
                    continue
            if process.poll() is not None:
                while True:
                    try:
                        chunk = os.read(output_fd, 4096)
                    except BlockingIOError:
                        break
                    if not chunk:
                        break
                    if len(output) < MAX_IPPTOOL_OUTPUT_BYTES:
                        output.extend(chunk[: MAX_IPPTOOL_OUTPUT_BYTES - len(output)])
                process.stdout.close()
                return int(process.returncode), bytes(output)

    def _job_id(self, output: bytes) -> int:
        matches = JOB_ID_PATTERN.findall(output)
        if not matches:
            raise RuntimeError("ipptool did not return the expected job receipt")
        return int(matches[0])

    def run(self) -> dict[str, Any]:
        if self.scenario not in SCENARIOS:
            raise ValueError("unknown fault-lab scenario")
        synthetic_document = validate_document_size((LAB_DIRECTORY / "synthetic.pdf").read_bytes())
        if not synthetic_document.startswith(b"%PDF-"):
            raise RuntimeError("synthetic fixture is not a PDF")

        with IppReceiver() as receiver:
            self.add_trace("receiver-ready", "loopback IPP Everywhere")
            with FaultProxy(
                receiver.port,
                self.scenario,
                self.timeout_seconds,
                self.add_trace,
                receiver.restart,
            ) as proxy:
                self.add_trace("proxy-ready", "one-shot bounded forwarding")
                if self.scenario == "duplicate-submit":
                    diagnostics: list[str] = []
                    first_job_ids: list[int] = []
                    first = _attempt_single_print_job(proxy.port, receiver.port, synthetic_document, self.timeout_seconds, request_id=1001, diagnostics=diagnostics, job_ids=first_job_ids)
                    if first:
                        deadline = time.monotonic() + self.timeout_seconds
                        completed = False
                        while time.monotonic() < deadline:
                            state = self._run_ipptool(proxy.port, receiver.port, "query-job.test", first_job_ids[0])
                            if state[0] == 0 and re.search(rb"job-state\s+\(enum\)\s*=\s*completed", state[1], re.IGNORECASE):
                                completed = True
                                break
                            time.sleep(0.1)
                        if not completed:
                            raise RuntimeError("receiver did not complete the first duplicate-submit job before the bounded deadline")
                    second = _attempt_single_print_job(proxy.port, receiver.port, synthetic_document, self.timeout_seconds, request_id=1002, diagnostics=diagnostics)
                    if not first or not second or proxy.counts["printJobRequests"] != 2:
                        raise RuntimeError(f"duplicate submission fixture did not deliver two requests (first={first}, second={second}, counts={proxy.counts}, responses={diagnostics})")
                    self.add_trace("duplicate-delivered", "Print-Job")
                    return {
                        "simulationOnly": True,
                        "evidenceClass": "protocol-emulation",
                        "scenario": self.scenario,
                        "classification": "duplicate-accepted",
                        "counts": proxy.counts,
                        "trace": self.trace,
                    }

                if self.scenario == "success":
                    result = self._run_ipptool(proxy.port, receiver.port, "submit-only.test")
                    if result[0] != 0:
                        raise RuntimeError("synthetic Print-Job did not receive a successful response")
                    print_job_id = self._job_id(result[1])
                    self.add_trace("receipt-received", "Print-Job")

                    query_result = self._run_ipptool(
                        proxy.port, receiver.port, "query-job.test", print_job_id
                    )
                    if query_result[0] != 0:
                        raise RuntimeError("Get-Job-Attributes did not return the submitted job")
                    self.add_trace("job-query-accepted", "Get-Job-Attributes")

                    create_result = self._run_ipptool(proxy.port, receiver.port, "create-job.test")
                    if create_result[0] != 0:
                        raise RuntimeError("Create-Job did not return a cancellation fixture")
                    cancel_job_id = self._job_id(create_result[1])
                    self.add_trace("pending-job-created", "Create-Job")

                    cancel_query = self._run_ipptool(
                        proxy.port, receiver.port, "query-job.test", cancel_job_id
                    )
                    if cancel_query[0] != 0:
                        raise RuntimeError("Get-Job-Attributes did not return the pending job")
                    self.add_trace("pending-job-query-accepted", "Get-Job-Attributes")
                    cancel_result = self._run_ipptool(
                        proxy.port, receiver.port, "cancel-job.test", cancel_job_id
                    )
                    if cancel_result[0] != 0:
                        raise RuntimeError("Cancel-Job was not accepted for the pending job")
                    self.add_trace("cancel-accepted", "Cancel-Job")
                    return {
                        "simulationOnly": True,
                        "evidenceClass": "protocol-emulation",
                        "scenario": self.scenario,
                        "classification": "accepted",
                        "receipt": {"jobId": print_job_id},
                        "query": {"jobId": print_job_id, "accepted": True},
                        "pendingJobQuery": {"jobId": cancel_job_id, "accepted": True},
                        "cancel": {"jobId": cancel_job_id, "accepted": True},
                        "counts": proxy.counts,
                        "trace": self.trace,
                    }

                request_accepted = _attempt_single_print_job(
                    proxy.port,
                    receiver.port,
                    synthetic_document,
                    self.timeout_seconds,
                )
                if request_accepted or not proxy.fault_applied:
                    raise RuntimeError("injected fault did not interrupt the Print-Job response")
                classification = FAULT_CLASSIFICATIONS[self.scenario]
                self.add_trace("submit-classified", classification)

                recovery = self._run_ipptool(proxy.port, receiver.port, "query-printer.test")
                if recovery[0] != 0:
                    raise RuntimeError("printer query did not recover after the one-shot fault")
                self.add_trace("recovery-query-accepted", "Get-Printer-Attributes")
                counts = proxy.counts
                if counts["printJobRequests"] != 1:
                    raise RuntimeError("faulted Print-Job was unexpectedly repeated")
                return {
                    "simulationOnly": True,
                    "evidenceClass": "protocol-emulation",
                    "scenario": self.scenario,
                    "classification": classification,
                    "counts": counts,
                    "recovery": {"printerQueryAccepted": True},
                    "trace": self.trace,
                }


def run_scenario(scenario: str, timeout_seconds: int = 1) -> dict[str, Any]:
    return LabRun(scenario, timeout_seconds).run()


def _write_result(result: dict[str, Any]) -> None:
    print(json.dumps(result, ensure_ascii=True, separators=(",", ":")))


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    subparsers = parser.add_subparsers(dest="command", required=True)
    run_parser = subparsers.add_parser("run", help="run one or every synthetic IPP scenario")
    run_parser.add_argument("--scenario", choices=("all", *SCENARIOS), default="all")
    run_parser.add_argument("--timeout", type=int, default=1)
    args = parser.parse_args(argv)

    try:
        timeout = validate_timeout(args.timeout)
        scenarios = SCENARIOS if args.scenario == "all" else (args.scenario,)
        for scenario in scenarios:
            _write_result(run_scenario(scenario, timeout))
        return 0
    except (OSError, RuntimeError, ValueError, subprocess.SubprocessError) as error:
        _write_result(
            {
                "simulationOnly": True,
                "evidenceClass": "protocol-emulation",
                "scenario": getattr(args, "scenario", "unknown"),
                "classification": "lab-error",
                "errorType": type(error).__name__,
                "trace": [],
            }
        )
        return 1


if __name__ == "__main__":
    sys.exit(main())
