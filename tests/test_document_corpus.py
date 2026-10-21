"""Regression tests for the deterministic, synthetic document corpus."""

from __future__ import annotations

import json
import re
import sys
import tempfile
import unittest
import zipfile
from pathlib import Path
from xml.etree import ElementTree as ET

REPOSITORY_ROOT = Path(__file__).resolve().parents[1]
CORPUS_DIR = REPOSITORY_ROOT / "tests" / "fixtures" / "synthetic_documents"
sys.path.insert(0, str(REPOSITORY_ROOT / "tools"))
import generate_document_corpus


DOCX_NS = "{http://schemas.openxmlformats.org/wordprocessingml/2006/main}"
SHEET_NS = "{http://schemas.openxmlformats.org/spreadsheetml/2006/main}"
PRESENTATION_NS = "{http://schemas.openxmlformats.org/presentationml/2006/main}"
DRAWING_NS = "{http://schemas.openxmlformats.org/drawingml/2006/main}"
PACKAGE_REL_NS = "{http://schemas.openxmlformats.org/package/2006/relationships}"


class DocumentCorpusTests(unittest.TestCase):
    def test_generation_is_byte_for_byte_deterministic(self) -> None:
        with tempfile.TemporaryDirectory() as first_dir, tempfile.TemporaryDirectory() as second_dir:
            first = Path(first_dir)
            second = Path(second_dir)
            generate_document_corpus.generate_corpus(first)
            generate_document_corpus.generate_corpus(second)

            first_files = {path.relative_to(first): path.read_bytes() for path in first.rglob("*") if path.is_file()}
            second_files = {path.relative_to(second): path.read_bytes() for path in second.rglob("*") if path.is_file()}

        self.assertEqual(first_files, second_files)

    def test_manifest_matches_checked_in_fixture_bytes(self) -> None:
        manifest_path = CORPUS_DIR / "manifest.json"
        manifest_bytes = manifest_path.read_bytes()
        manifest = json.loads(manifest_bytes)

        self.assertEqual(manifest["synthetic"], True)
        self.assertEqual(manifest["schema_version"], 2)
        self.assertGreaterEqual(len(manifest["fixtures"]), 150)
        self.assertEqual(manifest["evidence_class"], "synthetic")
        self.assertTrue(manifest["simulation_only"])
        self.assertEqual(manifest["coverage_target"], {"minimum": 150, "maximum": 300})
        self.assertEqual(manifest["evidence_gaps"]["engine_generated"], "absent")
        self.assertEqual(manifest["evidence_gaps"]["human_reviewed"], "absent")
        self.assertEqual(manifest["evidence_gaps"]["oem_driver"], "absent")
        self.assertEqual(manifest["evidence_gaps"]["physical_output"], "absent")
        identifiers = [fixture["id"] for fixture in manifest["fixtures"]]
        self.assertEqual(len(identifiers), len(set(identifiers)))

        declared_paths = {fixture["path"] for fixture in manifest["fixtures"]}
        actual_paths = {
            path.relative_to(CORPUS_DIR).as_posix()
            for path in CORPUS_DIR.rglob("*")
            if path.is_file() and path.name != "manifest.json"
        }
        self.assertEqual(actual_paths, declared_paths)

        for fixture in manifest["fixtures"]:
            self.assertEqual(fixture["evidence_class"], "synthetic")
            self.assertTrue(fixture["simulation_only"])
            self.assertTrue(fixture["coverage_tags"])
            self.assertIn(fixture["expected_outcome"]["status"], {"accepted", "rejected", "unsupported", "synthetic_only"})
            data = (CORPUS_DIR / fixture["path"]).read_bytes()
            self.assertEqual(len(data), fixture["size_bytes"], fixture["id"])
            self.assertEqual(generate_document_corpus.sha256(data), fixture["sha256"], fixture["id"])
            self.assertIn("synthetic", fixture["scenario"].lower())

        with tempfile.TemporaryDirectory() as regenerated_dir:
            generate_document_corpus.generate_corpus(Path(regenerated_dir))
            regenerated_manifest = (Path(regenerated_dir) / "manifest.json").read_bytes()
        self.assertNotIn(b"\r", manifest_bytes)
        self.assertNotIn(b"\r", regenerated_manifest)
        self.assertEqual(regenerated_manifest, manifest_bytes)

    def test_manifest_has_breadth_without_claiming_real_engine_coverage(self) -> None:
        manifest = json.loads((CORPUS_DIR / "manifest.json").read_text(encoding="utf-8"))
        fixtures = {fixture["id"]: fixture for fixture in manifest["fixtures"]}
        required = {
            "docx_multilingual_fields_forms",
            "docx_annotations",
            "xlsx_mixed_page_sizes",
            "pptx_multilingual_fonts",
            "pdf_annotations_forms",
            "encrypted_package_simulation",
            "legacy_ole_signature_simulation",
            "fault_zip_path_traversal",
            "fault_oversized_member",
            "opc_external_hyperlink_no_network",
        }
        self.assertTrue(required.issubset(fixtures))
        self.assertEqual(manifest["counts"]["synthetic_fixtures"], len(fixtures))
        self.assertEqual(manifest["counts"]["parameter_variants_counted_as_compatibility"], 0)
        self.assertEqual(manifest["counts"]["syntheticFixtureCount"], len(fixtures))
        self.assertEqual(manifest["counts"]["compatibilityEvidenceCount"], 0)
        self.assertEqual(manifest["counts"]["realEngineDeviceEvidenceCount"], 0)
        digests = [fixture["sha256"] for fixture in manifest["fixtures"]]
        self.assertEqual(len(digests), len(set(digests)), "fixtures must differ in content, not just filename")
        self.assertLessEqual(sum(fixture["size_bytes"] for fixture in manifest["fixtures"]), 4 * 1024 * 1024)
        self.assertLessEqual(max(fixture["size_bytes"] for fixture in manifest["fixtures"]), 1024 * 1024 + 1024)
        strata = {fixture["stratum"] for fixture in manifest["fixtures"]}
        self.assertTrue({"docx", "xlsx", "pptx", "pdf", "malformed"}.issubset(strata))
        for fixture_id in required:
            self.assertTrue(fixtures[fixture_id]["coverage_tags"], fixture_id)
        for fixture_id in {"docx_multilingual_fields_forms", "docx_annotations", "xlsx_mixed_page_sizes", "pptx_multilingual_fonts", "pdf_annotations_forms"}:
            self.assertEqual(fixtures[fixture_id]["expected_outcome"]["status"], "synthetic_only", fixture_id)
        for fixture_id in {"fault_zip_path_traversal", "fault_oversized_member"}:
            self.assertEqual(fixtures[fixture_id]["expected_outcome"]["status"], "rejected", fixture_id)
        external = fixtures["opc_external_hyperlink_no_network"]
        self.assertEqual(external["expected_outcome"]["status"], "synthetic_only")
        self.assertFalse(external["expected"]["network_fetch_expected"])

    def test_fault_archives_are_bounded_and_inspected_without_extraction(self) -> None:
        manifest = json.loads((CORPUS_DIR / "manifest.json").read_text(encoding="utf-8"))
        fixtures = {fixture["id"]: fixture for fixture in manifest["fixtures"]}
        traversal = fixtures["fault_zip_path_traversal"]
        with zipfile.ZipFile(CORPUS_DIR / traversal["path"]) as package:
            self.assertIn("../outside.txt", package.namelist())
        oversized = fixtures["fault_oversized_member"]
        with zipfile.ZipFile(CORPUS_DIR / oversized["path"]) as package:
            self.assertEqual(package.getinfo("payload.bin").file_size, 1024 * 1024)
        duplicate = fixtures["fault_duplicate_member"]
        with zipfile.ZipFile(CORPUS_DIR / duplicate["path"]) as package:
            self.assertEqual(package.namelist().count("part.xml"), 2)

    def test_scaled_variants_encode_their_declared_structural_differences(self) -> None:
        manifest = json.loads((CORPUS_DIR / "manifest.json").read_text(encoding="utf-8"))
        fixtures = {fixture["id"]: fixture for fixture in manifest["fixtures"]}
        docx = fixtures["docx_variant_005"]
        with zipfile.ZipFile(CORPUS_DIR / docx["path"]) as package:
            document = ET.fromstring(package.read("word/document.xml"))
        text = " ".join(node.text or "" for node in document.findall(f".//{DOCX_NS}t"))
        self.assertEqual(len(re.findall(r"Case 005 section", text)), docx["expected"]["paragraphs_added"])
        self.assertIn("005", text)

        xlsx = fixtures["xlsx_variant_007"]
        with zipfile.ZipFile(CORPUS_DIR / xlsx["path"]) as package:
            sheet = ET.fromstring(package.read("xl/worksheets/sheet1.xml"))
        self.assertEqual(len(sheet.findall(f".//{SHEET_NS}row")), xlsx["expected"]["data_rows"])
        self.assertEqual(sheet.find(f"{SHEET_NS}dimension").attrib["ref"], f"A1:B{xlsx['expected']['data_rows']}")
        self.assertIn("Case 007 row", " ".join(node.text or "" for node in sheet.findall(f".//{SHEET_NS}t")))
        with zipfile.ZipFile(CORPUS_DIR / xlsx["path"]) as package:
            workbook = ET.fromstring(package.read("xl/workbook.xml"))
        print_area = workbook.find(f".//{SHEET_NS}definedName")
        self.assertEqual(print_area.text, f"Data007!$A$1:$B${xlsx['expected']['print_area_end_row']}")

        pptx = fixtures["pptx_variant_004"]
        with zipfile.ZipFile(CORPUS_DIR / pptx["path"]) as package:
            slide = ET.fromstring(package.read("ppt/slides/slide1.xml"))
        self.assertEqual(len(slide.findall(f".//{DRAWING_NS}br")), pptx["expected"]["additional_text_runs"])

        pdf = (CORPUS_DIR / fixtures["pdf_variant_004"]["path"]).read_bytes()
        self.assertEqual(len(re.findall(rb"/Type\s*/Page\b", pdf)), fixtures["pdf_variant_004"]["expected"]["page_count"])
        self.assertTrue(pdf.startswith(b"%PDF-1.4\n"))
        startxref = re.search(rb"startxref\n(\d+)\n%%EOF", pdf)
        self.assertIsNotNone(startxref)
        self.assertEqual(pdf[int(startxref.group(1)) :].splitlines()[0], b"xref")
        malformed = fixtures["malformed_variant_029"]
        malformed_path = CORPUS_DIR / malformed["path"]
        self.assertEqual(malformed_path.stat().st_size, malformed["expected"]["truncated_at_byte"])
        self.assertFalse(zipfile.is_zipfile(malformed_path))

    def test_expanded_office_cases_use_format_parts_for_declared_features(self) -> None:
        manifest = json.loads((CORPUS_DIR / "manifest.json").read_text(encoding="utf-8"))
        fixtures = {fixture["id"]: fixture for fixture in manifest["fixtures"]}
        with zipfile.ZipFile(CORPUS_DIR / fixtures["docx_multilingual_fields_forms"]["path"]) as package:
            doc = ET.fromstring(package.read("word/document.xml"))
            text = "".join(node.text or "" for node in doc.findall(f".//{DOCX_NS}t"))
            self.assertIn("日本語", text)
            self.assertEqual(len(doc.findall(f".//{DOCX_NS}fldSimple")), 1)
            self.assertEqual(len(doc.findall(f".//{DOCX_NS}sdt")), 1)
        with zipfile.ZipFile(CORPUS_DIR / fixtures["docx_annotations"]["path"]) as package:
            doc = ET.fromstring(package.read("word/document.xml"))
            self.assertEqual(len(doc.findall(f".//{DOCX_NS}commentRangeStart")), 1)
            self.assertEqual(len(doc.findall(f".//{DOCX_NS}commentReference")), 1)
            self.assertIn("word/comments.xml", package.namelist())
            footnotes = ET.fromstring(package.read("word/footnotes.xml"))
            footnote_entries = footnotes.findall(f"{DOCX_NS}footnote")
            self.assertEqual([(entry.attrib[f"{DOCX_NS}id"], entry.attrib.get(f"{DOCX_NS}type")) for entry in footnote_entries], [("-1", "separator"), ("0", "continuationSeparator"), ("1", None)])
        with zipfile.ZipFile(CORPUS_DIR / fixtures["xlsx_mixed_page_sizes"]["path"]) as package:
            sheet_sizes = []
            for sheet_path in ("xl/worksheets/sheet1.xml", "xl/worksheets/sheet2.xml"):
                sheet = ET.fromstring(package.read(sheet_path))
                page_setup = sheet.find(f"{SHEET_NS}pageSetup")
                self.assertIsNotNone(page_setup)
                sheet_sizes.append(page_setup.attrib["paperSize"])
            self.assertEqual(sheet_sizes, ["1", "9"])
        with zipfile.ZipFile(CORPUS_DIR / fixtures["pptx_multilingual_fonts"]["path"]) as package:
            slide = ET.fromstring(package.read("ppt/slides/slide1.xml"))
            text = "".join(node.text or "" for node in slide.findall(f".//{DRAWING_NS}t"))
            theme = ET.fromstring(package.read("ppt/theme/theme1.xml"))
            font_names = [node.attrib.get("typeface") for node in theme.findall(f".//{DRAWING_NS}latin")]
            self.assertIn("日本語", text)
            self.assertIn("Noto Sans", font_names)
            self.assertIn("Liberation Sans", font_names)
        with zipfile.ZipFile(CORPUS_DIR / fixtures["opc_external_hyperlink_no_network"]["path"]) as package:
            document = ET.fromstring(package.read("word/document.xml"))
            relationships = ET.fromstring(package.read("word/_rels/document.xml.rels"))
            hyperlink = document.find(f".//{DOCX_NS}hyperlink")
            self.assertIsNotNone(hyperlink)
            relationship_ids = {relation.attrib["Id"] for relation in relationships}
            self.assertIn(hyperlink.attrib["{http://schemas.openxmlformats.org/officeDocument/2006/relationships}id"], relationship_ids)
            self.assertEqual(relationships[0].attrib["TargetMode"], "External")
        pdf = (CORPUS_DIR / fixtures["pdf_annotations_forms"]["path"]).read_bytes()
        self.assertTrue(pdf.startswith(b"%PDF-1.4\n"))
        self.assertIn(b"/Subtype /Text", pdf)
        self.assertIn(b"/Subtype /Widget", pdf)
        self.assertIn(b"/AcroForm << /Fields [4 0 R]", pdf)
        startxref = re.search(rb"startxref\n(\d+)\n%%EOF", pdf)
        self.assertIsNotNone(startxref)
        self.assertEqual(pdf[int(startxref.group(1)) :].splitlines()[0], b"xref")

    def test_fixture_structures_match_their_declared_scenarios(self) -> None:
        manifest = json.loads((CORPUS_DIR / "manifest.json").read_text(encoding="utf-8"))
        fixtures = {fixture["id"]: fixture for fixture in manifest["fixtures"]}

        docx = fixtures["docx_sections_table"]
        with zipfile.ZipFile(CORPUS_DIR / docx["path"]) as package:
            self.assertIsNone(package.testzip())
            self.assertIn("[Content_Types].xml", package.namelist())
            self.assertIn("_rels/.rels", package.namelist())
            document = ET.fromstring(package.read("word/document.xml"))
        body = document.find(f"{DOCX_NS}body")
        self.assertIsNotNone(body)
        self.assertEqual(len(body.findall(f"{DOCX_NS}tbl")), docx["expected"]["tables"])
        self.assertEqual(len(body.findall(f"{DOCX_NS}p")), docx["expected"]["body_paragraphs"])
        sections = body.findall(f".//{DOCX_NS}sectPr")
        self.assertEqual(len(sections), docx["expected"]["sections"])

        xlsx = fixtures["xlsx_hidden_print_area"]
        with zipfile.ZipFile(CORPUS_DIR / xlsx["path"]) as package:
            self.assertIsNone(package.testzip())
            self.assertIn("[Content_Types].xml", package.namelist())
            self.assertIn("_rels/.rels", package.namelist())
            workbook = ET.fromstring(package.read("xl/workbook.xml"))
        sheets = workbook.find(f"{SHEET_NS}sheets")
        self.assertIsNotNone(sheets)
        sheet_facts = [
            [sheet.attrib["name"], sheet.attrib.get("state", "visible")]
            for sheet in sheets.findall(f"{SHEET_NS}sheet")
        ]
        self.assertEqual(sheet_facts, xlsx["expected"]["sheets"])
        defined_names = workbook.find(f"{SHEET_NS}definedNames")
        self.assertIsNotNone(defined_names)
        print_areas = [
            [name.attrib["localSheetId"], name.text]
            for name in defined_names.findall(f"{SHEET_NS}definedName")
            if name.attrib.get("name") == "_xlnm.Print_Area"
        ]
        self.assertEqual(print_areas, xlsx["expected"]["print_areas"])

        pptx = fixtures["pptx_two_slides"]
        with zipfile.ZipFile(CORPUS_DIR / pptx["path"]) as package:
            self.assertIsNone(package.testzip())
            self.assertIn("[Content_Types].xml", package.namelist())
            self.assertIn("_rels/.rels", package.namelist())
            presentation = ET.fromstring(package.read("ppt/presentation.xml"))
            slide_count = len(presentation.findall(f".//{PRESENTATION_NS}sldId"))
            master_count = len(presentation.findall(f".//{PRESENTATION_NS}sldMasterId"))
            master = ET.fromstring(package.read("ppt/slideMasters/slideMaster1.xml"))
            layout_count = len(master.findall(f".//{PRESENTATION_NS}sldLayoutId"))
            layout_ids = [
                int(layout.attrib["id"])
                for layout in master.findall(f".//{PRESENTATION_NS}sldLayoutId")
            ]
            presentation_rels = ET.fromstring(package.read("ppt/_rels/presentation.xml.rels"))
            master_relationships = [
                relation.attrib["Target"]
                for relation in presentation_rels.findall(f"{PACKAGE_REL_NS}Relationship")
                if relation.attrib["Type"].endswith("/slideMaster")
            ]
            master_rels_path = "ppt/slideMasters/_rels/slideMaster1.xml.rels"
            if master_rels_path in package.namelist():
                master_rels = ET.fromstring(package.read(master_rels_path))
                theme_relationships = [
                    relation.attrib["Target"]
                    for relation in master_rels.findall(f"{PACKAGE_REL_NS}Relationship")
                    if relation.attrib["Type"].endswith("/theme")
                ]
            else:
                theme_relationships = []
            theme_parts = sorted(
                name for name in package.namelist() if re.fullmatch(r"ppt/theme/theme\d+\.xml", name)
            )
            theme_root = ET.fromstring(package.read("ppt/theme/theme1.xml")) if theme_parts else None
            theme_elements = (
                [child.tag.rsplit("}", 1)[-1] for child in theme_root.find(f"{DRAWING_NS}themeElements")]
                if theme_root is not None
                else []
            )
            theme_font_names = (
                [node.attrib.get("typeface") for node in theme_root.findall(f".//{DRAWING_NS}latin")]
                if theme_root is not None
                else []
            )
            slide_names = sorted(
                name for name in package.namelist() if re.fullmatch(r"ppt/slides/slide\d+\.xml", name)
            )
            slide_texts = []
            for name in slide_names:
                slide = ET.fromstring(package.read(name))
                slide_texts.append("".join(node.text or "" for node in slide.findall(f".//{DRAWING_NS}t")))
        self.assertEqual(slide_count, pptx["expected"]["slide_count"])
        self.assertEqual(master_count, pptx["expected"]["master_count"])
        self.assertEqual(layout_count, pptx["expected"]["layout_count"])
        with self.subTest("slide layout IDs use the allowed OOXML range"):
            self.assertTrue(all(layout_id >= 2147483648 for layout_id in layout_ids))
        self.assertEqual(master_relationships, ["slideMasters/slideMaster1.xml"])
        self.assertEqual(theme_relationships, ["../theme/theme1.xml"])
        self.assertEqual(theme_parts, ["ppt/theme/theme1.xml"])
        self.assertEqual(theme_elements, ["clrScheme", "fontScheme", "fmtScheme"])
        self.assertEqual(len(theme_parts), pptx["expected"]["theme_count"])
        with self.subTest("theme uses the non-proprietary font family"):
            self.assertEqual(theme_font_names, [pptx["expected"]["theme_font_family"]] * 2)
        self.assertEqual(slide_texts, pptx["expected"]["slide_texts"])

        pdf = fixtures["pdf_two_page_synthetic"]
        pdf_bytes = (CORPUS_DIR / pdf["path"]).read_bytes()
        self.assertTrue(pdf_bytes.startswith(b"%PDF-1.4\n"))
        self.assertEqual(len(re.findall(rb"/Type\s*/Page\b", pdf_bytes)), pdf["expected"]["page_count"])
        self.assertIn(b"/Count 2", pdf_bytes)
        self.assertFalse(pdf["expected"]["renderer_output"])
        self.assertTrue(pdf_bytes.rstrip().endswith(b"%%EOF"))
        startxref = re.search(rb"startxref\n(\d+)\n%%EOF", pdf_bytes)
        self.assertIsNotNone(startxref)
        self.assertEqual(pdf_bytes[int(startxref.group(1)) :].splitlines()[0], b"xref")

        malformed = fixtures["docx_truncated_package"]
        malformed_bytes = (CORPUS_DIR / malformed["path"]).read_bytes()
        self.assertGreater(len(malformed_bytes), 4)
        self.assertFalse(zipfile.is_zipfile(CORPUS_DIR / malformed["path"]))
        self.assertEqual(malformed["expected"]["zip_package"], False)


if __name__ == "__main__":
    unittest.main()
