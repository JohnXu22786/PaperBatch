"""Tests for bounded, sanitized Windows Python CI failure diagnostics."""

from __future__ import annotations

import sys
import unittest
from pathlib import Path

TOOLS_DIR = Path(__file__).resolve().parents[1] / "tools"
sys.path.insert(0, str(TOOLS_DIR))
import run_python_unit_tests_ci


class PythonCiDiagnosticsTests(unittest.TestCase):
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
