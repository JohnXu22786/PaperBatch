from pathlib import Path
import unittest


ROOT = Path(__file__).resolve().parents[1]
PINNED_SHA256 = "3452d11031050ec65e57f180215b57b9e6d465e3fbedafa8a77ad90885c7d23e"
ASSET_URL = "https://github.com/bblanchon/pdfium-binaries/releases/download/chromium/8111/pdfium-win-x64.tgz"


class WindowsPdfiumCiTests(unittest.TestCase):
    def test_release_url_and_digest_are_pinned_in_runner_and_harness(self):
        runner = (ROOT / "tools/run_windows_pdfium_community_proof.ps1").read_text()
        harness = (ROOT / "tests/PaperBatch.Pdfium.Windows.Tests/PdfiumSmoke.cs").read_text()
        fixture = (ROOT / "tests/PaperBatch.Pdfium.Windows.Tests/SyntheticPdf.cs").read_text()

        self.assertIn(PINNED_SHA256, runner)
        self.assertIn(PINNED_SHA256, fixture)
        self.assertIn(ASSET_URL, runner)
        self.assertIn(ASSET_URL, harness)
        self.assertNotIn("releases/latest", runner)
        self.assertNotIn("releases/latest", harness)

    def test_archive_digest_is_checked_before_extraction_and_native_load(self):
        runner = (ROOT / "tools/run_windows_pdfium_community_proof.ps1").read_text()
        harness = (ROOT / "tests/PaperBatch.Pdfium.Windows.Tests/PdfiumSmoke.cs").read_text()

        self.assertLess(runner.index("if ($archiveHash -ne $expectedSha256)"), runner.index("Expand-SafeArchive -Archive"))
        self.assertLess(harness.index("string.Equals(archiveSha256, SyntheticPdf.ExpectedReleaseSha256"), harness.index("NativeLibrary.Load(fullDllPath)"))

    def test_proof_report_keeps_product_and_other_native_requirements_open(self):
        report = (ROOT / "tests/PaperBatch.Pdfium.Windows.Tests/PdfiumSmoke.cs").read_text()
        runner = (ROOT / "tools/run_windows_pdfium_community_proof.ps1").read_text()

        self.assertIn('g1 = "open"', report)
        self.assertIn("productDistributionValidated = false", report)
        self.assertIn("productDistributionValidated = $false", runner)
        self.assertIn("Microsoft Office or WPS coexistence", runner)
        self.assertIn("physical printer output", runner)


if __name__ == "__main__":
    unittest.main()
