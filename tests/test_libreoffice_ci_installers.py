from __future__ import annotations

import os
import json
import subprocess
import sys
import tempfile
import unittest
from contextlib import redirect_stderr
from io import StringIO
from pathlib import Path
from types import SimpleNamespace
from unittest import mock

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / "tools"))
import build_libreoffice_ci_summary
import build_application_import_ci_summary
import resolve_macos_libreoffice_engine
import capture_bounded_command_output


class LibreOfficeCiInstallerTests(unittest.TestCase):
    def test_import_summary_emits_escaped_bounded_failure_annotation(self):
        with tempfile.TemporaryDirectory() as directory:
            report = Path(directory) / "report.json"
            summary = Path(directory) / "summary.md"
            test_output = Path(directory) / "test-output.txt"
            report.write_text(json.dumps({"failure": "import failed\n::warning::spoof%detail"}), encoding="utf-8")
            test_output.write_text(
                "noise line\nLibreOffice runtime capability check rejected: UnauthorizedAccessException: unsafe runtime ancestor\n",
                encoding="utf-8",
            )
            output = StringIO()
            with mock.patch.dict(os.environ, {
                    "GITHUB_STEP_SUMMARY": str(summary),
                    "PAPERBATCH_DESKTOP_TEST_OUTPUT": str(test_output),
                }), \
                    mock.patch.object(sys, "argv", ["build_application_import_ci_summary.py", str(report)]), \
                    redirect_stderr(output):
                self.assertEqual(build_application_import_ci_summary.main(), 1)
            self.assertIn(
                "::error::import failed ::warning::spoof%25detail LibreOffice diagnostic: "
                "LibreOffice runtime capability check rejected: UnauthorizedAccessException: unsafe runtime ancestor",
                output.getvalue(),
            )
            self.assertIn("Import test stopped:", summary.read_text(encoding="utf-8"))

    def test_bounded_command_output_keeps_tail_and_preserves_exit_status(self):
        with tempfile.TemporaryDirectory() as directory:
            output_path = Path(directory) / "bounded-output.txt"
            result = subprocess.run(
                [sys.executable, str(ROOT / "tools/capture_bounded_command_output.py"),
                 "--output", str(output_path), "--max-bytes", "32", "--",
                 sys.executable, "-c", "import sys; print('x' * 100); print('failure marker'); sys.exit(7)"],
                capture_output=True, check=False,
            )
            self.assertEqual(result.returncode, 7)
            self.assertIn(b"failure marker", result.stdout)
            bounded = output_path.read_bytes()
            self.assertLessEqual(len(bounded), 32)
            self.assertTrue(bounded.endswith(b"failure marker\n"))

    def test_general_matrix_keeps_import_evidence_and_print_contract_is_windows_only(self):
        workflow = (ROOT / ".github/workflows/ci.yml").read_text(encoding="utf-8")
        windows = (ROOT / "tools/install_libreoffice_windows.ps1").read_text(encoding="utf-8")
        macos = (ROOT / "tools/install_libreoffice_macos.sh").read_text(encoding="utf-8")

        self.assertIn("os: [ubuntu-latest, windows-latest, macos-latest]", workflow)
        self.assertIn("Install pinned LibreOffice on hosted Linux", workflow)
        self.assertIn("Install pinned LibreOffice on hosted Windows", workflow)
        self.assertIn("${RUNNER_ENVIRONMENT:-}\" == 'github-hosted'", (ROOT / "tools/install_libreoffice_linux.sh").read_text(encoding="utf-8"))
        self.assertIn("$env:RUNNER_ENVIRONMENT -ne 'github-hosted'", windows)
        self.assertNotIn("run_libreoffice_corpus.py", workflow)
        self.assertNotIn("synthetic-ooxml-libreoffice-report", workflow)
        self.assertIn("build_application_import_ci_summary.py", workflow)
        self.assertIn("install_libreoffice_linux.sh", workflow)
        self.assertIn("PB_JOB_NAME: Windows batch print contract", workflow)
        self.assertIn("${{ runner.os == 'Windows' && format('{0}/windows-batch-print-contract.json', runner.temp) || '' }}", workflow)
        self.assertIn("name: windows-batch-print-contract", workflow)
        self.assertIn("real production queue import reports per-format intake and renderer outcomes", (ROOT / "tests/PaperBatch.Desktop.Tests/Program.cs").read_text(encoding="utf-8"))
        app_import_test = (ROOT / "tests/PaperBatch.Desktop.Tests/ApplicationImportEvidenceTests.cs").read_text(encoding="utf-8")
        self.assertIn("new ProductionPresentationSources", app_import_test)
        self.assertIn("presentation.ImportAsync", app_import_test)
        self.assertIn("name: paperbatch-application-import-${{ runner.os }}", workflow)
        self.assertIn("Upload bounded focused desktop test output", workflow)
        self.assertIn("name: paperbatch-desktop-test-output-${{ runner.os }}", workflow)
        self.assertNotIn("releases/latest", windows)
        self.assertIn("downloadarchive.documentfoundation.org/libreoffice/old/$version/win/x86_64", windows)
        self.assertIn("downloadarchive.documentfoundation.org/libreoffice/old/${version}/mac/${architecture}", macos)
        self.assertIn("tools/resolve_macos_libreoffice_engine.py", macos)
        self.assertNotIn("Contents/MacOS/soffice.bin", macos)
        self.assertLess(macos.index("codesign --verify --deep --strict"), macos.index("resolve_macos_libreoffice_engine.py"))
        self.assertIn("26.2.2.2", windows)
        self.assertIn("$engine = Join-Path $installRoot 'program\\soffice.bin'", windows)
        self.assertIn("$launcher = Join-Path $installRoot 'program\\soffice.com'", windows)
        self.assertIn("$engineHash = (Get-FileHash -LiteralPath $engine -Algorithm SHA256).Hash.ToLowerInvariant()", windows)
        self.assertIn("26.2.2.2", macos)
        linux = (ROOT / "tools/install_libreoffice_linux.sh").read_text(encoding="utf-8")
        self.assertIn("gpgv --status-fd=1", linux)
        self.assertIn("sudo chmod +t /opt", linux)
        self.assertIn("sudo apt-get install -y util-linux", linux)
        self.assertIn("command -v prlimit", linux)
        self.assertIn("engine='/opt/libreoffice26.2/program/soffice.bin'", linux)
        self.assertIn("launcher='/opt/libreoffice26.2/program/soffice'", linux)
        self.assertNotIn("cp -a", linux)

    def test_windows_installer_version_check_requires_a_token_boundary(self):
        windows = (ROOT / "tools/install_libreoffice_windows.ps1").read_text(encoding="utf-8")

        self.assertIn("$versionPattern = '^LibreOffice\\s+' + [regex]::Escape($version) + '(?=\\s|$)'", windows)
        self.assertIn('$versionOutput -notmatch $versionPattern', windows)

    def test_only_windows_pdfium_fixture_runtime_is_cleaned_up(self):
        workflow = (ROOT / ".github/workflows/ci.yml").read_text(encoding="utf-8")
        cleanup = workflow.index("Remove temporary PDFium test runtime")
        self.assertIn("if: always() && runner.os == 'Windows'", workflow[cleanup:cleanup + 150])
        self.assertIn("Install pinned LibreOffice on hosted Linux", workflow)
        self.assertIn("Install pinned LibreOffice on hosted Windows", workflow)
        self.assertNotIn("paperbatch-lo-corpus-report", workflow)

    def test_vendor_signature_checks_remain_in_the_standalone_installer_scripts(self):
        workflow = (ROOT / ".github/workflows/ci.yml").read_text(encoding="utf-8")
        windows = (ROOT / "tools/install_libreoffice_windows.ps1").read_text(encoding="utf-8")
        macos = (ROOT / "tools/install_libreoffice_macos.sh").read_text(encoding="utf-8")

        self.assertNotIn("gpgv --status-fd=1", workflow)
        self.assertNotIn("aa2815afe97e45dc02e446c43e24232f1d78941aee1081c95d871e05d74062e8", workflow)
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
            expected_engine = engine.resolve(strict=True)

            def run(command, **kwargs):
                if command[0] == "/usr/bin/file":
                    return SimpleNamespace(returncode=0, stdout="Mach-O 64-bit executable arm64", stderr="")
                self.assertEqual(Path(command[0]).resolve(strict=True), expected_engine)
                self.assertEqual(command[1:], ["--version"])
                return SimpleNamespace(returncode=0, stdout="LibreOffice 26.2.2.2 Build 2", stderr="")

            resolved = resolve_macos_libreoffice_engine.resolve_engine(app, "26.2.2.2", run=run)
            self.assertEqual(resolved.resolve(strict=True), expected_engine)

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
            "engine": {"version": "LibreOffice 26.2.2.2 Build 2", "binarySha256": "a" * 64},
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
            report["engine"]["version"] = "LibreOffice 26.2.2.20 Build 2"
            path.write_text(json.dumps(report), encoding="utf-8")
            wrong_version, wrong_version_status = build_libreoffice_ci_summary.render(path)
            report["engine"]["version"] = "LibreOffice 26.2.2.2 Build 2"
            report["runIdentity"]["runId"] = "999"
            path.write_text(json.dumps(report), encoding="utf-8")
            rejected, rejected_status = build_libreoffice_ci_summary.render(path)

        self.assertEqual(status, 0)
        self.assertIn("LibreOffice 26.2.2.2 Build 2", rendered)
        self.assertIn("pinned.msi", rendered)
        self.assertIn("downloadarchive.documentfoundation.org", rendered)
        self.assertIn("docx_variant_000", rendered)
        self.assertEqual(wrong_version_status, 1)
        self.assertIn("not a pass", wrong_version)
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
            "synthetic_two_page.pdf": ("pdf", "accepted", "Artifact available · Pdf", "Succeeded", None),
            "synthetic_one_pixel.png": ("png", "accepted", "Artifact available · Png", "Succeeded", None),
            "synthetic_sections_table.docx": ("docx", "unsupported", "Unsupported format", "Unsupported", None),
            "synthetic_mixed_page_sizes.xlsx": ("xlsx", "unsupported", "Unsupported format", "Unsupported", None),
            "synthetic_two_slides.pptx": ("pptx", "unsupported", "Unsupported format", "Unsupported", None),
            "synthetic_text.rtf": ("rtf", "unsupported", "Unsupported format", "Unsupported", None),
            "synthetic_text.odt": ("odt", "unsupported", "Unsupported format", "Unsupported", None),
            "synthetic_table.ods": ("ods", "unsupported", "Unsupported format", "Unsupported", None),
            "synthetic_slide.odp": ("odp", "unsupported", "Unsupported format", "Unsupported", None),
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
            "outcomes": outcomes, "preparationAvailable": False,
            "evidenceLimits": [
                "production queue import", "PDFium remains separately required for page inspection, composition, and batch preparation",
                "No Microsoft Office/WPS equivalence or visual-fidelity evidence", "synthetic inputs only",
            ],
        }
        with tempfile.TemporaryDirectory() as directory, mock.patch.dict(os.environ, context, clear=True):
            path = Path(directory) / "report.json"
            path.write_text(json.dumps(report), encoding="utf-8")
            rendered, status = build_application_import_ci_summary.render(path)
            self.assertEqual(status, 0)
            self.assertIn("production queue-import path", rendered)
            self.assertIn("without a configured LibreOffice renderer", rendered)
            self.assertIn("document formats remained unsupported", rendered)
            self.assertIn("synthetic_sections_table.docx", rendered)
            self.assertIn("not Microsoft Office/WPS equivalence", rendered)
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

    def test_application_import_summary_reports_actual_pinned_release_identity_suffix(self):
        context = {
            "PB_SOURCE_COMMIT": "a" * 40, "GITHUB_WORKFLOW": "CI", "GITHUB_RUN_ID": "123",
            "GITHUB_RUN_ATTEMPT": "2", "RUNNER_OS": "Linux", "RUNNER_NAME": "Hosted Agent",
            "ImageOS": "ubuntu-24.04", "ImageVersion": "20261020.1",
            "PAPERBATCH_LO_EXPECTED_VERSION": "LibreOffice 26.2.2.2", "PAPERBATCH_LO_ENGINE_SHA256": "c" * 64,
        }
        outcomes = [
            {"fixture": "synthetic_two_page.pdf", "format": "pdf", "applicationOutcome": "accepted",
             "queueStatus": "Artifact available · Pdf", "rendererStatus": "Succeeded", "sha256": "1" * 64},
            {"fixture": "synthetic_one_pixel.png", "format": "png", "applicationOutcome": "accepted",
             "queueStatus": "Artifact available · Png", "rendererStatus": "Succeeded", "sha256": "2" * 64},
        ]
        for fixture, fmt in (("synthetic_sections_table.docx", "docx"), ("synthetic_mixed_page_sizes.xlsx", "xlsx"),
                             ("synthetic_two_slides.pptx", "pptx"), ("synthetic_text.rtf", "rtf"),
                             ("synthetic_text.odt", "odt"), ("synthetic_table.ods", "ods"),
                             ("synthetic_slide.odp", "odp")):
            outcomes.append({"fixture": fixture, "format": fmt, "applicationOutcome": "converted",
                             "queueStatus": "Artifact available · LibreOffice", "rendererStatus": "Succeeded", "sha256": "b" * 64})
        outcomes.append({"fixture": "synthetic_truncated_package.docx", "format": "docx", "applicationOutcome": "rejected",
                         "queueStatus": "Needs attention", "rendererStatus": None, "intakeDiagnostic": "MalformedContainer", "sha256": "b" * 64})
        report = {
            "schemaVersion": 1, "evidenceType": "paperbatch-production-queue-import", "sourceCommit": "a" * 40,
            "workflow": "CI", "runId": "123", "runAttempt": "2", "runnerOs": "Linux", "runnerImage": "ubuntu-24.04",
            "libreOffice": {"version": "LibreOffice 26.2.2.2 Build 2", "engineSha256": "c" * 64},
            "outcomes": outcomes, "preparationAvailable": False,
            "evidenceLimits": ["PDFium remains separately required", "No Microsoft Office/WPS equivalence", "synthetic inputs", "no physical output", "Limits: 512 MiB per source and final PDF; Linux RLIMIT_FSIZE is a hard per-file cap; Windows output monitoring is best-effort and may overshoot before the final 512 MiB check; neither OS caps aggregate profile/cache/temp storage. One conversion runs at a time across processes sharing the same per-user PaperBatch data root; timeout or cancellation stops and reaps the owned process tree before operation-directory cleanup."],
        }
        with tempfile.TemporaryDirectory() as directory, mock.patch.dict(os.environ, context, clear=True):
            path = Path(directory) / "report.json"
            path.write_text(json.dumps(report), encoding="utf-8")
            rendered, status = build_application_import_ci_summary.render(path)
            report["libreOffice"]["version"] = "LibreOffice 26.2.2.2 1f77d10d6938fd34972958f64b2bcfa54f8b1ba5"
            path.write_text(json.dumps(report), encoding="utf-8")
            release_hash_version, release_hash_status = build_application_import_ci_summary.render(path)
            report["libreOffice"]["version"] = "LibreOffice 26.2.2.20 Build 2"
            path.write_text(json.dumps(report), encoding="utf-8")
            wrong_version, wrong_version_status = build_application_import_ci_summary.render(path)
            report["libreOffice"]["version"] = "LibreOffice 26.2.2.2 unrelated"
            path.write_text(json.dumps(report), encoding="utf-8")
            unrelated_suffix, unrelated_suffix_status = build_application_import_ci_summary.render(path)
            report["libreOffice"]["version"] = "LibreOffice 26.2.2.2 "
            path.write_text(json.dumps(report), encoding="utf-8")
            trailing_space, trailing_space_status = build_application_import_ci_summary.render(path)
            report["libreOffice"]["version"] = "LibreOffice 26.2.2.2 Build 2"
            report["evidenceLimits"] = ["PDFium remains separately required", "No Microsoft Office/WPS equivalence"]
            path.write_text(json.dumps(report), encoding="utf-8")
            missing_limits, missing_limits_status = build_application_import_ci_summary.render(path)

        self.assertEqual(status, 0)
        self.assertIn("LibreOffice 26.2.2.2 Build 2", rendered)
        self.assertEqual(release_hash_status, 0)
        self.assertIn("LibreOffice 26.2.2.2 1f77d10d6938fd34972958f64b2bcfa54f8b1ba5", release_hash_version)
        self.assertIn("with the configured pinned LibreOffice renderer", rendered)
        self.assertIn("supported document formats produced persisted artifacts", rendered)
        self.assertEqual(wrong_version_status, 1)
        self.assertIn("not a pass", wrong_version)
        self.assertEqual(unrelated_suffix_status, 1)
        self.assertIn("not a pass", unrelated_suffix)
        self.assertEqual(trailing_space_status, 1)
        self.assertIn("not a pass", trailing_space)
        self.assertEqual(missing_limits_status, 1)
        self.assertIn("not a pass", missing_limits)

    def test_application_import_summary_rejects_malformed_windows_prepared_artifact(self):
        context = {
            "PB_SOURCE_COMMIT": "a" * 40, "GITHUB_WORKFLOW": "CI", "GITHUB_RUN_ID": "123",
            "GITHUB_RUN_ATTEMPT": "2", "RUNNER_OS": "Windows", "RUNNER_NAME": "Hosted Agent",
            "ImageOS": "win22", "ImageVersion": "20261020.1",
            "PAPERBATCH_LO_EXPECTED_VERSION": "LibreOffice 26.2.2.2", "PAPERBATCH_LO_ENGINE_SHA256": "c" * 64,
        }
        outcomes = [
            {"fixture": "synthetic_two_page.pdf", "format": "pdf", "applicationOutcome": "accepted",
             "queueStatus": "Artifact available · Pdf", "rendererStatus": "Succeeded", "sha256": "b" * 64},
            {"fixture": "synthetic_one_pixel.png", "format": "png", "applicationOutcome": "accepted",
             "queueStatus": "Artifact available · Png", "rendererStatus": "Succeeded", "sha256": "b" * 64},
        ]
        for fixture, fmt in (("synthetic_sections_table.docx", "docx"), ("synthetic_mixed_page_sizes.xlsx", "xlsx"),
                             ("synthetic_two_slides.pptx", "pptx"), ("synthetic_text.rtf", "rtf"),
                             ("synthetic_text.odt", "odt"), ("synthetic_table.ods", "ods"),
                             ("synthetic_slide.odp", "odp")):
            outcomes.append({"fixture": fixture, "format": fmt, "applicationOutcome": "converted",
                             "queueStatus": "Artifact available · LibreOffice", "rendererStatus": "Succeeded", "sha256": f"{len(outcomes) + 1:x}" * 64})
        outcomes.append({"fixture": "synthetic_truncated_package.docx", "format": "docx", "applicationOutcome": "rejected",
                         "queueStatus": "Needs attention", "rendererStatus": None, "intakeDiagnostic": "MalformedContainer", "sha256": "a" * 64})
        mixed = {"sourceCount": 9, "segmentCount": 1, "jobCount": 1, "fakeBackendSubmissions": 1,
                 "replaySubmissions": 0,
                 "sourceOrder": [item["sha256"] for item in outcomes if item["applicationOutcome"] in ("accepted", "converted")],
                 "preparedArtifact": {"sha256": "d" * 64, "lengthBytes": 12345, "pageCount": 9}}
        report = {
            "schemaVersion": 1, "evidenceType": "paperbatch-production-queue-import", "sourceCommit": "a" * 40,
            "workflow": "CI", "runId": "123", "runAttempt": "2", "runnerOs": "Windows", "runnerImage": "win22",
            "libreOffice": {"version": "LibreOffice 26.2.2.2 Build 2", "engineSha256": "c" * 64},
            "outcomes": outcomes, "preparationAvailable": False, "mixedBatchPreparation": mixed,
            "evidenceLimits": ["PDFium remains separately required", "No Microsoft Office/WPS equivalence", "synthetic inputs", "no physical output", "Limits: 512 MiB per source and final PDF; Linux RLIMIT_FSIZE is a hard per-file cap; Windows output monitoring is best-effort and may overshoot before the final 512 MiB check; neither OS caps aggregate profile/cache/temp storage. One conversion runs at a time across processes sharing the same per-user PaperBatch data root; timeout or cancellation stops and reaps the owned process tree before operation-directory cleanup."],
        }
        with tempfile.TemporaryDirectory() as directory, mock.patch.dict(os.environ, context, clear=True):
            path = Path(directory) / "report.json"
            path.write_text(json.dumps(report), encoding="utf-8")
            valid, valid_status = build_application_import_ci_summary.render(path)
            report["outcomes"] = outcomes[:3]
            report["failure"] = "docx did not become ready; renderer detail was `PDF artifact abc persisted | page count unavailable`."
            path.write_text(json.dumps(report), encoding="utf-8")
            partial, partial_status = build_application_import_ci_summary.render(path)
            report["outcomes"] = outcomes
            report.pop("failure")
            report["mixedBatchPreparation"]["preparedArtifact"] = None
            path.write_text(json.dumps(report), encoding="utf-8")
            null_artifact, null_status = build_application_import_ci_summary.render(path)
            report["mixedBatchPreparation"]["preparedArtifact"] = []
            path.write_text(json.dumps(report), encoding="utf-8")
            list_artifact, list_status = build_application_import_ci_summary.render(path)
            report["mixedBatchPreparation"]["preparedArtifact"] = {"sha256": "d" * 64, "lengthBytes": 12345, "pageCount": 9}
            report["mixedBatchPreparation"]["sourceOrder"] = list(reversed(mixed["sourceOrder"]))
            path.write_text(json.dumps(report), encoding="utf-8")
            wrong_order, wrong_order_status = build_application_import_ci_summary.render(path)
            report["mixedBatchPreparation"]["sourceOrder"] = mixed["sourceOrder"]
            report["mixedBatchPreparation"]["preparedArtifact"]["lengthBytes"] = 0
            path.write_text(json.dumps(report), encoding="utf-8")
            invalid_length, invalid_length_status = build_application_import_ci_summary.render(path)
            report["mixedBatchPreparation"]["preparedArtifact"]["lengthBytes"] = 12345
            report["mixedBatchPreparation"]["preparedArtifact"]["pageCount"] = 0
            path.write_text(json.dumps(report), encoding="utf-8")
            invalid_page_count, invalid_page_count_status = build_application_import_ci_summary.render(path)

        self.assertEqual(valid_status, 0)
        self.assertEqual(partial_status, 1)
        self.assertIn("Import test stopped:", partial)
        self.assertIn("PDF artifact abc persisted \\| page count unavailable", partial)
        self.assertEqual(null_status, 1)
        self.assertIn("not a pass", null_artifact)
        self.assertEqual(list_status, 1)
        self.assertIn("not a pass", list_artifact)
        self.assertEqual(wrong_order_status, 1)
        self.assertIn("not a pass", wrong_order)
        self.assertEqual(invalid_length_status, 1)
        self.assertIn("not a pass", invalid_length)
        self.assertEqual(invalid_page_count_status, 1)
        self.assertIn("not a pass", invalid_page_count)


if __name__ == "__main__":
    unittest.main()
