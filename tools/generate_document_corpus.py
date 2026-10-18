#!/usr/bin/env python3
"""Generate small, deterministic synthetic office-document and PDF fixtures."""

from __future__ import annotations

import argparse
import hashlib
import json
import zipfile
from pathlib import Path
from typing import Any


REPOSITORY_ROOT = Path(__file__).resolve().parents[1]
DEFAULT_OUTPUT_DIR = REPOSITORY_ROOT / "tests" / "fixtures" / "synthetic_documents"
FIXED_ZIP_TIMESTAMP = (1980, 1, 1, 0, 0, 0)


def sha256(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()


def _xml(text: str) -> bytes:
    return ('<?xml version="1.0" encoding="UTF-8" standalone="yes"?>' + text).encode("utf-8")


def _zip_package(parts: list[tuple[str, bytes]]) -> bytes:
    """Build an uncompressed OPC ZIP with metadata fixed across platforms."""
    from io import BytesIO

    output = BytesIO()
    with zipfile.ZipFile(output, "w", compression=zipfile.ZIP_STORED) as archive:
        archive.comment = b""
        for name, contents in parts:
            info = zipfile.ZipInfo(name, FIXED_ZIP_TIMESTAMP)
            info.compress_type = zipfile.ZIP_STORED
            info.create_system = 0
            info.external_attr = 0
            info.extra = b""
            info.comment = b""
            archive.writestr(info, contents)
    return output.getvalue()


def _docx_parts() -> list[tuple[str, bytes]]:
    document = _xml(
        '<w:document xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main">'
        "<w:body>"
        "<w:p><w:pPr><w:pStyle w:val=\"Title\"/></w:pPr><w:r><w:t>PaperBatch synthetic corpus document</w:t></w:r></w:p>"
        "<w:p><w:pPr><w:sectPr><w:type w:val=\"nextPage\"/><w:pgSz w:w=\"12240\" w:h=\"15840\"/></w:sectPr></w:pPr>"
        "<w:r><w:t>Synthetic section one</w:t></w:r></w:p>"
        "<w:tbl><w:tblPr/><w:tblGrid><w:gridCol w:w=\"2400\"/><w:gridCol w:w=\"4800\"/></w:tblGrid>"
        "<w:tr><w:tc><w:p><w:r><w:t>Label</w:t></w:r></w:p></w:tc><w:tc><w:p><w:r><w:t>Synthetic value</w:t></w:r></w:p></w:tc></w:tr>"
        "</w:tbl><w:sectPr><w:pgSz w:w=\"15840\" w:h=\"12240\" w:orient=\"landscape\"/></w:sectPr>"
        "</w:body></w:document>"
    )
    content_types = _xml(
        '<Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">'
        '<Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/>'
        '<Default Extension="xml" ContentType="application/xml"/>'
        '<Override PartName="/word/document.xml" ContentType="application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml"/>'
        "</Types>"
    )
    relationships = _xml(
        '<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">'
        '<Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="word/document.xml"/>'
        "</Relationships>"
    )
    return [
        ("[Content_Types].xml", content_types),
        ("_rels/.rels", relationships),
        ("word/document.xml", document),
    ]


def _build_docx() -> bytes:
    return _zip_package(_docx_parts())


def _build_xlsx() -> bytes:
    content_types = _xml(
        '<Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">'
        '<Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/>'
        '<Default Extension="xml" ContentType="application/xml"/>'
        '<Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/>'
        '<Override PartName="/xl/worksheets/sheet1.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/>'
        '<Override PartName="/xl/worksheets/sheet2.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/>'
        "</Types>"
    )
    root_rels = _xml(
        '<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">'
        '<Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="xl/workbook.xml"/>'
        "</Relationships>"
    )
    workbook = _xml(
        '<workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" '
        'xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships">'
        '<bookViews><workbookView activeTab="0"/></bookViews>'
        '<sheets><sheet name="VisibleData" sheetId="1" r:id="rId1"/>'
        '<sheet name="HiddenNotes" sheetId="2" state="hidden" r:id="rId2"/></sheets>'
        '<definedNames><definedName name="_xlnm.Print_Area" localSheetId="0">VisibleData!$A$1:$B$3</definedName></definedNames>'
        "</workbook>"
    )
    workbook_rels = _xml(
        '<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">'
        '<Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet1.xml"/>'
        '<Relationship Id="rId2" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet2.xml"/>'
        "</Relationships>"
    )
    sheet1 = _xml(
        '<worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main">'
        '<dimension ref="A1:B3"/><sheetData><row r="1"><c r="A1" t="inlineStr"><is><t>PaperBatch synthetic corpus</t></is></c>'
        '<c r="B1"><v>1</v></c></row><row r="2"><c r="A2" t="inlineStr"><is><t>Visible sheet</t></is></c>'
        '<c r="B2"><v>2</v></c></row><row r="3"><c r="A3" t="inlineStr"><is><t>Print area edge</t></is></c>'
        '<c r="B3"><v>3</v></c></row></sheetData></worksheet>'
    )
    sheet2 = _xml(
        '<worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main">'
        '<dimension ref="A1:A1"/><sheetData><row r="1"><c r="A1" t="inlineStr"><is><t>Synthetic hidden data</t></is></c></row></sheetData></worksheet>'
    )
    return _zip_package(
        [
            ("[Content_Types].xml", content_types),
            ("_rels/.rels", root_rels),
            ("xl/workbook.xml", workbook),
            ("xl/_rels/workbook.xml.rels", workbook_rels),
            ("xl/worksheets/sheet1.xml", sheet1),
            ("xl/worksheets/sheet2.xml", sheet2),
        ]
    )


def _pptx_slide(text: str) -> bytes:
    return _xml(
        '<p:sld xmlns:p="http://schemas.openxmlformats.org/presentationml/2006/main" '
        'xmlns:a="http://schemas.openxmlformats.org/drawingml/2006/main">'
        '<p:cSld><p:spTree><p:nvGrpSpPr><p:cNvPr id="1" name=""/><p:cNvGrpSpPr/>'
        "<p:nvPr/></p:nvGrpSpPr><p:grpSpPr><a:xfrm><a:off x=\"0\" y=\"0\"/><a:ext cx=\"0\" cy=\"0\"/>"
        "<a:chOff x=\"0\" y=\"0\"/><a:chExt cx=\"0\" cy=\"0\"/></a:xfrm></p:grpSpPr>"
        "<p:sp><p:nvSpPr><p:cNvPr id=\"2\" name=\"Synthetic text\"/><p:cNvSpPr/><p:nvPr/></p:nvSpPr>"
        "<p:spPr/><p:txBody><a:bodyPr/><a:lstStyle/><a:p><a:r><a:rPr lang=\"en-US\"/><a:t>"
        + text
        + "</a:t></a:r><a:endParaRPr lang=\"en-US\"/></a:p></p:txBody></p:sp>"
        "</p:spTree></p:cSld></p:sld>"
    )


def _pptx_empty_shape_tree() -> str:
    return (
        "<p:spTree><p:nvGrpSpPr><p:cNvPr id=\"1\" name=\"\"/><p:cNvGrpSpPr/>"
        "<p:nvPr/></p:nvGrpSpPr><p:grpSpPr><a:xfrm><a:off x=\"0\" y=\"0\"/>"
        "<a:ext cx=\"0\" cy=\"0\"/><a:chOff x=\"0\" y=\"0\"/>"
        "<a:chExt cx=\"0\" cy=\"0\"/></a:xfrm></p:grpSpPr></p:spTree>"
    )


def _build_pptx() -> bytes:
    content_types = _xml(
        '<Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">'
        '<Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/>'
        '<Default Extension="xml" ContentType="application/xml"/>'
        '<Override PartName="/ppt/presentation.xml" ContentType="application/vnd.openxmlformats-officedocument.presentationml.presentation.main+xml"/>'
        '<Override PartName="/ppt/slideMasters/slideMaster1.xml" ContentType="application/vnd.openxmlformats-officedocument.presentationml.slideMaster+xml"/>'
        '<Override PartName="/ppt/slideLayouts/slideLayout1.xml" ContentType="application/vnd.openxmlformats-officedocument.presentationml.slideLayout+xml"/>'
        '<Override PartName="/ppt/theme/theme1.xml" ContentType="application/vnd.openxmlformats-officedocument.theme+xml"/>'
        '<Override PartName="/ppt/slides/slide1.xml" ContentType="application/vnd.openxmlformats-officedocument.presentationml.slide+xml"/>'
        '<Override PartName="/ppt/slides/slide2.xml" ContentType="application/vnd.openxmlformats-officedocument.presentationml.slide+xml"/>'
        "</Types>"
    )
    root_rels = _xml(
        '<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">'
        '<Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="ppt/presentation.xml"/>'
        "</Relationships>"
    )
    presentation = _xml(
        '<p:presentation xmlns:p="http://schemas.openxmlformats.org/presentationml/2006/main" '
        'xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships">'
        '<p:sldMasterIdLst><p:sldMasterId id="2147483648" r:id="rIdMaster"/></p:sldMasterIdLst>'
        '<p:sldIdLst><p:sldId id="256" r:id="rId1"/><p:sldId id="257" r:id="rId2"/></p:sldIdLst>'
        '<p:sldSz cx="9144000" cy="5143500" type="screen16x9"/><p:notesSz cx="6858000" cy="9144000"/>'
        "</p:presentation>"
    )
    presentation_rels = _xml(
        '<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">'
        '<Relationship Id="rIdMaster" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/slideMaster" Target="slideMasters/slideMaster1.xml"/>'
        '<Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/slide" Target="slides/slide1.xml"/>'
        '<Relationship Id="rId2" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/slide" Target="slides/slide2.xml"/>'
        "</Relationships>"
    )
    slide_master = _xml(
        '<p:sldMaster xmlns:p="http://schemas.openxmlformats.org/presentationml/2006/main" '
        'xmlns:a="http://schemas.openxmlformats.org/drawingml/2006/main" '
        'xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships">'
        '<p:cSld name="Synthetic master">'
        + _pptx_empty_shape_tree()
        + "</p:cSld><p:clrMap bg1=\"lt1\" tx1=\"dk1\" bg2=\"lt2\" tx2=\"dk2\" accent1=\"accent1\" "
        "accent2=\"accent2\" accent3=\"accent3\" accent4=\"accent4\" accent5=\"accent5\" "
        "accent6=\"accent6\" hlink=\"hlink\" folHlink=\"folHlink\"/>"
        '<p:sldLayoutIdLst><p:sldLayoutId id="2147483648" r:id="rIdLayout"/></p:sldLayoutIdLst>'
        "<p:txStyles><p:titleStyle/><p:bodyStyle/><p:otherStyle/></p:txStyles></p:sldMaster>"
    )
    slide_master_rels = _xml(
        '<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">'
        '<Relationship Id="rIdLayout" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/slideLayout" Target="../slideLayouts/slideLayout1.xml"/>'
        '<Relationship Id="rIdTheme" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/theme" Target="../theme/theme1.xml"/>'
        "</Relationships>"
    )
    theme = _xml(
        '<a:theme xmlns:a="http://schemas.openxmlformats.org/drawingml/2006/main" name="PaperBatch Synthetic Theme">'
        '<a:themeElements><a:clrScheme name="PaperBatch Synthetic">'
        '<a:dk1><a:srgbClr val="000000"/></a:dk1><a:lt1><a:srgbClr val="FFFFFF"/></a:lt1>'
        '<a:dk2><a:srgbClr val="1F1F1F"/></a:dk2><a:lt2><a:srgbClr val="E7E6E6"/></a:lt2>'
        '<a:accent1><a:srgbClr val="4472C4"/></a:accent1><a:accent2><a:srgbClr val="ED7D31"/></a:accent2>'
        '<a:accent3><a:srgbClr val="A5A5A5"/></a:accent3><a:accent4><a:srgbClr val="FFC000"/></a:accent4>'
        '<a:accent5><a:srgbClr val="5B9BD5"/></a:accent5><a:accent6><a:srgbClr val="70AD47"/></a:accent6>'
        '<a:hlink><a:srgbClr val="0563C1"/></a:hlink><a:folHlink><a:srgbClr val="954F72"/></a:folHlink>'
        '</a:clrScheme><a:fontScheme name="PaperBatch Synthetic">'
        '<a:majorFont><a:latin typeface="Liberation Sans"/><a:ea typeface=""/><a:cs typeface=""/></a:majorFont>'
        '<a:minorFont><a:latin typeface="Liberation Sans"/><a:ea typeface=""/><a:cs typeface=""/></a:minorFont>'
        '</a:fontScheme><a:fmtScheme name="PaperBatch Synthetic"><a:fillStyleLst>'
        '<a:solidFill><a:schemeClr val="phClr"/></a:solidFill>'
        '<a:solidFill><a:schemeClr val="phClr"/></a:solidFill>'
        '<a:solidFill><a:schemeClr val="phClr"/></a:solidFill></a:fillStyleLst><a:lnStyleLst>'
        '<a:ln w="6350" cap="flat" cmpd="sng" algn="ctr"><a:solidFill><a:schemeClr val="phClr"/></a:solidFill><a:prstDash val="solid"/><a:miter lim="800000"/></a:ln>'
        '<a:ln w="12700" cap="flat" cmpd="sng" algn="ctr"><a:solidFill><a:schemeClr val="phClr"/></a:solidFill><a:prstDash val="solid"/><a:miter lim="800000"/></a:ln>'
        '<a:ln w="19050" cap="flat" cmpd="sng" algn="ctr"><a:solidFill><a:schemeClr val="phClr"/></a:solidFill><a:prstDash val="solid"/><a:miter lim="800000"/></a:ln>'
        '</a:lnStyleLst><a:effectStyleLst><a:effectStyle><a:effectLst/></a:effectStyle>'
        '<a:effectStyle><a:effectLst/></a:effectStyle><a:effectStyle><a:effectLst/></a:effectStyle>'
        '</a:effectStyleLst><a:bgFillStyleLst>'
        '<a:solidFill><a:schemeClr val="phClr"/></a:solidFill>'
        '<a:solidFill><a:schemeClr val="phClr"/></a:solidFill>'
        '<a:solidFill><a:schemeClr val="phClr"/></a:solidFill>'
        '</a:bgFillStyleLst></a:fmtScheme></a:themeElements></a:theme>'
    )
    slide_layout = _xml(
        '<p:sldLayout xmlns:p="http://schemas.openxmlformats.org/presentationml/2006/main" '
        'xmlns:a="http://schemas.openxmlformats.org/drawingml/2006/main" type="blank" preserve="1">'
        '<p:cSld name="Blank">'
        + _pptx_empty_shape_tree()
        + "</p:cSld><p:clrMapOvr><a:masterClrMapping/></p:clrMapOvr></p:sldLayout>"
    )
    slide_layout_rels = _xml(
        '<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">'
        '<Relationship Id="rIdMaster" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/slideMaster" Target="../slideMasters/slideMaster1.xml"/>'
        "</Relationships>"
    )
    slide_rels = _xml(
        '<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">'
        '<Relationship Id="rIdLayout" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/slideLayout" Target="../slideLayouts/slideLayout1.xml"/>'
        "</Relationships>"
    )
    return _zip_package(
        [
            ("[Content_Types].xml", content_types),
            ("_rels/.rels", root_rels),
            ("ppt/presentation.xml", presentation),
            ("ppt/_rels/presentation.xml.rels", presentation_rels),
            ("ppt/slideMasters/slideMaster1.xml", slide_master),
            ("ppt/slideMasters/_rels/slideMaster1.xml.rels", slide_master_rels),
            ("ppt/slideLayouts/slideLayout1.xml", slide_layout),
            ("ppt/slideLayouts/_rels/slideLayout1.xml.rels", slide_layout_rels),
            ("ppt/theme/theme1.xml", theme),
            ("ppt/slides/slide1.xml", _pptx_slide("PaperBatch synthetic slide one")),
            ("ppt/slides/_rels/slide1.xml.rels", slide_rels),
            ("ppt/slides/slide2.xml", _pptx_slide("PaperBatch synthetic slide two")),
            ("ppt/slides/_rels/slide2.xml.rels", slide_rels),
        ]
    )


def _build_pdf() -> bytes:
    stream_one = b"BT /F1 18 Tf 72 720 Td (PaperBatch synthetic PDF page one) Tj ET\n"
    stream_two = b"BT /F1 18 Tf 72 720 Td (PaperBatch synthetic PDF page two) Tj ET\n"
    objects = [
        b"<< /Type /Catalog /Pages 2 0 R >>",
        b"<< /Type /Pages /Kids [3 0 R 5 0 R] /Count 2 >>",
        b"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 7 0 R >> >> /Contents 4 0 R >>",
        b"<< /Length " + str(len(stream_one)).encode("ascii") + b" >>\nstream\n" + stream_one + b"endstream",
        b"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 7 0 R >> >> /Contents 6 0 R >>",
        b"<< /Length " + str(len(stream_two)).encode("ascii") + b" >>\nstream\n" + stream_two + b"endstream",
        b"<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>",
    ]
    output = bytearray(b"%PDF-1.4\n%PaperBatch synthetic fixture\x00\n%\xe2\xe3\xcf\xd3\n")
    offsets = [0]
    for object_number, body in enumerate(objects, start=1):
        offsets.append(len(output))
        output.extend(f"{object_number} 0 obj\n".encode("ascii"))
        output.extend(body)
        output.extend(b"\nendobj\n")

    xref_offset = len(output)
    output.extend(f"xref\n0 {len(offsets)}\n".encode("ascii"))
    output.extend(b"0000000000 65535 f \n")
    for offset in offsets[1:]:
        output.extend(f"{offset:010d} 00000 n \n".encode("ascii"))
    output.extend(
        f"trailer\n<< /Size {len(offsets)} /Root 1 0 R >>\nstartxref\n{xref_offset}\n%%EOF\n".encode("ascii")
    )
    return bytes(output)


def _fixture_specs() -> list[tuple[str, str, str, str, dict[str, Any], bytes]]:
    docx = _build_docx()
    return [
        (
            "docx_sections_table",
            "synthetic_sections_table.docx",
            "docx",
            "Synthetic narrative, table, and portrait-to-landscape section variation.",
            {"tables": 1, "body_paragraphs": 2, "sections": 2},
            docx,
        ),
        (
            "xlsx_hidden_print_area",
            "synthetic_hidden_print_area.xlsx",
            "xlsx",
            "Synthetic visible and hidden sheets with a declared print area.",
            {
                "sheets": [["VisibleData", "visible"], ["HiddenNotes", "hidden"]],
                "print_areas": [["0", "VisibleData!$A$1:$B$3"]],
            },
            _build_xlsx(),
        ),
        (
            "pptx_two_slides",
            "synthetic_two_slides.pptx",
            "pptx",
            "Synthetic two-slide presentation with distinct slide text.",
            {
                "slide_count": 2,
                "master_count": 1,
                "layout_count": 1,
                "theme_count": 1,
                "theme_font_family": "Liberation Sans",
                "slide_texts": ["PaperBatch synthetic slide one", "PaperBatch synthetic slide two"],
            },
            _build_pptx(),
        ),
        (
            "pdf_two_page_synthetic",
            "synthetic_two_page.pdf",
            "pdf",
            "Hand-authored synthetic PDF syntax with two page objects and text streams.",
            {"page_count": 2, "renderer_output": False},
            _build_pdf(),
        ),
        (
            "docx_truncated_package",
            "synthetic_truncated_package.docx",
            "docx",
            "Synthetic malformed edge case: a valid synthetic DOCX prefix truncated before its ZIP directory.",
            {"zip_package": False, "failure_reason": "truncated_without_central_directory"},
            docx[:96],
        ),
    ]


def generate_corpus(output_dir: Path) -> dict[str, Any]:
    """Write all synthetic fixtures and their deterministic integrity manifest."""
    output_dir = Path(output_dir)
    output_dir.mkdir(parents=True, exist_ok=True)
    entries = []
    for fixture_id, filename, file_format, scenario, expected, data in _fixture_specs():
        (output_dir / filename).write_bytes(data)
        entries.append(
            {
                "id": fixture_id,
                "path": filename,
                "format": file_format,
                "scenario": scenario,
                "expected": expected,
                "sha256": sha256(data),
                "size_bytes": len(data),
            }
        )
    manifest: dict[str, Any] = {
        "schema_version": 1,
        "synthetic": True,
        "provenance": "Generated by tools/generate_document_corpus.py; no renderer or external documents were used.",
        "fixtures": entries,
    }
    (output_dir / "manifest.json").write_text(
        json.dumps(manifest, ensure_ascii=False, indent=2) + "\n",
        encoding="utf-8",
        newline="\n",
    )
    return manifest


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument(
        "--output-dir",
        type=Path,
        default=DEFAULT_OUTPUT_DIR,
        help=f"destination directory (default: {DEFAULT_OUTPUT_DIR})",
    )
    args = parser.parse_args()
    manifest = generate_corpus(args.output_dir)
    print(f"Generated {len(manifest['fixtures'])} synthetic fixtures in {args.output_dir}")


if __name__ == "__main__":
    main()
