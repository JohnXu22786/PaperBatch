import json
import hashlib
import sys
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "tools"))
import build_u9_final_evidence_report as report_builder


ROOT = Path(__file__).resolve().parents[1]


def canonical_text_sha256(contents: str) -> str:
    """Hash tracked text consistently when Git checks it out with CRLF."""
    return hashlib.sha256(contents.replace("\r\n", "\n").encode("utf-8")).hexdigest()


class FinalEvidenceReportTests(unittest.TestCase):
    def setUp(self):
        self.evidence = json.loads(
            (ROOT / "docs/packaging/u9-final-evidence.json").read_text(encoding="utf-8")
        )

    def test_static_report_pins_each_unit_to_successful_exact_commit_runs(self):
        self.assertEqual(self.evidence["sourceMainCommit"], self.evidence["units"][-1]["merge"])
        self.assertEqual(len(self.evidence["units"]), 7)
        for unit in self.evidence["units"]:
            with self.subTest(unit=unit["unit"]):
                self.assertEqual(unit["ci"], "success")
                self.assertEqual(unit["simulation"], "success")
                self.assertEqual(len(unit["merge"]), 40)
                self.assertTrue(all(unit["artifacts"]))
                self.assertTrue(unit["artifactMetadata"]["CI"])
                self.assertTrue(unit["artifactMetadata"]["U9 simulation matrix"])
                self.assertTrue(
                    all(not artifact["expired"] for artifacts in unit["artifactMetadata"].values() for artifact in artifacts)
                )

    def test_license_inventory_hashes_match_tracked_lockfiles(self):
        for item in self.evidence["licenseInventory"]["lockedGraphs"]:
            with self.subTest(path=item["path"]):
                actual = canonical_text_sha256((ROOT / item["path"]).read_text(encoding="utf-8"))
                self.assertEqual(item["sha256"], actual)

    def test_lockfile_digest_is_stable_across_windows_line_endings(self):
        lf = "{\"packages\": [\"one\", \"two\"]}\n"
        crlf = lf.replace("\n", "\r\n")
        self.assertEqual(canonical_text_sha256(lf), canonical_text_sha256(crlf))

    def test_report_preserves_partial_and_excluded_scenarios(self):
        domains = {item["id"]: item for item in self.evidence["domains"]}
        self.assertEqual(domains["Q2"]["status"], "partial")
        self.assertEqual(domains["Q3"]["status"], "partial")
        self.assertEqual(domains["Q5"]["status"], "partial")
        self.assertEqual(domains["office_wps_native"]["status"], "blocked")
        self.assertEqual(domains["physical_output"]["status"], "not_run")
        self.assertEqual(domains["U10"]["status"], "not_run")
        self.assertEqual(
            self.evidence["decision"]["result"], "NO_GO_FOR_END_USER_BETA_OR_PUBLIC_RELEASE"
        )

    def test_current_report_is_bound_to_exact_candidate_and_all_ci_jobs(self):
        report = report_builder.build_report(
            self.evidence,
            candidate_sha="a" * 40,
            run_id="12345",
            run_attempt="1",
            workflow="CI",
            job_results={name: "success" for name in report_builder.JOB_NAMES},
        )
        current = report["currentRun"]
        self.assertEqual(current["candidateCommit"], "a" * 40)
        self.assertEqual(current["runId"], 12345)
        self.assertEqual(current["exactHeadCiOutcome"], "passed")

    def test_failed_exact_head_job_never_reports_green(self):
        jobs = {name: "success" for name in report_builder.JOB_NAMES}
        jobs["ipp-protocol-fault-lab"] = "failure"
        report = report_builder.build_report(
            self.evidence,
            candidate_sha="b" * 40,
            run_id="12346",
            run_attempt="2",
            workflow="CI",
            job_results=jobs,
        )
        self.assertEqual(report["currentRun"]["exactHeadCiOutcome"], "failed")

    def test_local_report_without_ci_context_does_not_invent_a_candidate(self):
        report = report_builder.build_report(
            self.evidence,
            candidate_sha=None,
            run_id=None,
            run_attempt=None,
            workflow=None,
            job_results={},
        )
        self.assertIsNone(report["currentRun"])

    def test_ci_context_without_candidate_sha_is_rejected(self):
        with self.assertRaisesRegex(ValueError, "exact candidate commit"):
            report_builder.build_report(
                self.evidence,
                candidate_sha=None,
                run_id="12347",
                run_attempt="1",
                workflow="CI",
                job_results={},
            )

    def test_workflows_checkout_pr_head_sha_used_by_evidence_report(self):
        expected = "ref: ${{ github.event.pull_request.head.sha || github.sha }}"
        for workflow in ("ci.yml", "u9-simulation-matrix.yml"):
            with self.subTest(workflow=workflow):
                lines = (ROOT / ".github/workflows" / workflow).read_text(encoding="utf-8").splitlines()
                checkouts = [index for index, line in enumerate(lines) if "uses: actions/checkout@v7" in line]
                self.assertTrue(checkouts)
                for index in checkouts:
                    self.assertTrue(any(expected in line for line in lines[index + 2 : index + 4]))


if __name__ == "__main__":
    unittest.main()
