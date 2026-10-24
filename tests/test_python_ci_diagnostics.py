"""Tests for bounded, sanitized Windows Python CI failure diagnostics."""

from __future__ import annotations

import sys
import tempfile
import unittest
from pathlib import Path
from unittest import mock

TOOLS_DIR = Path(__file__).resolve().parents[1] / "tools"
sys.path.insert(0, str(TOOLS_DIR))
import run_python_unit_tests_ci


class PythonCiDiagnosticsTests(unittest.TestCase):
    def test_run_command_bounds_oversized_output_without_temp_artifacts(self) -> None:
        script = (
            "import sys; "
            "sys.stdout.write('failure-start\\n'); sys.stdout.flush(); "
            "sys.stdout.write('x' * 200000); sys.stdout.flush(); "
            "sys.stderr.write('\\nfailure-end'); sys.stderr.flush(); "
            "sys.exit(23)"
        )
        with tempfile.TemporaryDirectory() as directory:
            with mock.patch.object(tempfile, "tempdir", directory):
                exit_code, output = run_python_unit_tests_ci.run_command(
                    [sys.executable, "-c", script],
                    cwd=directory,
                    max_capture_bytes=512,
                )

            self.assertEqual(exit_code, 23)
            self.assertLessEqual(len(output.encode("utf-8")), 512)
            self.assertIn("failure-start", output)
            self.assertIn("failure-end", output)
            self.assertIn("middle omitted", output)
            self.assertEqual(list(Path(directory).iterdir()), [])

    def test_run_command_preserves_success_exit_and_output(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            exit_code, output = run_python_unit_tests_ci.run_command(
                [sys.executable, "-c", "print('success-output')"],
                cwd=directory,
            )

        self.assertEqual(exit_code, 0)
        self.assertEqual(output, "success-output\n")

    def test_run_command_bounds_decoded_invalid_utf8_output(self) -> None:
        script = (
            "import os; "
            "os.write(1, b'failure-start\\n' + b'\\xff' * 2000 + b'\\nfailure-end')"
        )
        with tempfile.TemporaryDirectory() as directory:
            exit_code, output = run_python_unit_tests_ci.run_command(
                [sys.executable, "-c", script],
                cwd=directory,
                max_capture_bytes=512,
            )

        self.assertEqual(exit_code, 0)
        self.assertLessEqual(len(output.encode("utf-8")), 512)
        self.assertIn("failure-start", output)
        self.assertIn("failure-end", output)
        self.assertIn("middle omitted", output)
        self.assertIn("\ufffd", output)

    def test_sanitized_multibyte_output_stays_within_byte_limit(self) -> None:
        output = r"D:\a" + ("é" * 6000) + "failure-end"
        sanitized = run_python_unit_tests_ci.sanitize_output(output, workspace=r"D:\a")

        self.assertLess(len(sanitized), run_python_unit_tests_ci.MAX_DIAGNOSTIC_CHARS)
        self.assertGreater(len(sanitized.encode("utf-8")), run_python_unit_tests_ci.MAX_CAPTURE_BYTES)

        bounded = run_python_unit_tests_ci.bound_utf8_output(sanitized)

        self.assertLessEqual(len(bounded.encode("utf-8")), run_python_unit_tests_ci.MAX_CAPTURE_BYTES)
        self.assertIn("[WORKSPACE]", bounded)
        self.assertIn("failure-end", bounded)
        self.assertIn("middle omitted", bounded)

    def test_sanitize_output_redacts_runner_and_profile_paths(self) -> None:
        output = (
            r"D:\a\PaperBatch\PaperBatch\tests\test_sample.py "
            r"C:\Users\Jane Doe\AppData\Local\Temp\failure.log "
            r"D:\a\_temp\trace.txt"
        )

        sanitized = run_python_unit_tests_ci.sanitize_output(
            output,
            workspace=r"D:\a\PaperBatch\PaperBatch",
            runner_temp=r"D:\a\_temp",
        )

        self.assertIn("[WORKSPACE]", sanitized)
        self.assertIn("[RUNNER_TEMP]", sanitized)
        self.assertIn(r"C:\Users\[USER]\AppData", sanitized)
        self.assertNotIn("Jane Doe", sanitized)

    def test_bound_output_keeps_start_and_end_within_limit(self) -> None:
        output = "failure-start\n" + ("x" * 200) + "\nfailure-end"

        bounded = run_python_unit_tests_ci.bound_output(output, max_chars=80)

        self.assertLessEqual(len(bounded), 80)
        self.assertIn("failure-start", bounded)
        self.assertIn("failure-end", bounded)
        self.assertIn("middle omitted", bounded)


if __name__ == "__main__":
    unittest.main()
