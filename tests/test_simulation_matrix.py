import json
import shutil
import sys
import unittest
from pathlib import Path
from unittest import mock

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
        self.assertEqual(lanes["macos"]["additionalScenarios"][0]["client"], "macos_system_ipptool")

    def test_macos_cups_lane_is_explicitly_unsupported_without_system_ipptool(self):
        with mock.patch.object(simulation_matrix.shutil, "which", return_value=None):
            scenario, version = simulation_matrix.run_macos_cups_smoke()

        self.assertEqual(scenario["scenarioId"], "ipp.cups_ipptool_loopback.macos_runner.system_cups_client.protocol")
        self.assertEqual(scenario["outcome"], "unsupported")
        self.assertEqual(scenario["client"], "macos_system_ipptool")
        self.assertEqual(version, "unavailable")
        self.assertIn("ipptool is unavailable", scenario["evidenceLimit"])

    def test_aggregate_keeps_macos_cups_scenario_separate_from_fake_http_smoke(self):
        config = simulation_matrix._manifest()["lanes"]["macos"]["additionalScenarios"][0]
        report = simulation_matrix.build_report(
            [
                simulation_matrix.run_smoke("macos"),
                {**config, "outcome": "unsupported", "simulationOnly": True, "evidenceLimit": "ipptool unavailable on this runner."},
            ],
            commit="a" * 40,
            os_name="macOS",
            runner="macos-hosted",
            runner_image="macos-14",
            runner_image_version="2026.10",
            python_version="3.12.0",
            additional_tool_versions={"ipptool": "unavailable"},
        )
        aggregate = simulation_matrix.aggregate_scenarios(
            {"macos": json.dumps(report)}, {"macos": "success"}, commit="a" * 40
        )
        cups = next(item for item in aggregate if item["scenarioId"] == config["scenarioId"])

        self.assertEqual(cups["outcome"], "unsupported")
        self.assertEqual(cups["os"], "macOS")
        self.assertEqual(cups["runnerImage"], "macos-14")
        self.assertEqual(cups["endpoint"], "fake_loopback_endpoint")
        self.assertEqual(cups["client"], "macos_system_ipptool")
        self.assertEqual(cups["toolVersions"]["ipptool"], "unavailable")

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
            "toolVersions": {"python": "3.12.0", "ipptool": "2.4.10"},
            "scenarios": [{
                "scenarioId": manifest["lanes"]["linux"]["scenarioId"],
                "outcome": "passed",
                "endpoint": "fake_loopback_endpoint",
                "client": "python_stdlib_http_client_on_linux_runner",
                "evidenceLimit": "Synthetic loopback only.",
            }],
        }
        scenarios = simulation_matrix.aggregate_scenarios(
            {"linux": json.dumps(linux_report), "windows": "not-json"},
            {"linux": "success", "windows": "failure", "macos": "skipped"},
            commit="a" * 40,
        )
        self.assertEqual([item["outcome"] for item in scenarios], ["passed", "not_run", "unsupported", "unsupported", "blocked"])
        self.assertIn("availability/quota", scenarios[1]["reason"])
        self.assertEqual(scenarios[2]["reason"].split("job result: ")[1].split(")")[0], "skipped")
        self.assertEqual(scenarios[0]["runnerProvenance"]["runnerImage"], "ubuntu-24.04")
        self.assertEqual(scenarios[0]["os"], "Linux")
        self.assertEqual(scenarios[0]["runnerImage"], "ubuntu-24.04")
        self.assertEqual(scenarios[0]["endpoint"], "fake_loopback_endpoint")
        self.assertEqual(scenarios[0]["client"], "python_stdlib_http_client_on_linux_runner")
        self.assertEqual(list(scenarios[0]["toolVersions"]), ["ipptool", "python"])
        self.assertEqual(scenarios[0]["toolVersions"], {"ipptool": "2.4.10", "python": "3.12.0"})
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

    def test_get_printer_attributes_accepts_supported_ipp_versions_and_request_ids(self):
        for version in (b"\x01\x01", b"\x02\x00", b"\x02\x01", b"\x02\x02"):
            with self.subTest(version=version):
                request = version + b"\x00\x0b\x00\x00\x00\x2a\x01" + simulation_matrix.REQUEST[9:]
                response = request[:2] + b"\x00\x00" + request[4:8] + simulation_matrix.RESPONSE[8:]
                self.assertTrue(simulation_matrix._valid_get_printer_attributes_request(request))
                self.assertTrue(simulation_matrix._valid_get_printer_attributes_response(response, request))

    def test_smoke_exchange_reports_endpoint_and_client_without_physical_claim(self):
        scenario = simulation_matrix.run_smoke("linux")
        self.assertTrue(scenario["scenarioId"].startswith("ipp.fake_loopback_endpoint."))
        self.assertIn("linux_runner_python_http_client", scenario["scenarioId"])
        self.assertEqual(scenario["client"], "python_stdlib_http_client_on_linux_runner")
        self.assertEqual(scenario["endpoint"], "fake_loopback_endpoint")
        self.assertEqual(scenario["outcome"], "passed")
        self.assertIn("paper output", scenario["evidenceLimit"].lower())
        self.assertTrue(scenario["simulationOnly"])

    def test_native_runner_stub_is_blocked(self):
        scenario = simulation_matrix.native_runner_stub()
        self.assertEqual(scenario["outcome"], "blocked")
        self.assertIn("snapshot", scenario["evidenceLimit"].lower())
        self.assertEqual(scenario["requiredSnapshotProfiles"], ["office_only", "wps_only", "coexistence"])
        self.assertTrue(scenario["compatibilityClaimsOpen"])


if __name__ == "__main__":
    unittest.main()
