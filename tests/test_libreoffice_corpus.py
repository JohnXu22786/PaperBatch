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
PINNED_XLSX_PPTX_TEST_IDS = frozenset(
    {f"docx_variant_{index:03d}" for index in range(6)}
    | {"xlsx_variant_000", "pptx_variant_000"}
)


class LibreOfficeCorpusTests(unittest.TestCase):
    def test_console_summary_exposes_only_bounded_pdfium_stage_counts(self):
        def failed_row(**overrides):
            row = {
                "status": "failed", "timedOut": False, "launchAttempted": True,
                "identityVerified": True, "exitCode": 0, "outputPdfBytes": 100,
                "outputPdfSha256": "a" * 64,
                "pageInspection": {"status": "pinned-pdfium", "structureValid": True, "pageCount": 1},
                "failureCategory": "other",
                "inputPath": "/tmp/private document.docx", "reason": "private failure detail",
                "stdout": "private stdout", "stderr": "private stderr",
            }
            row.update(overrides)
            return row

        report = {
            "status": "failed",
            "counts": {
                "selectedForLibreOffice": 8,
                "successfulLibreOfficeConversions": 1,
                "failedLibreOfficeConversions": 7,
            },
            "results": [
                failed_row(timedOut=True, failureCategory="timeout"),
                failed_row(launchAttempted=False, failureCategory="launch-failure"),
                failed_row(identityVerified=False, failureCategory="identity-mismatch"),
                failed_row(exitCode=7, failureCategory="nonzero-exit"),
                failed_row(outputPdfBytes=None, outputPdfSha256=None, failureCategory="missing-output"),
                failed_row(pageInspection={"status": "structural-parser", "structureValid": False}, failureCategory="invalid-output"),
                failed_row(
                    pageInspection={"status": "pinned-pdfium-digest-check-failed-filenotfounderror", "structureValid": True},
                    failureCategory="other",
                ),
                {"status": "converted", "pageInspection": {"status": "pinned-pdfium"}},
            ],
            "reason": "private report reason",
        }

        summary = run_libreoffice_corpus.console_summary(report)

        self.assertIn("status=failed selected=8 converted=1 failed=7", summary)
        self.assertIn("conversion failure categories: nonzero-exit=1,timeout=1,launch-failure=1,identity-mismatch=1,missing-output=1,invalid-output=1,other=1", summary)
        self.assertIn("digest-check=1", summary)
        self.assertIn("passed=6", summary)
        self.assertNotIn("filenotfounderror", summary)
        self.assertNotIn("/tmp/private document.docx", summary)
        self.assertNotIn("private failure detail", summary)
        self.assertNotIn("private stdout", summary)
        self.assertNotIn("private stderr", summary)
        self.assertNotIn("private report reason", summary)

    def test_failure_categories_distinguish_launch_failure_from_unlaunched_rows(self):
        fixture = {
            "id": "docx_variant_000", "path": "sample.docx", "sha256": "a" * 64, "size_bytes": 10,
        }

        def result(command, *, input_sha256="a" * 64, reason=None):
            return run_libreoffice_corpus.result_record(
                fixture, status="failed", input_sha256=input_sha256, output_bytes=None,
                output_sha256=None, page_count=None, command=command, reason=reason,
            )

        changed_input = result(None, input_sha256="b" * 64, reason="input digest changed after manifest selection")
        exhausted = result(None, reason="total corpus runtime budget exhausted")
        prelaunch_budget = result({
            "exitCode": None, "timedOut": False, "launchAttempted": False,
            "budgetExpiredBeforeLaunch": True, "identityVerified": True,
        })
        launch_failure = result({
            "exitCode": None, "timedOut": False, "launchAttempted": False,
            "identityVerified": True,
        })
        process_creation_error = result({
            "exitCode": None, "timedOut": False, "launchAttempted": True,
            "identityVerified": False,
        })
        postprocess_identity_error = result({
            "exitCode": 0, "timedOut": False, "launchAttempted": True,
            "identityVerified": False,
        })

        self.assertEqual(changed_input["failureCategory"], "identity-mismatch")
        self.assertEqual(exhausted["failureCategory"], "other")
        self.assertEqual(prelaunch_budget["failureCategory"], "other")
        self.assertEqual(launch_failure["failureCategory"], "launch-failure")
        self.assertEqual(process_creation_error["failureCategory"], "launch-failure")
        self.assertEqual(postprocess_identity_error["failureCategory"], "identity-mismatch")

    @unittest.skipUnless(
        sys.platform.startswith("linux")
        and os.environ.get("PAPERBATCH_REQUIRE_LIBREOFFICE_TEST") == "1"
        and os.environ.get("PAPERBATCH_LO_ENGINE_PATH")
        and os.environ.get("PAPERBATCH_LO_ENGINE_SHA256"),
        "requires the pinned Linux LibreOffice CI runtime",
    )
    def test_pinned_libreoffice_accepts_generated_xlsx_and_pptx(self):
        manifest = json.loads((CORPUS_DIR / "manifest.json").read_text(encoding="utf-8"))
        target_ids = {"xlsx_variant_000", "pptx_variant_000"}
        test_fixture_ids = PINNED_XLSX_PPTX_TEST_IDS
        records = [
            record for record in manifest["fixtures"]
            if record["id"] in test_fixture_ids
        ]
        self.assertEqual({record["id"] for record in records}, test_fixture_ids)
        self.assertEqual({record["format"] for record in records}, {"docx", "xlsx", "pptx"})

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

            with mock.patch.object(run_libreoffice_corpus, "EXPECTED_FIXTURE_IDS", frozenset(test_fixture_ids)):
                report, exit_code = run_libreoffice_corpus.run_corpus(
                    os.environ["PAPERBATCH_LO_ENGINE_PATH"],
                    os.environ["PAPERBATCH_LO_ENGINE_SHA256"],
                    timeout_seconds=60,
                    budget_seconds=300,
                    corpus_dir=corpus,
                )

        self.assertEqual(exit_code, 0, report["results"])
        self.assertEqual(report["counts"]["selectedForLibreOffice"], len(test_fixture_ids))
        self.assertEqual(report["counts"]["successfulLibreOfficeConversions"], len(test_fixture_ids))
        self.assertEqual({result["inputId"].split("_", 1)[0] for result in report["results"]}, {"docx", "xlsx", "pptx"})
        self.assertEqual({result["inputId"] for result in report["results"]} & target_ids, target_ids)
        self.assertTrue(all(result["status"] == "converted" for result in report["results"]))
        for result in report["results"]:
            with self.subTest(input_id=result["inputId"]):
                inspection = result["pageInspection"]
                self.assertTrue(inspection["structureValid"])
                self.assertEqual(inspection["status"], "pinned-pdfium")
                self.assertIsInstance(inspection["pageCount"], int)
                self.assertGreater(inspection["pageCount"], 0)
        self.assertEqual(report["counts"]["humanReviewedFidelity"], 0)
        self.assertEqual(report["counts"]["compatibilityEvidenceCount"], 0)

    def test_xlsx_pptx_runtime_subset_has_complete_accepted_docx_context(self):
        manifest = json.loads((CORPUS_DIR / "manifest.json").read_text(encoding="utf-8"))
        records = [record for record in manifest["fixtures"] if record["id"] in PINNED_XLSX_PPTX_TEST_IDS]
        subset_manifest = {
            "synthetic": True,
            "evidence_class": "synthetic",
            "simulation_only": True,
            "fixtures": records,
        }

        with mock.patch.object(run_libreoffice_corpus, "EXPECTED_FIXTURE_IDS", PINNED_XLSX_PPTX_TEST_IDS):
            selected, skipped = run_libreoffice_corpus.select_fixtures(subset_manifest, CORPUS_DIR)

        self.assertEqual({item["id"] for item in selected}, PINNED_XLSX_PPTX_TEST_IDS)
        self.assertEqual({item["format"] for item in selected}, {"docx", "xlsx", "pptx"})
        self.assertEqual(skipped, [])

    def test_pdfium_page_count_uses_pinned_in_memory_document_api(self):
        pdf_bytes = b"%PDF-1.7\nsynthetic PDFium page-count test\n%%EOF\n"
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            library = root / "libpdfium.so"
            library.write_bytes(b"pinned PDFium fixture bytes")
            pdf_path = root / "converted.pdf"
            pdf_path.write_bytes(pdf_bytes)
            pdfium = SimpleNamespace(
                FPDF_InitLibrary=mock.Mock(),
                FPDF_DestroyLibrary=mock.Mock(),
                FPDF_LoadMemDocument=mock.Mock(return_value=1234),
                FPDF_GetPageCount=mock.Mock(return_value=3),
                FPDF_CloseDocument=mock.Mock(),
            )
            library_sha256 = hashlib.sha256(library.read_bytes()).hexdigest()

            with (
                mock.patch.dict(os.environ, {
                    "PAPERBATCH_PDFIUM_TEST_PATH": str(library),
                    "PAPERBATCH_PDFIUM_TEST_SHA256": library_sha256,
                }),
                mock.patch.object(run_libreoffice_corpus.ctypes, "CDLL", return_value=pdfium),
            ):
                page_count, inspection_status = run_libreoffice_corpus._pdfium_page_count(pdf_path)

        self.assertEqual((page_count, inspection_status), (3, "pinned-pdfium"))
        load_arguments = pdfium.FPDF_LoadMemDocument.call_args.args
        self.assertEqual(run_libreoffice_corpus.ctypes.string_at(load_arguments[0], len(pdf_bytes)), pdf_bytes)
        self.assertEqual(load_arguments[1], len(pdf_bytes))
        self.assertIsNone(load_arguments[2])
        pdfium.FPDF_InitLibrary.assert_called_once_with()
        pdfium.FPDF_CloseDocument.assert_called_once_with(1234)
        pdfium.FPDF_DestroyLibrary.assert_called_once_with()

    def test_pdfium_api_resolution_failure_reports_only_bounded_stage_and_error_type(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            library = root / "private-runner-path" / "libpdfium.so"
            library.parent.mkdir()
            library.write_bytes(b"pinned PDFium fixture bytes")
            pdf_path = root / "converted.pdf"
            pdf_path.write_bytes(b"%PDF-1.7\n%%EOF\n")
            partial_pdfium = SimpleNamespace()
            library_sha256 = hashlib.sha256(library.read_bytes()).hexdigest()

            with (
                mock.patch.dict(os.environ, {
                    "PAPERBATCH_PDFIUM_TEST_PATH": str(library),
                    "PAPERBATCH_PDFIUM_TEST_SHA256": library_sha256,
                }),
                mock.patch.object(run_libreoffice_corpus.ctypes, "CDLL", return_value=partial_pdfium),
            ):
                page_count, inspection_status = run_libreoffice_corpus._pdfium_page_count(pdf_path)

        self.assertIsNone(page_count)
        self.assertEqual(inspection_status, "pinned-pdfium-resolve-api-failed-attributeerror")
        self.assertNotIn(str(library), inspection_status)

    def test_pdfium_call_failure_reports_the_operation_stage_and_cleans_up(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            library = root / "libpdfium.so"
            library.write_bytes(b"pinned PDFium fixture bytes")
            pdf_path = root / "converted.pdf"
            pdf_path.write_bytes(b"%PDF-1.7\n%%EOF\n")
            pdfium = SimpleNamespace(
                FPDF_InitLibrary=mock.Mock(),
                FPDF_DestroyLibrary=mock.Mock(),
                FPDF_LoadMemDocument=mock.Mock(side_effect=run_libreoffice_corpus.ctypes.ArgumentError("private detail")),
                FPDF_GetPageCount=mock.Mock(),
                FPDF_CloseDocument=mock.Mock(),
            )
            library_sha256 = hashlib.sha256(library.read_bytes()).hexdigest()

            with (
                mock.patch.dict(os.environ, {
                    "PAPERBATCH_PDFIUM_TEST_PATH": str(library),
                    "PAPERBATCH_PDFIUM_TEST_SHA256": library_sha256,
                }),
                mock.patch.object(run_libreoffice_corpus.ctypes, "CDLL", return_value=pdfium),
            ):
                page_count, inspection_status = run_libreoffice_corpus._pdfium_page_count(pdf_path)

        self.assertIsNone(page_count)
        self.assertEqual(inspection_status, "pinned-pdfium-load-document-failed-argumenterror")
        self.assertNotIn("private detail", inspection_status)
        pdfium.FPDF_DestroyLibrary.assert_called_once_with()

    def test_pdfium_rejected_pdf_remains_visible_without_fabricated_page_count(self):
        pdf_bytes = b"%PDF-1.7\nrejected PDFium output\n%%EOF\n"
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            library = root / "libpdfium.so"
            library.write_bytes(b"pinned PDFium fixture bytes")
            pdf_path = root / "rejected.pdf"
            pdf_path.write_bytes(pdf_bytes)
            pdfium = SimpleNamespace(
                FPDF_InitLibrary=mock.Mock(),
                FPDF_DestroyLibrary=mock.Mock(),
                FPDF_LoadMemDocument=mock.Mock(return_value=None),
                FPDF_GetPageCount=mock.Mock(),
                FPDF_CloseDocument=mock.Mock(),
            )
            library_sha256 = hashlib.sha256(library.read_bytes()).hexdigest()

            with (
                mock.patch.dict(os.environ, {
                    "PAPERBATCH_PDFIUM_TEST_PATH": str(library),
                    "PAPERBATCH_PDFIUM_TEST_SHA256": library_sha256,
                }),
                mock.patch.object(run_libreoffice_corpus.ctypes, "CDLL", return_value=pdfium),
            ):
                page_count, inspection_status = run_libreoffice_corpus._pdfium_page_count(pdf_path)

        self.assertIsNone(page_count)
        self.assertEqual(inspection_status, "pinned-pdfium-rejected-output")
        pdfium.FPDF_GetPageCount.assert_not_called()
        pdfium.FPDF_DestroyLibrary.assert_called_once_with()
        result = run_libreoffice_corpus.result_record(
            {"id": "docx_variant_000", "path": "sample.docx", "sha256": "a" * 64, "size_bytes": 10},
            status="converted", input_sha256="a" * 64, output_bytes=100, output_sha256="b" * 64,
            page_count=page_count, command={"exitCode": 0, "timedOut": False},
            page_inspection_status=inspection_status, structure_valid=True,
        )
        self.assertEqual(result["status"], "converted")
        self.assertEqual(result["pageInspection"], {
            "status": "pinned-pdfium-rejected-output", "structureValid": True, "pageCount": None,
        })

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

    def test_manifest_byte_and_fixture_count_limits_fail_closed(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "manifest.json"
            path.write_bytes(b" " * (run_libreoffice_corpus.MAX_MANIFEST_BYTES + 1))
            with self.assertRaisesRegex(ValueError, "byte limit"):
                run_libreoffice_corpus.read_manifest(path)

        manifest = json.loads((CORPUS_DIR / "manifest.json").read_text(encoding="utf-8"))
        fixture = next(item for item in manifest["fixtures"] if item["id"] == "docx_variant_000")
        manifest["fixtures"] = [fixture] * (run_libreoffice_corpus.MAX_MANIFEST_FIXTURES + 1)
        with self.assertRaisesRegex(ValueError, "fixture count"):
            run_libreoffice_corpus.select_fixtures(manifest, CORPUS_DIR)

    def test_selection_fails_closed_when_any_required_accepted_variant_is_missing(self):
        manifest = json.loads((CORPUS_DIR / "manifest.json").read_text(encoding="utf-8"))
        manifest["fixtures"] = [item for item in manifest["fixtures"] if item["id"] != "pptx_variant_029"]

        with self.assertRaisesRegex(ValueError, "missing required accepted synthetic fixtures.*pptx_variant_029"):
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
                selected, skipped = run_libreoffice_corpus.select_fixtures(manifest, CORPUS_DIR, require_complete=False)

                self.assertEqual(selected, [])
                self.assertEqual(len(skipped), 1)
                self.assertNotIn("Jane Doe", skipped[0]["path"])
                self.assertIn("[PATH]", skipped[0]["path"])

    def test_selected_windows_manifest_path_is_redacted_in_results(self):
        windows_path = "C:\\Users\\Jane Doe\\document.docx"
        selected_fixture = {
            "id": "docx_variant_000", "path": windows_path, "sha256": "a" * 64, "size_bytes": 123,
        }
        result = run_libreoffice_corpus.result_record(
            selected_fixture, status="failed", input_sha256=selected_fixture["sha256"], output_bytes=None,
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

            with (
                mock.patch.object(run_libreoffice_corpus.sys, "platform", "linux"),
                mock.patch.object(run_libreoffice_corpus, "EXPECTED_FIXTURE_IDS", frozenset()),
                mock.patch.object(run_libreoffice_corpus.probe, "open_pinned_engine") as open_engine,
                mock.patch.object(run_libreoffice_corpus.probe, "invoke") as invoke,
                redirect_stdout(output),
            ):
                exit_code = run_libreoffice_corpus.main([
                    "--engine-binary", str(missing_engine),
                    "--expected-engine-sha256", "a" * 64,
                    "--corpus-dir", str(corpus),
                ])

            open_engine.assert_not_called()
            invoke.assert_not_called()

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

    def test_report_records_bounded_runner_and_signed_installer_provenance(self):
        report = run_libreoffice_corpus.classify_report(
            selected=[], skipped=[], results=[],
            engine={"version": "LibreOffice 26.2.2.2", "binarySha256": "a" * 64},
            run_metadata={
                "os": "macOS", "runner": "GitHub Actions 42", "runnerImage": "macos-15",
                "runnerImageVersion": "20261020.1",
                "installer": {
                    "version": "26.2.2.2", "source": "Document Foundation archive",
                    "sourceUrl": "https://downloadarchive.documentfoundation.org/libreoffice/old/26.2.2.2/mac/x86_64/LibreOffice.dmg",
                    "package": "LibreOffice.dmg", "packageSha256": "b" * 64,
                    "integrityMethod": "OpenPGP detached signature",
                    "signingKeyFingerprint": "C2839ECAD9408FBE9531C3E9F434A1EFAFEEAEA3",
                },
            },
        )

        self.assertEqual(report["run"]["os"], "macOS")
        self.assertEqual(report["run"]["runnerImage"], "macos-15")
        self.assertEqual(report["run"]["installer"]["packageSha256"], "b" * 64)
        self.assertEqual(report["run"]["installer"]["signingKeyFingerprint"], "C2839ECAD9408FBE9531C3E9F434A1EFAFEEAEA3")
        self.assertEqual(report["counts"]["compatibilityEvidenceCount"], 0)
        self.assertIn("does not establish Office/WPS compatibility", report["classification"])

    def test_report_blocks_a_successful_conversion_when_installer_provenance_is_unverified(self):
        report = run_libreoffice_corpus.classify_report(
            selected=[{"id": "docx_variant_000"}], skipped=[],
            results=[{"id": "docx_variant_000", "status": "converted"}],
            engine={"version": "LibreOffice 26.2.2.2", "binarySha256": "a" * 64},
            run_metadata={"installer": {"status": "invalid"}},
        )

        self.assertEqual(report["status"], "blocked")
        self.assertEqual(report["counts"]["successfulLibreOfficeConversions"], 1)

    def test_installer_provenance_rejects_oversized_and_malformed_records(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "installer.json"
            with mock.patch.dict(os.environ, {"PAPERBATCH_LO_INSTALLER_PROVENANCE": str(path)}):
                path.write_text("{}", encoding="utf-8")
                self.assertEqual(run_libreoffice_corpus._run_metadata()["installer"], {"status": "invalid"})
                path.write_bytes(b" " * 4097)
                self.assertEqual(run_libreoffice_corpus._run_metadata()["installer"], {"status": "invalid"})

    def test_installer_provenance_accepts_only_bounded_hash_and_signer_fields(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "installer.json"
            path.write_text(json.dumps({
                "version": "26.2.2.2", "source": "Document Foundation official archive",
                "sourceUrl": "https://downloadarchive.documentfoundation.org/libreoffice/old/26.2.2.2/win/x86_64/LibreOffice.msi",
                "package": "signed-installer", "packageSha256": "b" * 64,
                "integrityMethod": "OpenPGP detached signature",
                "signingKeyFingerprint": "C2839ECAD9408FBE9531C3E9F434A1EFAFEEAEA3",
                "unboundedSecret": "must-not-be-copied",
            }), encoding="utf-8")
            with mock.patch.dict(os.environ, {"PAPERBATCH_LO_INSTALLER_PROVENANCE": str(path)}, clear=True):
                metadata = run_libreoffice_corpus._run_metadata()

        self.assertEqual(metadata["installer"]["status"], "verified")
        self.assertEqual(metadata["installer"]["packageSha256"], "b" * 64)
        self.assertTrue(metadata["installer"]["sourceUrl"].startswith("https://downloadarchive.documentfoundation.org/"))
        self.assertNotIn("unboundedSecret", metadata["installer"])

    def test_corpus_runner_platform_modes_are_linux_macos_windows_specific(self):
        manifest = json.loads((CORPUS_DIR / "manifest.json").read_text(encoding="utf-8"))
        sample = next(item for item in manifest["fixtures"] if item["id"] == "docx_variant_000")
        with tempfile.TemporaryDirectory() as directory:
            corpus = Path(directory) / "corpus"
            corpus.mkdir()
            shutil.copyfile(CORPUS_DIR / sample["path"], corpus / "sample.docx")
            sample = {**sample, "path": "sample.docx"}
            (corpus / "manifest.json").write_text(json.dumps({**manifest, "fixtures": [sample]}), encoding="utf-8")
            engine = Path(directory) / "soffice.bin"
            engine.write_bytes(b"trusted test placeholder")
            pinned = SimpleNamespace(sha256="a" * 64, runtime_directory_sha256="b" * 64, close=lambda: None)
            provenance = Path(directory) / "installer.json"
            provenance.write_text(json.dumps({
                "version": "26.2.2.2", "source": "Document Foundation official archive",
                "sourceUrl": "https://downloadarchive.documentfoundation.org/libreoffice/old/26.2.2.2/test.pkg",
                "package": "pinned.pkg", "packageSha256": "b" * 64,
                "integrityMethod": "OpenPGP detached signature",
                "signingKeyFingerprint": "C2839ECAD9408FBE9531C3E9F434A1EFAFEEAEA3",
            }), encoding="utf-8")
            for target, expected_network, expected_launcher in (
                ("linux", True, True), ("win32", False, False), ("darwin", False, True),
            ):
                with (
                    self.subTest(platform=target),
                    mock.patch.object(run_libreoffice_corpus, "EXPECTED_FIXTURE_IDS", frozenset({sample["id"]})),
                    mock.patch.dict(os.environ, {"PAPERBATCH_LO_INSTALLER_PROVENANCE": str(provenance)}),
                    mock.patch.object(run_libreoffice_corpus.sys, "platform", target),
                    mock.patch.object(run_libreoffice_corpus.probe, "open_pinned_engine", return_value=pinned),
                    mock.patch.object(run_libreoffice_corpus.probe, "invoke", side_effect=[
                        {"stdout": "LibreOffice 26.2.2.2", "stderr": "", "exitCode": 0, "timedOut": False, "identityVerified": True},
                        {"stdout": "", "stderr": "", "exitCode": 0, "timedOut": False, "identityVerified": True},
                    ]) as invoke,
                    mock.patch.object(run_libreoffice_corpus.probe, "inspect_pdf", return_value=(100, True, True, False, "c" * 64)),
                ):
                    report, exit_code = run_libreoffice_corpus.run_corpus(str(engine), "a" * 64, 60, 180, corpus)

                self.assertEqual(exit_code, 0)
                self.assertEqual(report["status"], "passed")
                self.assertEqual(report["counts"]["successfulLibreOfficeConversions"], 1)
                self.assertEqual(report["run"]["networkIsolation"].startswith("Linux x86_64 seccomp"), expected_network)
                self.assertEqual(invoke.call_args.kwargs["disable_network"], expected_network)
                self.assertEqual(invoke.call_args.kwargs["use_launcher"], expected_launcher)

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
                mock.patch.object(run_libreoffice_corpus, "EXPECTED_FIXTURE_IDS", frozenset({"docx_variant_000", "docx_variant_001"})),
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
        expected_launcher = sys.platform in ("linux", "darwin")
        self.assertEqual(launcher_modes, [False, expected_launcher, expected_launcher])

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
                mock.patch.object(run_libreoffice_corpus, "EXPECTED_FIXTURE_IDS", frozenset({"docx_variant_000"})),
                mock.patch.object(run_libreoffice_corpus.probe, "open_pinned_engine", return_value=pinned),
                mock.patch.object(run_libreoffice_corpus.probe, "invoke") as invoke,
            ):
                report, exit_code = run_libreoffice_corpus.run_corpus(str(engine), "a" * 64, 60, 0, corpus)

        invoke.assert_not_called()
        self.assertEqual(exit_code, 1)
        self.assertEqual(report["results"][0]["status"], "failed")

    def test_runner_accepts_supported_host_platforms_and_keeps_linux_sandbox_specific(self):
        with mock.patch.object(run_libreoffice_corpus.sys, "platform", "win32"):
            self.assertIn("win32", run_libreoffice_corpus.SUPPORTED_PLATFORMS)
        with mock.patch.object(run_libreoffice_corpus.sys, "platform", "darwin"):
            self.assertIn("darwin", run_libreoffice_corpus.SUPPORTED_PLATFORMS)
        self.assertNotIn("win32", run_libreoffice_corpus.NETWORK_SANDBOX_PLATFORMS)
        self.assertIn("linux", run_libreoffice_corpus.NETWORK_SANDBOX_PLATFORMS)

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
