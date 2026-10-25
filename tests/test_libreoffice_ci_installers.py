from __future__ import annotations

import os
import json
import sys
import tempfile
import unittest
from pathlib import Path
from types import SimpleNamespace
from unittest import mock

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / "tools"))
import build_libreoffice_ci_summary
import build_application_import_ci_summary
import resolve_macos_libreoffice_engine


class LibreOfficeCiInstallerTests(unittest.TestCase):
    def test_every_hosted_os_lane_uses_the_standard_matrix_and_corpus_report(self):
        workflow = (ROOT / ".github/workflows/ci.yml").read_text(encoding="utf-8")
        windows = (ROOT / "tools/install_libreoffice_windows.ps1").read_text(encoding="utf-8")
        macos = (ROOT / "tools/install_libreoffice_macos.sh").read_text(encoding="utf-8")

        self.assertIn("os: [ubuntu-latest, windows-latest, macos-latest]", workflow)
        self.assertIn("tools/install_libreoffice_windows.ps1", workflow)
        self.assertIn("tools/install_libreoffice_macos.sh", workflow)
        self.assertIn("tools/run_libreoffice_corpus.py", workflow)
        self.assertIn("build_libreoffice_ci_summary.py", workflow)
        self.assertIn("build_application_import_ci_summary.py", workflow)
        self.assertIn("real production queue import reports per-format intake and renderer outcomes", (ROOT / "tests/PaperBatch.Desktop.Tests/Program.cs").read_text(encoding="utf-8"))
        app_import_test = (ROOT / "tests/PaperBatch.Desktop.Tests/ApplicationImportEvidenceTests.cs").read_text(encoding="utf-8")
        self.assertIn("new ProductionPresentationSources", app_import_test)
        self.assertIn("presentation.ImportAsync", app_import_test)
        self.assertIn("name: synthetic-ooxml-libreoffice-report-${{ matrix.os }}", workflow)
        self.assertIn("name: paperbatch-application-import-${{ runner.os }}", workflow)
        self.assertNotIn("runner.os == 'Linux'\n        shell: bash\n        run:\n          report=\"$RUNNER_TEMP/paperbatch-lo-corpus-report.json\"", workflow)
        self.assertNotIn("releases/latest", windows)
        self.assertIn("downloadarchive.documentfoundation.org/libreoffice/old/$version/win/x86_64", windows)
        self.assertIn("downloadarchive.documentfoundation.org/libreoffice/old/${version}/mac/${architecture}", macos)
        self.assertIn("tools/resolve_macos_libreoffice_engine.py", macos)
        self.assertNotIn("Contents/MacOS/soffice.bin", macos)
        self.assertLess(macos.index("codesign --verify --deep --strict"), macos.index("resolve_macos_libreoffice_engine.py"))
        self.assertIn("26.2.2.2", windows)
        self.assertIn("26.2.2.2", macos)

    def test_vendor_signatures_are_verified_before_each_installer_runs(self):
        workflow = (ROOT / ".github/workflows/ci.yml").read_text(encoding="utf-8")
        windows = (ROOT / "tools/install_libreoffice_windows.ps1").read_text(encoding="utf-8")
        macos = (ROOT / "tools/install_libreoffice_macos.sh").read_text(encoding="utf-8")

        self.assertIn("gpgv --status-fd=1 --keyring", workflow)
        self.assertIn("aa2815afe97e45dc02e446c43e24232f1d78941aee1081c95d871e05d74062e8", workflow)
        self.assertLess(workflow.index("gpgv --status-fd=1"), workflow.index('tar -xzf "$lo_archive_path"'))
        self.assertIn("C2839ECAD9408FBE9531C3E9F434A1EFAFEEAEA3", windows)
        self.assertLess(windows.index("$gpgv --homedir"), windows.index("Start-Process -FilePath 'msiexec.exe'"))
        self.assertIn("C2839ECAD9408FBE9531C3E9F434A1EFAFEEAEA3", macos)
        self.assertLess(macos.index("gpgv --status-fd=1"), macos.index("hdiutil attach"))

    def test_windows_signature_verifier_distinguishes_exit_and_identity_failures(self):
        windows = (ROOT / "tools/install_libreoffice_windows.ps1").read_text(encoding="utf-8")

        self.assertIn("$verificationExitCode = $LASTEXITCODE", windows)
        self.assertRegex(windows, r"(?s)if \(\$verificationExitCode -ne 0\).*?verifier exited with code", msg=windows)
        self.assertIn("signature identity mismatch; expected $fingerprint, observed $observedText", windows)
        self.assertIn("^\\[GNUPG:\\] VALIDSIG $fingerprint ", windows)
        self.assertIn("$details.Substring(0, 600)", windows)
        self.assertIn("$observedText.Substring(0, 200)", windows)
        self.assertLess(windows.index("if ($verificationExitCode -ne 0)"), windows.index("Start-Process -FilePath 'msiexec.exe'"))

    def test_windows_gpgv_uses_msys_home_path_and_relative_keyring_in_private_home(self):
        windows = (ROOT / "tools/install_libreoffice_windows.ps1").read_text(encoding="utf-8")

        self.assertIn("$keyringHome = Join-Path $env:RUNNER_TEMP 'paperbatch-gpg-home'", windows)
        self.assertIn("$keyringName = 'tdf-release-key.gpg'", windows)
        self.assertIn("$keyring = Join-Path $keyringHome $keyringName", windows)
        self.assertIn("New-Item -ItemType Directory -Path $keyringHome -Force", windows)
        self.assertIn("$cygpath = Join-Path (Split-Path $gpg) 'cygpath.exe'", windows)
        self.assertIn("$keyringHomeForGpg = (& $cygpath -u $keyringHome | Out-String).Trim()", windows)
        self.assertIn("$keyringHomeForGpg -notmatch '^/(?:[A-Za-z]|cygdrive/[A-Za-z])/[^:\\r\\n]*$'", windows)
        self.assertIn("--homedir $keyringHomeForGpg --status-fd=1 --keyring $keyringName", windows)
        self.assertNotIn("--homedir $keyringHome --status-fd=1", windows)
        self.assertNotIn("--status-fd=1 --keyring $keyring ", windows)
        self.assertLess(windows.index("gpg --batch --yes --dearmor"), windows.index("$gpgv --homedir"))
        self.assertLess(windows.index("$cygpath -u $keyringHome"), windows.index("$gpgv --homedir"))
        self.assertLess(windows.index("$gpgv --homedir"), windows.index("if ($verificationExitCode -ne 0)"))

    def test_macos_engine_resolver_selects_native_main_binary_and_checks_pinned_version(self):
        with tempfile.TemporaryDirectory() as directory:
            app = Path(directory) / "LibreOffice.app"
            engine = app / "Contents" / "MacOS" / "soffice"
            engine.parent.mkdir(parents=True)
            engine.write_bytes(b"fixture Mach-O engine")
            engine.chmod(0o755)

            def run(command, **kwargs):
                if command[0] == "/usr/bin/file":
                    return SimpleNamespace(returncode=0, stdout="Mach-O 64-bit executable arm64", stderr="")
                self.assertEqual(command, [str(engine), "--version"])
                return SimpleNamespace(returncode=0, stdout="LibreOffice 26.2.2.2 Build 2", stderr="")

            resolved = resolve_macos_libreoffice_engine.resolve_engine(app, "26.2.2.2", run=run)

        self.assertEqual(resolved, engine)

    def test_macos_engine_resolver_rejects_wrappers_missing_binaries_and_wrong_versions(self):
        with tempfile.TemporaryDirectory() as directory:
            app = Path(directory) / "LibreOffice.app"
            engine = app / "Contents" / "MacOS" / "soffice"
            engine.parent.mkdir(parents=True)
            engine.write_text("#!/bin/sh\nexec ./soffice.bin\n", encoding="utf-8")
            engine.chmod(0o755)

            wrapper_probe = mock.Mock(return_value=SimpleNamespace(returncode=0, stdout="POSIX shell script", stderr=""))
            with self.assertRaisesRegex(RuntimeError, "not a native Mach-O"):
                resolve_macos_libreoffice_engine.resolve_engine(app, "26.2.2.2", run=wrapper_probe)
            wrapper_probe.assert_called_once()

            engine.unlink()
            with self.assertRaisesRegex(RuntimeError, "missing executable"):
                resolve_macos_libreoffice_engine.resolve_engine(app, "26.2.2.2", run=mock.Mock())

            engine.write_bytes(b"fixture Mach-O engine")
            engine.chmod(0o755)
            def wrong_version(command, **kwargs):
                if command[0] == "/usr/bin/file":
                    return SimpleNamespace(returncode=0, stdout="Mach-O 64-bit executable arm64", stderr="")
                return SimpleNamespace(returncode=0, stdout="LibreOffice 26.8.1 Build 1", stderr="")

            with self.assertRaisesRegex(RuntimeError, "did not report pinned version 26.2.2.2"):
                resolve_macos_libreoffice_engine.resolve_engine(app, "26.2.2.2", run=wrong_version)

            def shared_prefix_version(command, **kwargs):
                if command[0] == "/usr/bin/file":
                    return SimpleNamespace(returncode=0, stdout="Mach-O 64-bit executable arm64", stderr="")
                return SimpleNamespace(returncode=0, stdout="LibreOffice 26.2.2.20 Build 1", stderr="")

            with self.assertRaisesRegex(RuntimeError, "did not report pinned version 26.2.2.2"):
                resolve_macos_libreoffice_engine.resolve_engine(app, "26.2.2.2", run=shared_prefix_version)

    def test_summary_fails_closed_for_missing_report_and_keeps_exact_run_context(self):
        context = {
            "PB_SOURCE_COMMIT": "a" * 40,
            "GITHUB_WORKFLOW": "CI",
            "GITHUB_RUN_ID": "123",
            "GITHUB_RUN_ATTEMPT": "2",
            "RUNNER_OS": "macOS",
            "RUNNER_NAME": "GitHub Actions 1",
            "ImageOS": "macos-15",
            "ImageVersion": "20261020.1",
        }
        with tempfile.TemporaryDirectory() as directory, mock.patch.dict(os.environ, context, clear=True):
            rendered, status = build_libreoffice_ci_summary.render(Path(directory) / "missing.json")

        self.assertEqual(status, 1)
        self.assertIn("attempt `2`", rendered)
        self.assertIn("macos-15", rendered)
        self.assertIn("not a pass", rendered)
        self.assertIn("Microsoft Office/WPS compatibility", rendered)
        self.assertLessEqual(len(rendered.encode("utf-8")), build_libreoffice_ci_summary.MAX_SUMMARY_BYTES)

    def test_summary_requires_exact_run_and_verified_installer_provenance(self):
        context = {
            "PB_SOURCE_COMMIT": "a" * 40, "GITHUB_WORKFLOW": "CI", "GITHUB_RUN_ID": "123",
            "GITHUB_RUN_ATTEMPT": "2", "RUNNER_OS": "Windows", "RUNNER_NAME": "Hosted Agent",
            "ImageOS": "win22", "ImageVersion": "20261020.1",
        }
        report = {
            "status": "passed", "commit": "a" * 40,
            "runIdentity": {"workflow": "CI", "runId": "123", "runAttempt": "2"},
            "engine": {"version": "LibreOffice 26.2.2.2", "binarySha256": "a" * 64},
            "run": {"os": "Windows", "runner": "Hosted Agent", "runnerImage": "win22", "runnerImageVersion": "20261020.1", "installer": {
                "version": "26.2.2.2",
                "status": "verified", "package": "pinned.msi", "packageSha256": "b" * 64,
                "sourceUrl": "https://downloadarchive.documentfoundation.org/pinned.msi",
                "integrityMethod": "OpenPGP signature", "signingKeyFingerprint": "C2839ECAD9408FBE9531C3E9F434A1EFAFEEAEA3",
            }},
            "counts": {"syntheticSourceSpecimens": 1, "selectedForLibreOffice": 1,
                       "successfulLibreOfficeConversions": 1, "failedLibreOfficeConversions": 0},
            "results": [{"inputId": "docx_variant_000", "status": "converted", "exitCode": 0,
                         "timedOut": False, "outputPdfBytes": 100, "outputPdfSha256": "c" * 64,
                         "pageInspection": {"status": "structural-parser", "structureValid": True, "pageCount": None}}],
        }
        with tempfile.TemporaryDirectory() as directory, mock.patch.dict(os.environ, context, clear=True):
            path = Path(directory) / "report.json"
            path.write_text(json.dumps(report), encoding="utf-8")
            rendered, status = build_libreoffice_ci_summary.render(path)
            report["runIdentity"]["runId"] = "999"
            path.write_text(json.dumps(report), encoding="utf-8")
            rejected, rejected_status = build_libreoffice_ci_summary.render(path)

        self.assertEqual(status, 0)
        self.assertIn("LibreOffice 26.2.2.2", rendered)
        self.assertIn("pinned.msi", rendered)
        self.assertIn("downloadarchive.documentfoundation.org", rendered)
        self.assertIn("docx_variant_000", rendered)
        self.assertEqual(rejected_status, 1)
        self.assertIn("not a pass", rejected)

    def test_summary_rejects_oversized_machine_report(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "report.json"
            path.write_bytes(b" " * (build_libreoffice_ci_summary.MAX_REPORT_BYTES + 1))
            with mock.patch.dict(os.environ, {}, clear=True):
                rendered, status = build_libreoffice_ci_summary.render(path)

        self.assertEqual(status, 1)
        self.assertIn("exceeds the report byte limit", rendered)

    def test_summary_fails_closed_on_malformed_nested_run_identity(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "report.json"
            path.write_text(json.dumps({"runIdentity": None}), encoding="utf-8")
            rendered, status = build_libreoffice_ci_summary.render(path)

        self.assertEqual(status, 1)
        self.assertIn("invalid schema", rendered)
        self.assertIn("not a pass", rendered)

    def test_application_import_summary_requires_exact_expected_outcomes_and_run_identity(self):
        context = {
            "PB_SOURCE_COMMIT": "a" * 40, "GITHUB_WORKFLOW": "CI", "GITHUB_RUN_ID": "123",
            "GITHUB_RUN_ATTEMPT": "2", "RUNNER_OS": "macOS", "RUNNER_NAME": "Hosted Agent",
            "ImageOS": "macos-15", "ImageVersion": "20261020.1",
        }
        expectations = {
            "synthetic_two_page.pdf": ("pdf", "accepted", "Ready to prepare · Pdf", "Succeeded", None),
            "synthetic_one_pixel.png": ("png", "accepted", "Ready to prepare · Png", "Succeeded", None),
            "synthetic_sections_table.docx": ("docx", "unsupported", "Unsupported format", "Unsupported", None),
            "synthetic_mixed_page_sizes.xlsx": ("xlsx", "unsupported", "Unsupported format", "Unsupported", None),
            "synthetic_two_slides.pptx": ("pptx", "unsupported", "Unsupported format", "Unsupported", None),
            "synthetic_truncated_package.docx": ("docx", "rejected", "Needs attention", None, "MalformedContainer"),
        }
        outcomes = [
            {"fixture": name, "format": fmt, "applicationOutcome": result, "queueStatus": queue,
             "rendererStatus": renderer, "intakeDiagnostic": diagnostic, "sha256": "b" * 64}
            for name, (fmt, result, queue, renderer, diagnostic) in expectations.items()
        ]
        report = {
            "schemaVersion": 1, "evidenceType": "paperbatch-production-queue-import", "sourceCommit": "a" * 40,
            "workflow": "CI", "runId": "123", "runAttempt": "2", "runnerOs": "macOS", "runnerImage": "macos-15",
            "outcomes": outcomes,
            "evidenceLimits": [
                "production queue import", "standalone LibreOffice conversion does not establish PaperBatch import support",
                "No Office/WPS compatibility claim", "synthetic inputs only",
            ],
        }
        with tempfile.TemporaryDirectory() as directory, mock.patch.dict(os.environ, context, clear=True):
            path = Path(directory) / "report.json"
            path.write_text(json.dumps(report), encoding="utf-8")
            rendered, status = build_application_import_ci_summary.render(path)
            self.assertEqual(status, 0)
            self.assertIn("production queue-import path", rendered)
            self.assertIn("synthetic_sections_table.docx", rendered)
            self.assertIn("Standalone LibreOffice conversion does not establish PaperBatch import support", rendered)
            report["runId"] = "999"
            path.write_text(json.dumps(report), encoding="utf-8")
            mismatched, mismatched_status = build_application_import_ci_summary.render(path)
            report["runId"] = "123"
            report["outcomes"][2]["rendererStatus"] = "Succeeded"
            path.write_text(json.dumps(report), encoding="utf-8")
            rejected, rejected_status = build_application_import_ci_summary.render(path)
            report["outcomes"][2]["rendererStatus"] = "Unsupported"
            report["outcomes"][0]["fixture"] = []
            path.write_text(json.dumps(report), encoding="utf-8")
            malformed, malformed_status = build_application_import_ci_summary.render(path)

        self.assertEqual(rejected_status, 1)
        self.assertIn("not a pass", rejected)
        self.assertEqual(mismatched_status, 1)
        self.assertIn("not a pass", mismatched)
        self.assertEqual(malformed_status, 1)
        self.assertIn("not a pass", malformed)


if __name__ == "__main__":
    unittest.main()
