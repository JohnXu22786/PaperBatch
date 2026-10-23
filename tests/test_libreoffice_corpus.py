"""Regression tests for bounded synthetic LibreOffice corpus evidence."""

from __future__ import annotations

import json
import hashlib
import io
import os
import platform
import shutil
import zipfile
import sys
import tempfile
import unittest
from contextlib import redirect_stdout
from types import SimpleNamespace
from unittest import mock
from pathlib import Path

REPOSITORY_ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(REPOSITORY_ROOT / "tools"))
import run_libreoffice_corpus


CORPUS_DIR = REPOSITORY_ROOT / "tests" / "fixtures" / "synthetic_documents"


class LibreOfficeCorpusTests(unittest.TestCase):
    @unittest.skipUnless(
        sys.platform.startswith("linux")
        and os.environ.get("PAPERBATCH_REQUIRE_LIBREOFFICE_TEST") == "1"
        and os.environ.get("PAPERBATCH_LO_ENGINE_PATH")
        and os.environ.get("PAPERBATCH_LO_ENGINE_SHA256"),
        "requires the pinned Linux LibreOffice CI runtime",
    )
    def test_pinned_libreoffice_accepts_generated_xlsx_and_pptx(self):
        manifest = json.loads((CORPUS_DIR / "manifest.json").read_text(encoding="utf-8"))
        records = [
            record for record in manifest["fixtures"]
            if record["id"] in {"xlsx_variant_000", "pptx_variant_000"}
        ]
        self.assertEqual({record["format"] for record in records}, {"xlsx", "pptx"})

        with tempfile.TemporaryDirectory() as directory:
            corpus = Path(directory)
            isolated_records = []
            for record in records:
                relative_path = Path(record["path"])
                destination = corpus / relative_path.name
                shutil.copyfile(CORPUS_DIR / relative_path, destination)
                isolated = dict(record)
                isolated["path"] = destination.name
                isolated_records.append(isolated)
            (corpus / "manifest.json").write_text(
                json.dumps({
                    "synthetic": True,
                    "evidence_class": "synthetic",
                    "simulation_only": True,
                    "fixtures": isolated_records,
                }),
                encoding="utf-8",
            )

            report, exit_code = run_libreoffice_corpus.run_corpus(
                os.environ["PAPERBATCH_LO_ENGINE_PATH"],
                os.environ["PAPERBATCH_LO_ENGINE_SHA256"],
                timeout_seconds=60,
                budget_seconds=180,
                corpus_dir=corpus,
            )

        self.assertEqual(exit_code, 0, report["results"])
        self.assertEqual(report["counts"]["selectedForLibreOffice"], 2)
        self.assertEqual(report["counts"]["successfulLibreOfficeConversions"], 2)
        self.assertEqual({result["inputId"].split("_", 1)[0] for result in report["results"]}, {"xlsx", "pptx"})
        self.assertTrue(all(result["status"] == "converted" for result in report["results"]))
        self.assertEqual(report["counts"]["humanReviewedFidelity"], 0)
        self.assertEqual(report["counts"]["compatibilityEvidenceCount"], 0)

    def test_selection_is_manifest_bound_and_skips_inert_or_out_of_scope_records(self):
        manifest = json.loads((CORPUS_DIR / "manifest.json").read_text(encoding="utf-8"))

        selected, skipped = run_libreoffice_corpus.select_fixtures(manifest, CORPUS_DIR)

        self.assertEqual(len(selected), 90)
        self.assertEqual({item["format"] for item in selected}, {"docx", "xlsx", "pptx"})
        self.assertEqual(len({item["id"] for item in selected}), len(selected))
        self.assertEqual(
            {item["id"] for item in selected},
            {f"{file_format}_variant_{index:03d}" for file_format in ("docx", "xlsx", "pptx") for index in range(30)},
        )
        self.assertTrue(all(item["sha256"] == item["manifestSha256"] for item in selected))
        skipped_by_id = {item["id"]: item["reason"] for item in skipped}
        self.assertIn("malformed_variant_000", skipped_by_id)
        malformed = next(item for item in skipped if item["id"] == "malformed_variant_000")
        self.assertEqual(malformed["sha256"], next(item["sha256"] for item in manifest["fixtures"] if item["id"] == "malformed_variant_000"))
        self.assertIn("truncated", skipped_by_id["malformed_variant_000"].lower())
        self.assertIn("opc_external_hyperlink_no_network", skipped_by_id)
        self.assertIn("external", skipped_by_id["opc_external_hyperlink_no_network"].lower())

    @unittest.skipUnless(sys.platform.startswith("linux") and platform.machine().lower() in {"x86_64", "amd64"}, "requires Linux x86_64 seccomp")
    def test_child_process_cannot_create_network_sockets(self):
        result = run_libreoffice_corpus.probe.run_bounded_process(
            [sys.executable, "-c", "import socket; socket.socket()"],
            timeout_seconds=5,
            max_log_chars=512,
            preexec_fn=run_libreoffice_corpus.probe.deny_child_network,
        )

        self.assertEqual(result.returncode, 1)
        self.assertIn("Operation not permitted", result.stderr)

    @unittest.skipUnless(sys.platform.startswith("linux") and platform.machine().lower() in {"x86_64", "amd64"}, "requires Linux x86_64 seccomp")
    def test_child_process_can_use_local_ipc_but_not_network_sockets(self):
        code = (
            "import errno, socket, sys\n"
            "local=socket.socket(socket.AF_UNIX, socket.SOCK_STREAM); local.close()\n"
            "pair=socket.socketpair(socket.AF_UNIX, socket.SOCK_STREAM); [s.close() for s in pair]\n"
            "try:\n    socket.socket(socket.AF_INET, socket.SOCK_STREAM)\n"
            "except OSError as error:\n    sys.exit(0 if error.errno == errno.EPERM else 2)\n"
            "sys.exit(1)\n"
        )
        result = run_libreoffice_corpus.probe.run_bounded_process(
            [sys.executable, "-c", code],
            timeout_seconds=5,
            max_log_chars=512,
            preexec_fn=run_libreoffice_corpus.probe.deny_child_network,
        )

        self.assertEqual(result.returncode, 0, result.stderr)


    @unittest.skipUnless(sys.platform.startswith("linux") and platform.machine().lower() in {"x86_64", "amd64"}, "requires Linux x86_64 seccomp")
    def test_child_process_cannot_use_x32_network_syscall_numbers(self):
        code = (
            "import ctypes, errno, sys; libc=ctypes.CDLL(None, use_errno=True); "
            "libc.syscall.restype=ctypes.c_long; "
            "result=libc.syscall(ctypes.c_long(0x40000000 | 41), 2, 1, 0); "
            "error=ctypes.get_errno(); print(error); "
            "sys.exit(0 if result == -1 and error == errno.EPERM else 1)"
        )
        result = run_libreoffice_corpus.probe.run_bounded_process(
            [sys.executable, "-c", code],
            timeout_seconds=5,
            max_log_chars=80,
            preexec_fn=run_libreoffice_corpus.probe.deny_child_network,
        )

        self.assertEqual(result.returncode, 0)
        self.assertEqual(result.stdout.strip(), "1")

    def test_package_with_binary_vba_member_is_rejected(self):
        with tempfile.TemporaryDirectory() as directory:
            package = Path(directory) / "macro.docx"
            with zipfile.ZipFile(package, "w") as archive:
                archive.writestr("[Content_Types].xml", "<Types/>")
                archive.writestr("_rels/.rels", "<Relationships/>")
                archive.writestr("word/document.xml", "<document/>")
                archive.writestr("word/vbaProject.bin", b"synthetic macro payload")

            reason = run_libreoffice_corpus.validate_package(package, "docx")

        self.assertIn("macro", reason.lower())

    def test_child_logs_are_capped_while_the_process_is_drained(self):
        result = run_libreoffice_corpus.probe.run_bounded_process(
            [sys.executable, "-c", "import sys; sys.stdout.write('x' * 100000); sys.stderr.write('y' * 100000)"],
            timeout_seconds=5,
            max_log_chars=80,
        )

        self.assertEqual(result.returncode, 0)
        self.assertLessEqual(len(result.stdout), 80)
        self.assertLessEqual(len(result.stderr), 80)
        self.assertTrue(result.stdout.endswith("[truncated]"))
        self.assertTrue(result.stderr.endswith("[truncated]"))

    def test_result_manifest_redacts_secrets_and_absolute_paths_from_engine_logs(self):
        fixture = {
            "id": "docx_variant_000",
            "path": "variants/docx_000.docx",
            "sha256": "a" * 64,
            "size_bytes": 123,
        }
        result = run_libreoffice_corpus.result_record(
            fixture,
            status="failed",
            input_sha256="a" * 64,
            output_bytes=None,
            output_sha256=None,
            page_count=None,
            command={
                "exitCode": 1,
                "timedOut": False,
                "stdout": "auth_token=synthetic-secret credential=synthetic-credential credentials=synthetic-credentials password=\"synthetic password phrase\" Authorization: Bearer synthetic-bearer /home/alice/private/input.docx\npath:/home/alice/private.docx\npassword=/home/Jane Doe/private.docx\nC:\\Users\\Jane Doe\\private\\secret file.docx\n/home/alice/private,account.docx",
                "stderr": "Authorization: Basic synthetic-basic-credential failed",
                "identityVerified": False,
                "identityFailure": "runtime token=identity-secret changed at /home/alice/runtime",
            },
        )

        serialized = json.dumps(result)
        self.assertNotIn("synthetic-secret", serialized)
        self.assertNotIn("synthetic-credential", serialized)
        self.assertNotIn("synthetic-credentials", serialized)
        self.assertNotIn("password phrase", serialized)
        self.assertNotIn("synthetic-bearer", serialized)
        self.assertNotIn("synthetic-basic-credential", serialized)
        self.assertNotIn("identity-secret", serialized)
        self.assertNotIn("/home/alice", serialized)
        self.assertNotIn("/home/Jane", serialized)
        self.assertNotIn("Doe/private.docx", serialized)
        self.assertNotIn("Jane Doe", serialized)
        self.assertNotIn("secret file.docx", serialized)
        self.assertNotIn("account.docx", serialized)
        self.assertIn("[REDACTED]", serialized)
        self.assertIn("[PATH]", serialized)

    def test_selection_rejects_manifest_digest_mismatch_and_invalid_packages(self):
        manifest = json.loads((CORPUS_DIR / "manifest.json").read_text(encoding="utf-8"))
        fixture = next(item for item in manifest["fixtures"] if item["id"] == "docx_variant_000")
        fixture["sha256"] = "0" * 64

        with self.assertRaisesRegex(ValueError, "digest"):
            run_libreoffice_corpus.select_fixtures(manifest, CORPUS_DIR)

    def test_skipped_manifest_paths_are_redacted(self):
        corpus_manifest = json.loads((CORPUS_DIR / "manifest.json").read_text(encoding="utf-8"))
        source_fixture = next(item for item in corpus_manifest["fixtures"] if item["id"] == "docx_variant_000")

        for path in ("/home/Jane Doe/private/document.docx", "C:\\Users\\Jane Doe\\private\\document.docx"):
            with self.subTest(path=path):
                fixture = {**source_fixture, "path": path}
                manifest = {
                    "synthetic": True,
                    "evidence_class": "synthetic",
                    "simulation_only": True,
                    "fixtures": [fixture],
                }
                selected, skipped = run_libreoffice_corpus.select_fixtures(manifest, CORPUS_DIR)

                self.assertEqual(selected, [])
                self.assertEqual(len(skipped), 1)
                self.assertNotIn("Jane Doe", skipped[0]["path"])
                self.assertIn("[PATH]", skipped[0]["path"])

    def test_selected_windows_manifest_path_is_redacted_in_results(self):
        source_fixture = CORPUS_DIR / "variants/docx_000.docx"
        windows_path = "C:\\Users\\Jane Doe\\document.docx"
        with tempfile.TemporaryDirectory() as directory:
            corpus = Path(directory)
            target = corpus / windows_path
            target.write_bytes(source_fixture.read_bytes())
            data = target.read_bytes()
            manifest = {
                "synthetic": True, "evidence_class": "synthetic", "simulation_only": True,
                "fixtures": [{
                    "id": "docx_variant_000", "path": windows_path, "format": "docx", "stratum": "docx",
                    "expected_outcome": {"status": "accepted"}, "evidence_class": "synthetic", "simulation_only": True,
                    "sha256": hashlib.sha256(data).hexdigest(), "size_bytes": len(data),
                }],
            }
            selected, skipped = run_libreoffice_corpus.select_fixtures(manifest, corpus)

        self.assertEqual(skipped, [])
        self.assertEqual(len(selected), 1)
        result = run_libreoffice_corpus.result_record(
            selected[0], status="failed", input_sha256=selected[0]["sha256"], output_bytes=None,
            output_sha256=None, page_count=None, command=None,
        )
        self.assertNotIn("Jane Doe", result["inputPath"])
        self.assertIn("[PATH]", result["inputPath"])

    def test_blocked_cli_report_redacts_missing_engine_path(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            corpus = root / "corpus"
            corpus.mkdir()
            (corpus / "manifest.json").write_text(json.dumps({
                "synthetic": True,
                "evidence_class": "synthetic",
                "simulation_only": True,
                "fixtures": [],
            }), encoding="utf-8")
            missing_engine = root / "Jane Doe" / "missing-engine"
            output = io.StringIO()

            with redirect_stdout(output):
                exit_code = run_libreoffice_corpus.main([
                    "--engine-binary", str(missing_engine),
                    "--expected-engine-sha256", "a" * 64,
                    "--corpus-dir", str(corpus),
                ])

        report_text = output.getvalue()
        report = json.loads(report_text)
        self.assertEqual(exit_code, 1)
        self.assertNotIn(directory, report_text)
        self.assertIn("[PATH]", report["reason"])

    def test_report_classification_never_promotes_rendering_to_compatibility_evidence(self):
        report = run_libreoffice_corpus.classify_report(
            selected=[{"id": "docx_variant_000"}, {"id": "xlsx_variant_000"}],
            skipped=[{"id": "malformed_variant_000", "reason": "truncated"}],
            results=[{"id": "docx_variant_000", "status": "converted"}, {"id": "xlsx_variant_000", "status": "failed"}],
            engine={"version": "LibreOffice 26.2.2.2", "binarySha256": "a" * 64},
        )

        self.assertEqual(report["counts"]["syntheticSourceSpecimens"], 3)
        self.assertEqual(report["counts"]["successfulLibreOfficeConversions"], 1)
        self.assertEqual(report["counts"]["humanReviewedFidelity"], 0)
        self.assertEqual(report["counts"]["officeWpsRuns"], 0)
        self.assertEqual(report["counts"]["oemDriverDeviceRuns"], 0)
        self.assertEqual(report["counts"]["physicalOutputRuns"], 0)
        self.assertEqual(report["counts"]["compatibilityEvidenceCount"], 0)
        self.assertEqual(report["evidenceGates"], {"G1": "open", "G3": "open", "G4": "open", "U8NativeDeviceCleanPc": "open"})

    @unittest.skipUnless(sys.platform.startswith("linux"), "corpus conversion requires the Linux seccomp sandbox")
    def test_batch_continues_after_one_file_failure_with_separate_profiles(self):
        with tempfile.TemporaryDirectory() as directory:
            corpus = Path(directory) / "corpus"
            corpus.mkdir()
            source = CORPUS_DIR / "variants/docx_000.docx"
            fixtures = []
            for index in range(2):
                name = f"docx_variant_{index:03d}.docx"
                target = corpus / name
                shutil.copyfile(source, target)
                data = target.read_bytes()
                fixtures.append({
                    "id": f"docx_variant_{index:03d}", "path": name, "format": "docx", "stratum": "docx",
                    "expected_outcome": {"status": "accepted"}, "evidence_class": "synthetic",
                    "simulation_only": True, "sha256": hashlib.sha256(data).hexdigest(), "size_bytes": len(data),
                })
            manifest = {"synthetic": True, "evidence_class": "synthetic", "simulation_only": True, "fixtures": fixtures}
            (corpus / "manifest.json").write_text(json.dumps(manifest), encoding="utf-8")
            engine = Path(directory) / "soffice.bin"
            engine.write_bytes(b"trusted fixture engine placeholder")
            pinned = SimpleNamespace(sha256="a" * 64, runtime_directory_sha256="b" * 64, close=lambda: None)
            profiles = []
            temporary_directories = []
            launcher_modes = []

            def invoke(_engine, args, profile, _timeout, **kwargs):
                profiles.append(profile)
                launcher_modes.append(kwargs.get("use_launcher", False))
                if "--version" in args:
                    return {"stdout": "LibreOffice 26.2.2.2", "stderr": "", "exitCode": 0, "timedOut": False, "identityVerified": True}
                temporary_directories.append(kwargs["temporary_directory"])
                output_dir = Path(args[args.index("--outdir") + 1])
                output_dir.joinpath(Path(args[-1]).stem + ".pdf").write_bytes(b"synthetic pdf placeholder")
                exit_code = 1 if "000" in args[-1] else 0
                return {"stdout": "", "stderr": "failure" if exit_code else "", "exitCode": exit_code, "timedOut": False, "identityVerified": True}

            with (
                mock.patch.object(run_libreoffice_corpus.probe, "open_pinned_engine", return_value=pinned),
                mock.patch.object(run_libreoffice_corpus.probe, "invoke", side_effect=invoke),
                mock.patch.object(run_libreoffice_corpus.probe, "inspect_pdf", return_value=(100, True, True, False, "c" * 64)),
            ):
                report, exit_code = run_libreoffice_corpus.run_corpus(str(engine), "a" * 64, 60, 900, corpus)

        self.assertEqual(exit_code, 1)
        self.assertEqual([item["status"] for item in report["results"]], ["failed", "converted"])
        self.assertEqual(report["counts"]["successfulLibreOfficeConversions"], 1)
        self.assertEqual(len(set(profiles)), 3)
        self.assertEqual(len(set(temporary_directories)), 2)
        self.assertTrue(all(path.name == "intermediate" for path in temporary_directories))
        self.assertEqual(launcher_modes, [False, True, True])

    @unittest.skipUnless(sys.platform.startswith("linux"), "corpus conversion requires the Linux seccomp sandbox")
    def test_exhausted_batch_budget_does_not_launch_version_probe(self):
        with tempfile.TemporaryDirectory() as directory:
            corpus = Path(directory) / "corpus"
            corpus.mkdir()
            source = CORPUS_DIR / "variants/docx_000.docx"
            target = corpus / "docx_variant_000.docx"
            shutil.copyfile(source, target)
            data = target.read_bytes()
            manifest = {
                "synthetic": True, "evidence_class": "synthetic", "simulation_only": True,
                "fixtures": [{"id": "docx_variant_000", "path": target.name, "format": "docx", "stratum": "docx",
                    "expected_outcome": {"status": "accepted"}, "evidence_class": "synthetic", "simulation_only": True,
                    "sha256": hashlib.sha256(data).hexdigest(), "size_bytes": len(data)}],
            }
            (corpus / "manifest.json").write_text(json.dumps(manifest), encoding="utf-8")
            engine = Path(directory) / "soffice.bin"
            engine.write_bytes(b"trusted fixture engine placeholder")
            pinned = SimpleNamespace(sha256="a" * 64, runtime_directory_sha256="b" * 64, close=lambda: None)

            with (
                mock.patch.object(run_libreoffice_corpus.probe, "open_pinned_engine", return_value=pinned),
                mock.patch.object(run_libreoffice_corpus.probe, "invoke") as invoke,
            ):
                report, exit_code = run_libreoffice_corpus.run_corpus(str(engine), "a" * 64, 60, 0, corpus)

        invoke.assert_not_called()
        self.assertEqual(exit_code, 1)
        self.assertEqual(report["results"][0]["status"], "failed")

    def test_runner_fails_closed_with_explicit_unsupported_platform_error(self):
        with mock.patch.object(run_libreoffice_corpus.sys, "platform", "win32"):
            with self.assertRaisesRegex(ValueError, "requires Linux for its no-network process sandbox"):
                run_libreoffice_corpus.run_corpus("unused", "a" * 64, 60, 900, Path("."))

    def test_manifest_provenance_is_recorded_without_pdf_byte_stability_claim(self):
        with tempfile.TemporaryDirectory() as directory:
            fixture = {"id": "docx_variant_000", "path": "variants/docx_000.docx", "sha256": "a" * 64, "size_bytes": 123}
            result = run_libreoffice_corpus.result_record(
                fixture,
                status="converted",
                input_sha256="a" * 64,
                output_bytes=123,
                output_sha256="b" * 64,
                page_count=1,
                command={"exitCode": 0, "timedOut": False},
            )
        self.assertEqual(result["inputId"], "docx_variant_000")
        self.assertEqual(result["inputSha256"], fixture["sha256"])
        self.assertEqual(result["outputPdfSha256"], "b" * 64)
        self.assertEqual(result["pageInspection"]["pageCount"], 1)
        self.assertNotIn("expectedOutputSha256", result)

    def test_failed_conversion_report_explains_empty_process_diagnostics(self):
        fixture = {"id": "docx_variant_000", "path": "variants/docx_000.docx", "sha256": "a" * 64, "size_bytes": 123}
        result = run_libreoffice_corpus.result_record(
            fixture,
            status="failed",
            input_sha256="a" * 64,
            output_bytes=None,
            output_sha256=None,
            page_count=None,
            command={"exitCode": 1, "timedOut": False, "durationSeconds": 0.36, "stdout": "", "stderr": ""},
        )

        self.assertIn("exited with code 1", result["reason"])
        self.assertIn("no output PDF was produced", result["reason"])
        self.assertIn("stdout, stderr empty", result["reason"])

    def test_prelaunch_deadline_report_does_not_claim_a_conversion_ran(self):
        fixture = {"id": "docx_variant_000", "path": "variants/docx_000.docx", "sha256": "a" * 64, "size_bytes": 123}
        command = {
            "exitCode": None, "timedOut": False, "launchAttempted": False, "budgetExpiredBeforeLaunch": True,
            "durationSeconds": 0.36, "stdout": "", "stderr": "total runtime budget exhausted before LibreOffice launch",
        }

        result = run_libreoffice_corpus.result_record(
            fixture, status="failed", input_sha256="a" * 64, output_bytes=None,
            output_sha256=None, page_count=None, command=command,
        )

        self.assertIn("before LibreOffice launch", result["reason"])
        self.assertNotIn("during conversion", result["reason"])

        setup_failure = run_libreoffice_corpus.result_record(
            fixture, status="failed", input_sha256="a" * 64, output_bytes=None,
            output_sha256=None, page_count=None,
            command={"exitCode": None, "timedOut": False, "launchAttempted": False,
                     "stderr": "runtime directory verification failed"},
        )
        self.assertIn("was not launched", setup_failure["reason"])
        self.assertNotIn("during conversion", setup_failure["reason"])

    def test_identity_failure_is_structured_even_when_process_and_pdf_succeed(self):
        fixture = {"id": "docx_variant_000", "path": "variants/docx_000.docx", "sha256": "a" * 64, "size_bytes": 123}
        command = {
            "exitCode": 0, "timedOut": False, "launchAttempted": True,
            "identityVerified": False, "identityFailure": "runtime components changed after conversion",
            "stdout": "converted", "stderr": "identity verification failed",
        }

        result = run_libreoffice_corpus.result_record(
            fixture, status="failed", input_sha256="a" * 64, output_bytes=100,
            output_sha256="b" * 64, page_count=1, command=command, structure_valid=True,
        )

        self.assertFalse(result["identityVerified"])
        self.assertEqual(result["identityFailure"], command["identityFailure"])
        self.assertIn("runtime components changed", result["reason"])
        self.assertIn("output PDF was 100 bytes", result["reason"])


if __name__ == "__main__":
    unittest.main()
