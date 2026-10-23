import shutil
import sys
import threading
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "tools"))
import office_wps_harness as lab


class StubAdapter:
    def __init__(self, *, lose_ownership=False, timeout_on=None, cancel_on=None, unsupported_on=None, failure_on=None):
        self.calls = []
        self.lose_ownership = lose_ownership
        self.timeout_on = timeout_on
        self.cancel_on = cancel_on
        self.unsupported_on = unsupported_on
        self.failure_on = failure_on
        self.next_pid = 40
        self.live = set()
        self.identities = {}

    def abort_run(self, timeout_seconds):
        self.calls.append(("abort_run", timeout_seconds))

    def reset_profile(self, profile_dir):
        self.calls.append(("reset", Path(profile_dir), Path(profile_dir).is_dir()))

    def wait_for_operator(self, step, timeout_seconds, cancel_event):
        self.calls.append(("wait", step, timeout_seconds))
        if step == self.unsupported_on:
            raise lab.Unsupported
        if step == self.timeout_on:
            raise TimeoutError
        if step == self.cancel_on:
            cancel_event.set()
        if cancel_event.is_set():
            raise lab.Cancelled

    def launch(self, product, profile_dir, timeout_seconds, cancel_event):
        self.calls.append(("launch", product, Path(profile_dir), timeout_seconds))
        self.next_pid += 1
        self.live.add(self.next_pid)
        identity = f"stub-process-{self.next_pid}"
        self.identities[self.next_pid] = identity
        return lab.ProcessHandle(self.next_pid, identity)

    def owns_process(self, process):
        return process.pid in self.live and self.identities.get(process.pid) == process.identity and not self.lose_ownership

    def open_fixture(self, process, fixture_id, timeout_seconds, cancel_event):
        self.calls.append(("open", process.pid, fixture_id, timeout_seconds))
        if self.failure_on == "open":
            raise lab.HarnessFailure("C:\\Users\\Example\\private.docx")

    def export_pdf(self, process, fixture_id, output_dir, timeout_seconds, cancel_event):
        self.calls.append(("export", process.pid, fixture_id, Path(output_dir), timeout_seconds))
        path = Path(output_dir) / "export.pdf"
        path.write_bytes(b"%PDF-1.4 synthetic harness output")
        return path

    def close(self, process, timeout_seconds):
        self.calls.append(("close", process.pid, timeout_seconds))
        if self.identities.get(process.pid) == process.identity:
            self.live.discard(process.pid)

    def cancel(self, process):
        self.calls.append(("cancel", process.pid))
        if self.identities.get(process.pid) == process.identity:
            self.live.discard(process.pid)

    def is_running(self, process):
        return process.pid in self.live and self.identities.get(process.pid) == process.identity


def contract_inputs():
    expectation = lab.RunnerExpectation(
        repository="owner/repo",
        workflow_ref="owner/repo/.github/workflows/u9-office-wps-trusted.yml@refs/heads/main",
        default_ref="refs/heads/main",
        main_sha="a" * 40,
        snapshot_profile="office_only",
        snapshot_id="u9-win11-office-2026-10",
        image_digest="sha256:" + "b" * 64,
        product_builds={"office": "office-16.0.19029-x64"},
        network_allowlist=(),
    )
    context = lab.RunnerContext(
        event_name="workflow_dispatch",
        repository="owner/repo",
        ref="refs/heads/main",
        ref_protected=True,
        checkout_sha="a" * 40,
        main_sha="a" * 40,
        is_fork=False,
        workflow_ref=expectation.workflow_ref,
    )
    snapshot = lab.RunnerSnapshot(
        snapshot_profile=expectation.snapshot_profile,
        snapshot_id=expectation.snapshot_id,
        image_digest=expectation.image_digest,
        ephemeral=True,
        licensed=True,
        software_builds={"office": "office-16.0.19029-x64"},
        profile_isolated=True,
        profile_reset=True,
        repository_secrets=(),
        network_allowlist=expectation.network_allowlist,
    )
    return expectation, context, snapshot


class OfficeWpsHarnessTests(unittest.TestCase):
    def test_trusted_push_to_exact_protected_main_passes_runner_gate(self):
        expectation, context, snapshot = contract_inputs()
        context = lab.RunnerContext(**{**context.__dict__, "event_name": "push"})
        self.assertEqual(lab.authorize_native_lane(context, expectation, snapshot), lab.GateResult(True, "trusted_snapshot_verified"))

    def test_untrusted_events_forks_and_wrong_checkout_fail_before_adapter_creation(self):
        expectation, context, snapshot = contract_inputs()
        for changes in (
            {"event_name": "pull_request"},
            {"event_name": "pull_request_target"},
            {"event_name": "push", "is_fork": True},
            {"ref": "refs/heads/feature"},
            {"ref_protected": False},
            {"ref_protected": "false"},
            {"is_fork": None},
            {"checkout_sha": "c" * 40},
            {"workflow_ref": "owner/repo/.github/workflows/u9-office-wps-trusted.yml@refs/pull/1/merge"},
        ):
            with self.subTest(changes=changes):
                created = []
                result = lab.run_interactive_harness(
                    context=lab.RunnerContext(**{**context.__dict__, **changes}),
                    expectation=expectation,
                    snapshot=snapshot,
                    adapter_factory=lambda: created.append(StubAdapter()),
                    fixture_id="u9.office_wps.synthetic_docx_smoke",
                )
                self.assertEqual(result["outcome"], "blocked")
                self.assertFalse(result["compatibilityClaim"])
                self.assertEqual(created, [])

    def test_snapshot_build_license_secret_and_network_mismatches_fail_closed(self):
        expectation, context, snapshot = contract_inputs()
        for changes in (
            {"snapshot_profile": "coexistence"},
            {"snapshot_id": "wrong-snapshot"},
            {"image_digest": "sha256:" + "c" * 64},
            {"software_builds": {"office": "other-build"}},
            {"licensed": False},
            {"licensed": "false"},
            {"ephemeral": False},
            {"profile_isolated": False},
            {"profile_reset": False},
            {"repository_secrets": None},
            {"repository_secrets": []},
            {"repository_secrets": ("SOME_SECRET",)},
            {"network_allowlist": ("*",)},
        ):
            with self.subTest(changes=changes):
                created = []
                result = lab.run_interactive_harness(
                    context=context,
                    expectation=expectation,
                    snapshot=lab.RunnerSnapshot(**{**snapshot.__dict__, **changes}),
                    adapter_factory=lambda: created.append(StubAdapter()),
                    fixture_id="u9.office_wps.synthetic_docx_smoke",
                )
                self.assertEqual(result["outcome"], "blocked")
                self.assertEqual(created, [])

    def test_broad_network_policy_is_rejected_even_when_snapshot_matches(self):
        expectation, context, snapshot = contract_inputs()
        for rule in ("0.0.0.0/0", "*", "license.example.test"):
            with self.subTest(rule=rule):
                broad = (rule,)
                broad_expectation = lab.RunnerExpectation(**{**expectation.__dict__, "network_allowlist": broad})
                broad_snapshot = lab.RunnerSnapshot(**{**snapshot.__dict__, "network_allowlist": broad})
                created = []
                result = lab.run_interactive_harness(
                    context=context,
                    expectation=broad_expectation,
                    snapshot=broad_snapshot,
                    adapter_factory=lambda: created.append(StubAdapter()),
                    fixture_id="u9.office_wps.synthetic_docx_smoke",
                )
                self.assertEqual(result["outcome"], "blocked")
                self.assertEqual(result["reasonCode"], "network_policy_mismatch")
                self.assertEqual(created, [])

    def test_product_must_exist_in_the_verified_snapshot_profile_before_launch(self):
        expectation, context, snapshot = contract_inputs()
        for profile, product in (("office_only", "wps"), ("wps_only", "office")):
            installed = "office" if profile == "office_only" else "wps"
            builds = {installed: f"{installed}-build"}
            wrong_expectation = lab.RunnerExpectation(**{
                **expectation.__dict__, "snapshot_profile": profile, "product_builds": builds,
            })
            wrong_snapshot = lab.RunnerSnapshot(**{
                **snapshot.__dict__, "snapshot_profile": profile, "software_builds": builds,
            })
            created = []
            result = lab.run_interactive_harness(
                context=context,
                expectation=wrong_expectation,
                snapshot=wrong_snapshot,
                adapter_factory=lambda: created.append(StubAdapter()),
                fixture_id="u9.office_wps.synthetic_docx_smoke",
                product=product,
            )
            self.assertEqual(result["outcome"], "blocked")
            self.assertEqual(result["reasonCode"], "wrong_software_build")
            self.assertEqual(created, [])

    def test_cancel_after_launch_return_still_cleans_the_launched_process(self):
        expectation, context, snapshot = contract_inputs()
        cancel = threading.Event()
        adapter = StubAdapter()
        original_check_owned = lab._check_owned
        check_count = 0

        def cancel_before_first_ownership_check(adapter_arg, process, timeout_seconds, cancel_event):
            nonlocal check_count
            check_count += 1
            if check_count == 1:
                cancel.set()
            return original_check_owned(adapter_arg, process, timeout_seconds, cancel_event)

        lab._check_owned = cancel_before_first_ownership_check
        try:
            result = lab.run_interactive_harness(
                context=context,
                expectation=expectation,
                snapshot=snapshot,
                adapter_factory=lambda: adapter,
                fixture_id="u9.office_wps.synthetic_docx_smoke",
                cancel_event=cancel,
            )
        finally:
            lab._check_owned = original_check_owned
        self.assertEqual(result["outcome"], "not_run")
        self.assertEqual(result["reasonCode"], "cancelled")
        self.assertFalse(adapter.live)
        self.assertEqual([call[0] for call in adapter.calls].count("cancel"), 1)

    def test_watchdog_aborts_an_adapter_that_blocks_past_its_deadline(self):
        expectation, context, snapshot = contract_inputs()

        class BlockingAdapter(StubAdapter):
            def __init__(self):
                super().__init__()
                self.release = threading.Event()

            def wait_for_operator(self, step, timeout_seconds, cancel_event):
                super().wait_for_operator(step, timeout_seconds, cancel_event)

            def open_fixture(self, pid, fixture_id, timeout_seconds, cancel_event):
                self.release.wait()

            def abort_run(self, timeout_seconds):
                super().abort_run(timeout_seconds)
                self.release.set()
                self.live.clear()

        adapter = BlockingAdapter()
        result = lab.run_interactive_harness(
            context=context,
            expectation=expectation,
            snapshot=snapshot,
            adapter_factory=lambda: adapter,
            fixture_id="u9.office_wps.synthetic_docx_smoke",
            timeout_seconds=1,
        )
        self.assertEqual(result["outcome"], "failed")
        self.assertEqual(result["reasonCode"], "step_timeout")
        self.assertIn("abort_run", [call[0] for call in adapter.calls])
        self.assertEqual(result["steps"], ["launch", "open"])

    def test_unconfirmed_abort_preserves_profile_until_adapter_is_quiescent(self):
        expectation, context, snapshot = contract_inputs()

        class UnstoppableAdapter(StubAdapter):
            def __init__(self):
                super().__init__()
                self.release_call = threading.Event()
                self.call_done = threading.Event()
                self.abort_done = threading.Event()

            def open_fixture(self, pid, fixture_id, timeout_seconds, cancel_event):
                try:
                    self.release_call.wait()
                finally:
                    self.call_done.set()

            def abort_run(self, timeout_seconds):
                self.release_call.set()
                self.abort_done.set()
                raise RuntimeError("abort failed")

        adapter = UnstoppableAdapter()
        result = lab.run_interactive_harness(
            context=context,
            expectation=expectation,
            snapshot=snapshot,
            adapter_factory=lambda: adapter,
            fixture_id="u9.office_wps.synthetic_docx_smoke",
            timeout_seconds=1,
        )
        profile_dir = next(call[1] for call in adapter.calls if call[0] == "reset")
        self.assertEqual(result["outcome"], "failed")
        self.assertEqual(result["reasonCode"], "adapter_error")
        self.assertTrue(profile_dir.is_dir())
        self.assertTrue(adapter.live)
        self.assertTrue(adapter.call_done.wait(2))
        self.assertTrue(adapter.abort_done.wait(2))
        adapter.live.clear()
        shutil.rmtree(profile_dir.parent)

    def test_failed_process_termination_preserves_profile(self):
        expectation, context, snapshot = contract_inputs()

        class UnclosableAdapter(StubAdapter):
            def cancel(self, pid):
                raise RuntimeError("cancel did not stop process")

            def close(self, pid, timeout_seconds):
                raise RuntimeError("process remains active")

        adapter = UnclosableAdapter(failure_on="open")
        result = lab.run_interactive_harness(
            context=context,
            expectation=expectation,
            snapshot=snapshot,
            adapter_factory=lambda: adapter,
            fixture_id="u9.office_wps.synthetic_docx_smoke",
        )
        profile_dir = next(call[1] for call in adapter.calls if call[0] == "reset")
        self.assertEqual(result["outcome"], "failed")
        self.assertTrue(adapter.live)
        self.assertEqual(len([call for call in adapter.calls if call[0] == "reset"]), 1)
        self.assertTrue(profile_dir.is_dir())
        adapter.live.clear()
        shutil.rmtree(profile_dir.parent)

    def test_ownership_is_rechecked_after_operator_wait_before_close(self):
        expectation, context, snapshot = contract_inputs()

        class ReusedPidAdapter(StubAdapter):
            def wait_for_operator(self, step, timeout_seconds, cancel_event):
                super().wait_for_operator(step, timeout_seconds, cancel_event)
                if step == "close":
                    self.lose_ownership = True

        adapter = ReusedPidAdapter()
        result = lab.run_interactive_harness(
            context=context,
            expectation=expectation,
            snapshot=snapshot,
            adapter_factory=lambda: adapter,
            fixture_id="u9.office_wps.synthetic_docx_smoke",
        )
        profile_dir = next(call[1] for call in adapter.calls if call[0] == "reset")
        self.assertEqual(result["reasonCode"], "process_ownership_lost")
        self.assertNotIn("close", [call[0] for call in adapter.calls])
        self.assertTrue(profile_dir.is_dir())
        adapter.live.clear()
        shutil.rmtree(profile_dir.parent)

    def test_failed_final_profile_reset_preserves_the_run_directory(self):
        expectation, context, snapshot = contract_inputs()

        class ResetFailureAdapter(StubAdapter):
            def reset_profile(self, profile_dir):
                super().reset_profile(profile_dir)
                if len([call for call in self.calls if call[0] == "reset"]) == 2:
                    raise RuntimeError("reset failed")

        adapter = ResetFailureAdapter()
        result = lab.run_interactive_harness(
            context=context,
            expectation=expectation,
            snapshot=snapshot,
            adapter_factory=lambda: adapter,
            fixture_id="u9.office_wps.synthetic_docx_smoke",
        )
        profile_dir = next(call[1] for call in adapter.calls if call[0] == "reset")
        self.assertEqual(result["outcome"], "failed")
        self.assertEqual(result["reasonCode"], "profile_reset_failed")
        self.assertTrue(profile_dir.is_dir())
        shutil.rmtree(profile_dir.parent)

    def test_script_runs_bounded_workflow_without_claiming_compatibility_from_stub(self):
        expectation, context, snapshot = contract_inputs()
        adapters = []

        def create_adapter():
            adapter = StubAdapter()
            adapters.append(adapter)
            return adapter

        result = lab.run_interactive_harness(
            context=context,
            expectation=expectation,
            snapshot=snapshot,
            adapter_factory=create_adapter,
            fixture_id="u9.office_wps.synthetic_docx_smoke",
            timeout_seconds=20,
        )

        self.assertEqual(result["outcome"], "passed")
        self.assertEqual(result["evidenceClass"], "simulation_only")
        self.assertTrue(result["simulationOnly"])
        self.assertFalse(result["compatibilityClaim"])
        self.assertEqual(result["steps"], ["launch", "open", "export", "close", "restart", "close"])
        self.assertLessEqual(len(str(result).encode()), lab.MAX_REPORT_BYTES)
        self.assertNotIn("export.pdf", str(result))
        reset_calls = [item for item in adapters[0].calls if item[0] == "reset"]
        self.assertEqual(len(reset_calls), 2)
        self.assertEqual([item[2] for item in reset_calls], [True, True])
        self.assertFalse(reset_calls[0][1].exists())
        self.assertEqual(
            [item[0] for item in adapters[0].calls if item[0] in {"launch", "open", "export", "close"}],
            ["launch", "open", "export", "close", "launch", "close"],
        )

    def test_lost_process_ownership_fails_without_touching_unowned_process_profile(self):
        expectation, context, snapshot = contract_inputs()
        adapter = StubAdapter(lose_ownership=True)
        result = lab.run_interactive_harness(
            context=context,
            expectation=expectation,
            snapshot=snapshot,
            adapter_factory=lambda: adapter,
            fixture_id="u9.office_wps.synthetic_docx_smoke",
        )
        self.assertEqual(result["outcome"], "failed")
        self.assertEqual(result["reasonCode"], "process_ownership_lost")
        reset_calls = [item for item in adapter.calls if item[0] == "reset"]
        self.assertEqual(len(reset_calls), 1)
        self.assertEqual([item[2] for item in reset_calls], [True])
        self.assertNotIn("cancel", [item[0] for item in adapter.calls])
        self.assertTrue(reset_calls[0][1].is_dir())
        adapter.live.clear()
        shutil.rmtree(reset_calls[0][1].parent)

    def test_cancel_is_not_reported_as_compatibility_failure_or_pass(self):
        expectation, context, snapshot = contract_inputs()
        cancel = threading.Event()
        cancel.set()
        adapter = StubAdapter()
        result = lab.run_interactive_harness(
            context=context,
            expectation=expectation,
            snapshot=snapshot,
            adapter_factory=lambda: adapter,
            fixture_id="u9.office_wps.synthetic_docx_smoke",
            cancel_event=cancel,
        )
        self.assertEqual(result["outcome"], "not_run")
        self.assertEqual(result["reasonCode"], "cancelled")
        self.assertFalse(result["compatibilityClaim"])
        self.assertEqual(adapter.calls, [])

    def test_in_run_cancel_stops_the_script_and_cleans_owned_process(self):
        expectation, context, snapshot = contract_inputs()
        cancel = threading.Event()
        adapter = StubAdapter(cancel_on="open")
        result = lab.run_interactive_harness(
            context=context,
            expectation=expectation,
            snapshot=snapshot,
            adapter_factory=lambda: adapter,
            fixture_id="u9.office_wps.synthetic_docx_smoke",
            cancel_event=cancel,
        )
        self.assertEqual(result["outcome"], "not_run")
        self.assertEqual(result["reasonCode"], "cancelled")
        self.assertEqual(result["steps"], ["launch"])
        self.assertIn("cancel", [item[0] for item in adapter.calls])

    def test_step_timeout_is_redacted_and_triggers_cleanup(self):
        expectation, context, snapshot = contract_inputs()
        adapter = StubAdapter(timeout_on="open")
        result = lab.run_interactive_harness(
            context=context,
            expectation=expectation,
            snapshot=snapshot,
            adapter_factory=lambda: adapter,
            fixture_id="u9.office_wps.synthetic_docx_smoke",
        )
        self.assertEqual(result["outcome"], "failed")
        self.assertEqual(result["reasonCode"], "step_timeout")
        self.assertEqual(result["steps"], ["launch"])
        self.assertNotIn("TimeoutError", str(result))
        self.assertIn("cancel", [item[0] for item in adapter.calls])

    def test_missing_interactive_desktop_is_unsupported(self):
        expectation, context, snapshot = contract_inputs()
        adapter = StubAdapter(unsupported_on="launch")
        result = lab.run_interactive_harness(
            context=context,
            expectation=expectation,
            snapshot=snapshot,
            adapter_factory=lambda: adapter,
            fixture_id="u9.office_wps.synthetic_docx_smoke",
        )
        self.assertEqual(result["outcome"], "unsupported")
        self.assertEqual(result["reasonCode"], "interactive_session_unavailable")

    def test_adapter_failure_codes_cannot_leak_user_paths(self):
        expectation, context, snapshot = contract_inputs()
        adapter = StubAdapter(failure_on="open")
        result = lab.run_interactive_harness(
            context=context,
            expectation=expectation,
            snapshot=snapshot,
            adapter_factory=lambda: adapter,
            fixture_id="u9.office_wps.synthetic_docx_smoke",
        )
        self.assertEqual(result["outcome"], "failed")
        self.assertEqual(result["reasonCode"], "adapter_error")
        self.assertNotIn("private.docx", str(result))
        self.assertLessEqual(len(str(result).encode()), lab.MAX_REPORT_BYTES)

    def test_timeout_and_unknown_fixture_are_bounded_failures(self):
        expectation, context, snapshot = contract_inputs()
        adapter = StubAdapter()
        timed_out = lab.run_interactive_harness(
            context=context,
            expectation=expectation,
            snapshot=snapshot,
            adapter_factory=lambda: adapter,
            fixture_id="u9.office_wps.synthetic_docx_smoke",
            timeout_seconds=lab.MAX_STEP_TIMEOUT_SECONDS + 1,
        )
        self.assertEqual(timed_out["outcome"], "failed")
        self.assertEqual(timed_out["reasonCode"], "timeout_out_of_range")
        self.assertEqual(adapter.calls, [])

        created = []
        unknown = lab.run_interactive_harness(
            context=context,
            expectation=expectation,
            snapshot=snapshot,
            adapter_factory=lambda: created.append(StubAdapter()),
            fixture_id="/tmp/user-document.docx",
        )
        self.assertEqual(unknown["outcome"], "blocked")
        self.assertEqual(created, [])
        self.assertNotIn("user-document", str(unknown))


if __name__ == "__main__":
    unittest.main()
