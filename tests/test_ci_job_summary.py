from __future__ import annotations

import sys
import tempfile
import unittest
from pathlib import Path
from unittest import mock

TOOLS_DIR = Path(__file__).resolve().parents[1] / "tools"
sys.path.insert(0, str(TOOLS_DIR))
import build_ci_job_summary as summary_builder


class CiJobSummaryTests(unittest.TestCase):
    def setUp(self) -> None:
        self.context = {
            "source_commit": "a" * 40,
            "workflow": "CI",
            "run_id": "12345",
            "run_attempt": "2",
            "job_result": "success",
            "job_name": "build-and-test (ubuntu-latest)",
            "runner_os": "Linux",
            "runner_name": "GitHub Actions 42",
            "runner_image": "ubuntu24",
            "runner_image_version": "20261020.1",
            "python_version": "3.12.7",
            "dotnet_version": "10.0.100",
        }

    def test_summary_binds_source_run_attempt_job_and_provenance(self) -> None:
        report = {
            "commit": "a" * 40,
            "runIdentity": {"workflow": "CI", "runId": "12345", "runAttempt": "2"},
            "scenarios": [{"scenarioId": "synthetic.one", "outcome": "passed", "evidenceLimit": "Loopback only."}],
        }
        result = summary_builder.render_summary(
            self.context,
            title="Managed test and runner evidence",
            steps={"restore": "success", "managed_tests": "success"},
            report=report,
        )

        for expected in ("a" * 40, "12345", "attempt 2", "success", "ubuntu24", "10.0.100", "synthetic.one", "Loopback only."):
            self.assertIn(expected, result)

    def test_mismatched_report_identity_is_rejected(self) -> None:
        report = {
            "commit": "b" * 40,
            "runIdentity": {"workflow": "CI", "runId": "12345", "runAttempt": "2"},
            "scenarios": [],
        }
        with self.assertRaisesRegex(ValueError, "does not match summary run"):
            summary_builder.render_summary(self.context, title="Evidence", report=report)

    def test_failed_job_and_missing_report_are_never_reported_as_passed(self) -> None:
        context = {**self.context, "job_result": "failure"}
        result = summary_builder.render_summary(
            context, title="Package evidence", report=None, report_required=True
        )

        self.assertIn("Job conclusion: `failure`", result)
        self.assertIn("machine report: unavailable", result.lower())
        self.assertIn("not a pass", result.lower())
        self.assertNotIn("Outcome: `passed`", result)

    def test_summary_is_byte_bounded_and_carries_omission_notice(self) -> None:
        result = summary_builder.render_summary(
            self.context,
            title="CI evidence",
            details=["detail " + str(index) for index in range(1000)],
            evidence_limits=["Synthetic tests do not prove paper output."],
            max_bytes=1500,
        )

        self.assertLessEqual(len(result.encode("utf-8")), 1500)
        self.assertIn("omitted", result.lower())
        self.assertIn("12345", result)
        self.assertIn("paper output", result)

    def test_report_file_over_size_limit_is_rejected(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "report.json"
            path.write_text("{" + (" " * (summary_builder.MAX_REPORT_BYTES + 1)) + "}", encoding="utf-8")
            with self.assertRaisesRegex(ValueError, "exceeds the .* byte limit"):
                summary_builder.read_report(path)

    def test_required_missing_report_makes_summary_step_fail(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            summary_path = Path(directory) / "summary.md"
            missing_report = Path(directory) / "missing.json"
            args = [
                "build_ci_job_summary.py", "--title", "Required evidence", "--report", str(missing_report),
                "--report-required", "--summary-path", str(summary_path),
            ]
            env = {
                "PB_SOURCE_COMMIT": "a" * 40,
                "GITHUB_WORKFLOW": "CI",
                "GITHUB_RUN_ID": "12345",
                "GITHUB_RUN_ATTEMPT": "1",
                "PB_JOB_RESULT": "success",
                "PB_JOB_NAME": "report job",
            }
            with mock.patch.object(summary_builder.sys, "argv", args), mock.patch.dict("os.environ", env, clear=True), mock.patch.object(summary_builder, "_version", return_value="test"):
                exit_code = summary_builder.main()

            self.assertEqual(exit_code, 1)
            self.assertIn("not a pass", summary_path.read_text(encoding="utf-8"))


if __name__ == "__main__":
    unittest.main()
