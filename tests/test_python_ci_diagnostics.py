"""Tests for bounded, sanitized Python CI failure diagnostics."""

from __future__ import annotations

import io
import os
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
        self.assertEqual(output, f"success-output{os.linesep}")

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
        test_temp = "/private/tmp/pb-ci-123-1"
        output = (
            r"D:\a\PaperBatch\PaperBatch\tests\test_sample.py "
            r"C:\Users\Jane Doe\AppData\Local\Temp\failure.log "
            r"D:\a\_temp\trace.txt " + test_temp + "/fixture.txt"
        )

        sanitized = run_python_unit_tests_ci.sanitize_output(
            output,
            workspace=r"D:\a\PaperBatch\PaperBatch",
            runner_temp=r"D:\a\_temp",
            test_temp=test_temp,
        )

        self.assertIn("[WORKSPACE]", sanitized)
        self.assertIn("[RUNNER_TEMP]", sanitized)
        self.assertIn(r"C:\Users\[USER]\AppData", sanitized)
        self.assertIn("[TEST_TEMP]/fixture.txt", sanitized)
        self.assertNotIn(test_temp, sanitized)
        self.assertNotIn("Jane Doe", sanitized)

    def test_bound_output_keeps_start_and_end_within_limit(self) -> None:
        output = "failure-start\n" + ("x" * 200) + "\nfailure-end"

        bounded = run_python_unit_tests_ci.bound_output(output, max_chars=80)

        self.assertLessEqual(len(bounded), 80)
        self.assertIn("failure-start", bounded)
        self.assertIn("failure-end", bounded)
        self.assertIn("middle omitted", bounded)

    def test_failure_emits_bounded_escaped_annotation_and_sanitized_summary(self) -> None:
        workspace = r"D:\a\PaperBatch\PaperBatch"
        runner_temp = r"D:\a\_temp"
        profile = r"C:\Users\Jane Doe"
        child_output = (
            "FAILED (errors=1)\nTraceback (most recent call last):\n"
            f"  File '{workspace}\\tests\\test_sample.py', line 8\n"
            f"  temp '{runner_temp}\\capture.txt' profile '{profile}\\profile.dat'\n"
            "::error title=forged::failure%detail\r\n"
            + ("é" * 6000)
            + "\nAssertionError: synthetic failing test\n"
        )
        with tempfile.TemporaryDirectory() as directory:
            summary_path = Path(directory) / "summary.md"
            stdout = io.StringIO()
            with (
                mock.patch.dict(
                    "os.environ",
                    {
                        "GITHUB_WORKSPACE": workspace,
                        "RUNNER_TEMP": runner_temp,
                        "GITHUB_STEP_SUMMARY": str(summary_path),
                    },
                    clear=True,
                ),
                mock.patch.object(run_python_unit_tests_ci.Path, "home", return_value=Path(profile)),
                mock.patch.object(run_python_unit_tests_ci, "run_command", return_value=(1, child_output)),
                mock.patch.object(run_python_unit_tests_ci.sys, "stdout", stdout),
            ):
                exit_code = run_python_unit_tests_ci.main()

            annotation = stdout.getvalue()
            summary = summary_path.read_text(encoding="utf-8")

        self.assertEqual(exit_code, 1)
        self.assertLessEqual(
            len(annotation.encode("utf-8")),
            run_python_unit_tests_ci.MAX_WORKFLOW_ANNOTATION_BYTES,
        )
        self.assertTrue(annotation.startswith("::error::"))
        self.assertEqual(annotation.count("\n"), 1)
        self.assertIn("FAILED (errors=1)%0ATraceback", annotation)
        self.assertIn("AssertionError: synthetic failing test", annotation)
        self.assertIn("[... middle omitted ...]", annotation)
        self.assertIn("%25detail%0D%0A", annotation)
        self.assertIn("::error title=forged::failure%25detail%0D%0A", annotation)
        for raw_path in (workspace, runner_temp, profile, "Jane Doe"):
            self.assertNotIn(raw_path, annotation)
            self.assertNotIn(raw_path, summary)
        self.assertIn("[WORKSPACE]", annotation)
        self.assertIn("[RUNNER_TEMP]", annotation)
        self.assertIn("[USER_HOME]", annotation)
        self.assertIn("AssertionError: synthetic failing test", summary)


if __name__ == "__main__":
    unittest.main()
