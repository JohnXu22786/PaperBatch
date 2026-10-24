import json
import shutil
import sys
import unittest
from unittest import mock
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "tools"))
import ipp_fault_lab


@unittest.skipUnless(sys.platform.startswith("linux") and shutil.which("ippeveprinter"), "run inside the pinned Linux IPP lab image")
class IPPFaultLabTests(unittest.TestCase):
    def test_synthetic_job_has_receipt_query_and_cancel_evidence(self):
        result = ipp_fault_lab.run_scenario("success", timeout_seconds=1)

        self.assertTrue(result["simulationOnly"])
        self.assertEqual(result["evidenceClass"], "protocol-emulation")
        self.assertEqual(result["classification"], "accepted")
        self.assertIsInstance(result["receipt"]["jobId"], int)
        self.assertEqual(result["query"]["jobId"], result["receipt"]["jobId"])
        self.assertTrue(result["pendingJobQuery"]["accepted"])
        self.assertEqual(result["pendingJobQuery"]["jobId"], result["cancel"]["jobId"])
        self.assertTrue(result["cancel"]["accepted"])
        self.assertNotIn("hostname", json.dumps(result).lower())
        self.assertLessEqual(len(result["trace"]), ipp_fault_lab.MAX_TRACE_EVENTS)

    def test_each_fault_is_classified_once_and_recovers_without_resubmission(self):
        expected = {
            "delayed-response": "uncertain-timeout",
            "connection-reset": "uncertain-transport",
            "unavailable-503": "uncertain-http",
            "auth-rejected": "rejected-auth",
            "receiver-restart": "uncertain-transport",
            "malformed-ipp": "uncertain-protocol",
            "partial-upload": "uncertain-transport",
        }

        for scenario, classification in expected.items():
            with self.subTest(scenario=scenario):
                result = ipp_fault_lab.run_scenario(scenario, timeout_seconds=1)

                self.assertTrue(result["simulationOnly"])
                self.assertEqual(result["evidenceClass"], "protocol-emulation")
                self.assertEqual(result["classification"], classification)
                self.assertEqual(result["counts"]["printJobRequests"], 1)
                self.assertTrue(result["recovery"]["printerQueryAccepted"])
                self.assertNotIn("hostname", json.dumps(result).lower())
                self.assertLessEqual(len(result["trace"]), ipp_fault_lab.MAX_TRACE_EVENTS)

    def test_duplicate_submission_is_reported_as_two_receiver_acceptances(self):
        result = ipp_fault_lab.run_scenario("duplicate-submit", timeout_seconds=5)
        self.assertTrue(result["simulationOnly"])
        self.assertEqual(result["classification"], "duplicate-accepted")
        self.assertEqual(result["counts"]["printJobRequests"], 2)

    def test_request_size_and_timeout_limits_are_enforced(self):
        oversized_document = b"x" * (ipp_fault_lab.MAX_DOCUMENT_BYTES + 1)
        with self.assertRaises(ValueError):
            ipp_fault_lab.validate_document_size(oversized_document)

        with self.assertRaises(ValueError):
            ipp_fault_lab.validate_timeout(ipp_fault_lab.MAX_TIMEOUT_SECONDS + 1)

        request = (
            b"POST /ipp/print HTTP/1.1\r\n"
            b"Content-Length: 8\r\n\r\n"
            b"\x02\x00\x00\x02\x00\x00\x00\x01"
        )

        class SlowDripSocket:
            def __init__(self):
                self.offset = 0
                self.timeouts = []

            def settimeout(self, seconds):
                self.timeouts.append(seconds)

            def recv(self, size):
                chunk = request[self.offset : self.offset + 1]
                self.offset += len(chunk)
                return chunk

        connection = SlowDripSocket()
        clock = iter((0.0, 1.0, ipp_fault_lab.MAX_TIMEOUT_SECONDS + 2.0))
        with mock.patch.object(ipp_fault_lab.time, "monotonic", side_effect=lambda: next(clock)):
            with self.assertRaises(TimeoutError):
                ipp_fault_lab._read_http_request(connection)

        self.assertEqual(connection.timeouts, [ipp_fault_lab.MAX_TIMEOUT_SECONDS + 1.0])


class IPPProxyUnitTests(unittest.TestCase):
    def test_auth_challenge_rejects_before_forwarding(self):
        trace = []
        with ipp_fault_lab.FaultProxy(9, "auth-rejected", 1, lambda phase, operation: trace.append((phase, operation))) as proxy:
            accepted = ipp_fault_lab._attempt_single_print_job(proxy.port, 9, b"%PDF-1.4\nsynthetic\n", 1)
            self.assertFalse(accepted)
            self.assertTrue(proxy.fault_applied)
            self.assertEqual(proxy.counts["printJobRequests"], 1)
            self.assertIn(("fault-applied", "auth-rejected"), trace)


if __name__ == "__main__":
    unittest.main()
