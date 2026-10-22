import json
import sys
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "tools"))
import simulation_matrix


class SimulationMatrixTests(unittest.TestCase):
    def test_report_requires_provenance_and_known_outcome(self):
        base = {
            "scenarioId": "ipp.fake_loopback_endpoint.linux_runner_python_http_client.smoke",
            "outcome": "passed",
            "evidenceLimit": "Synthetic loopback exchange only; no printer or product support claim.",
        }
        report = simulation_matrix.build_report(
            [base], commit="a" * 40, os_name="Linux", runner="runner", runner_image="ubuntu-24.04", runner_image_version="2026.10", python_version="3.12.0"
        )
        self.assertEqual(report["commit"], "a" * 40)
        self.assertEqual(report["scenarios"][0]["outcome"], "passed")
        for change in ({"outcome": "success"}, {"scenarioId": ""}, {"evidenceLimit": ""}):
            with self.subTest(change=change):
                with self.assertRaises(ValueError):
                    simulation_matrix.build_report(
                        [{**base, **change}], commit="a" * 40, os_name="Linux", runner="runner", runner_image="image", runner_image_version="version", python_version="3.12"
                    )

    def test_manifest_distinguishes_each_runner_client_contract(self):
        manifest = simulation_matrix._manifest()
        lanes = manifest["lanes"]
        self.assertEqual(set(lanes), {"linux", "windows", "macos"})
        self.assertEqual(len({lane["scenarioId"] for lane in lanes.values()}), 3)
        self.assertIn("macOS support", lanes["macos"]["evidenceLimit"])

    def test_ipp_smoke_requires_a_complete_get_printer_attributes_request(self):
        self.assertTrue(simulation_matrix._valid_get_printer_attributes_request(simulation_matrix.REQUEST))
        self.assertFalse(simulation_matrix._valid_get_printer_attributes_request(simulation_matrix.REQUEST[:-1]))
        without_uri = simulation_matrix.REQUEST.replace(
            simulation_matrix._ipp_attribute(0x45, "printer-uri", b"ipp://127.0.0.1/printers/synthetic"), b""
        )
        self.assertFalse(simulation_matrix._valid_get_printer_attributes_request(without_uri))

    def test_aggregate_reports_unavailable_lanes_without_claiming_pass(self):
        manifest = simulation_matrix._manifest()
        linux_report = {
            "commit": "a" * 40,
            "os": "Linux",
            "runner": "linux-hosted",
            "runnerImage": "ubuntu-24.04",
            "runnerImageVersion": "2026.10",
            "toolVersions": {"python": "3.12.0"},
            "scenarios": [{
                "scenarioId": manifest["lanes"]["linux"]["scenarioId"],
                "outcome": "passed",
                "evidenceLimit": "Synthetic loopback only.",
            }],
        }
        scenarios = simulation_matrix.aggregate_scenarios(
            {"linux": json.dumps(linux_report), "windows": "not-json"},
            {"linux": "success", "windows": "failure", "macos": "skipped"},
            commit="a" * 40,
        )
        self.assertEqual([item["outcome"] for item in scenarios], ["passed", "not_run", "unsupported", "blocked"])
        self.assertIn("availability/quota", scenarios[1]["reason"])
        self.assertEqual(scenarios[2]["reason"].split("job result: ")[1].split(")")[0], "skipped")
        self.assertEqual(scenarios[0]["runnerProvenance"]["runnerImage"], "ubuntu-24.04")
        failed_lane = simulation_matrix.aggregate_scenarios(
            {"linux": json.dumps(linux_report)},
            {"linux": "failure"},
            commit="a" * 40,
        )[0]
        self.assertEqual(failed_lane["scenarioOutcome"], "passed")
        self.assertEqual(failed_lane["outcome"], "failed")
        self.assertEqual(failed_lane["jobResult"], "failure")
        self.assertEqual(simulation_matrix._aggregate_exit_code([failed_lane]), 1)
        self.assertEqual(scenarios[1]["jobResult"], "invalid_report")
        self.assertEqual(simulation_matrix._aggregate_exit_code([scenarios[1], scenarios[2]]), 1)
        self.assertEqual(simulation_matrix._aggregate_exit_code([scenarios[0], scenarios[3]]), 0)
        incomplete_report = {**linux_report, "runner": ""}
        invalid_lane = simulation_matrix.aggregate_scenarios(
            {"linux": json.dumps(incomplete_report)}, {"linux": "success"}, commit="a" * 40
        )[0]
        self.assertEqual(invalid_lane["outcome"], "not_run")
        self.assertEqual(invalid_lane["jobResult"], "invalid_report")
        self.assertEqual(simulation_matrix._aggregate_exit_code([invalid_lane]), 1)

    def test_ipp_smoke_rejects_incomplete_or_unsuccessful_responses(self):
        self.assertTrue(simulation_matrix._valid_get_printer_attributes_response(simulation_matrix.RESPONSE))
        self.assertFalse(simulation_matrix._valid_get_printer_attributes_response(simulation_matrix.RESPONSE[:-1]))
        unsuccessful = simulation_matrix.RESPONSE[:2] + b"\x00\x05" + simulation_matrix.RESPONSE[4:]
        self.assertFalse(simulation_matrix._valid_get_printer_attributes_response(unsuccessful))

    def test_smoke_exchange_reports_endpoint_and_client_without_physical_claim(self):
        scenario = simulation_matrix.run_smoke("linux")
        self.assertTrue(scenario["scenarioId"].startswith("ipp.fake_loopback_endpoint."))
        self.assertIn("linux_runner_python_http_client", scenario["scenarioId"])
        self.assertEqual(scenario["client"], "python_stdlib_http_client_on_linux_runner")
        self.assertEqual(scenario["outcome"], "passed")
        self.assertIn("paper output", scenario["evidenceLimit"].lower())
        self.assertTrue(scenario["simulationOnly"])

    def test_native_runner_stub_is_blocked(self):
        scenario = simulation_matrix.native_runner_stub()
        self.assertEqual(scenario["outcome"], "blocked")
        self.assertIn("provisioned", scenario["evidenceLimit"].lower())


if __name__ == "__main__":
    unittest.main()
