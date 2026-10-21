#!/usr/bin/env python3
"""Generate small, deterministic synthetic office-document and PDF fixtures."""

from __future__ import annotations

import argparse
import hashlib
import json
import zipfile
import warnings
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


def _repackage(package_bytes: bytes, replacements: dict[str, bytes]) -> bytes:
    from io import BytesIO

    with zipfile.ZipFile(BytesIO(package_bytes)) as source:
        parts = [(name, replacements.get(name, source.read(name))) for name in source.namelist()]
    return _zip_package(parts)


def _build_multilingual_docx() -> bytes:
    parts = dict(_docx_parts())
    multilingual = "English | Ελληνικά | 日本語 | العربية | हिन्दी | עברית | 한국어 | Español"
    document = _xml(
        '<w:document xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main"><w:body>'
        f'<w:p><w:r><w:rPr><w:rFonts w:ascii="Liberation Sans"/></w:rPr><w:t>{multilingual}</w:t></w:r></w:p>'
        '<w:p><w:fldSimple w:instr="PAGE"><w:r><w:t>1</w:t></w:r></w:fldSimple></w:p>'
        '<w:sdt><w:sdtPr><w:checkBox/></w:sdtPr><w:sdtContent><w:p><w:r><w:t>Form marker</w:t></w:r></w:p></w:sdtContent></w:sdt>'
        '<w:sectPr/></w:body></w:document>'
    )
    parts["word/document.xml"] = document
    return _zip_package(list(parts.items()))


def _build_annotated_docx() -> bytes:
    parts = dict(_docx_parts())
    content_types = parts["[Content_Types].xml"].decode("utf-8").replace(
        "</Types>",
        '<Override PartName="/word/comments.xml" ContentType="application/vnd.openxmlformats-officedocument.wordprocessingml.comments+xml"/>'
        '<Override PartName="/word/footnotes.xml" ContentType="application/vnd.openxmlformats-officedocument.wordprocessingml.footnotes+xml"/></Types>',
    )
    # Document-level relationships are required for comment and footnote parts.
    doc_rels = _xml(
        '<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">'
        '<Relationship Id="rIdComments" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/comments" Target="comments.xml"/>'
        '<Relationship Id="rIdFootnotes" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/footnotes" Target="footnotes.xml"/>'
        '</Relationships>'
    )
    document = _xml(
        '<w:document xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main"><w:body><w:p>'
        '<w:commentRangeStart w:id="0"/><w:r><w:t>Annotated synthetic text</w:t></w:r><w:commentRangeEnd w:id="0"/>'
        '<w:r><w:commentReference w:id="0"/></w:r><w:r><w:footnoteReference w:id="1"/></w:r>'
        '</w:p><w:sectPr/></w:body></w:document>'
    )
    parts["[Content_Types].xml"] = content_types.encode("utf-8")
    parts["word/document.xml"] = document
    parts["word/_rels/document.xml.rels"] = doc_rels
    parts["word/comments.xml"] = _xml(
        '<w:comments xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main"><w:comment w:id="0" w:author="Synthetic"><w:p><w:r><w:t>Authored test comment</w:t></w:r></w:p></w:comment></w:comments>'
    )
    parts["word/footnotes.xml"] = _xml(
        '<w:footnotes xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main">'
        '<w:footnote w:type="separator" w:id="-1"><w:p><w:r><w:separator/></w:r></w:p></w:footnote>'
        '<w:footnote w:type="continuationSeparator" w:id="0"><w:p><w:r><w:continuationSeparator/></w:r></w:p></w:footnote>'
        '<w:footnote w:id="1"><w:p><w:r><w:t>Authored footnote</w:t></w:r></w:p></w:footnote></w:footnotes>'
    )
    return _zip_package(list(parts.items()))


def _build_mixed_page_xlsx() -> bytes:
    package = _build_xlsx()
    from io import BytesIO

    replacements: dict[str, bytes] = {}
    with zipfile.ZipFile(BytesIO(package)) as source:
        for name, paper_size in (("xl/worksheets/sheet1.xml", "1"), ("xl/worksheets/sheet2.xml", "9")):
            xml = source.read(name).decode("utf-8").replace("</worksheet>", f'<pageSetup paperSize="{paper_size}"/></worksheet>')
            replacements[name] = xml.encode("utf-8")
    return _repackage(package, replacements)


def _build_multilingual_pptx() -> bytes:
    package = _build_pptx()
    multilingual = "English | Ελληνικά | 日本語 | العربية | हिन्दी | עברית | 한국어 | Español"
    slide = _pptx_slide(multilingual)
    from io import BytesIO

    with zipfile.ZipFile(BytesIO(package)) as source:
        theme = source.read("ppt/theme/theme1.xml").replace(
            b'<a:majorFont><a:latin typeface="Liberation Sans"/>',
            b'<a:majorFont><a:latin typeface="Noto Sans"/>',
        )
    return _repackage(package, {"ppt/slides/slide1.xml": slide, "ppt/theme/theme1.xml": theme})


def _build_external_relationship_package() -> bytes:
    parts = dict(_docx_parts())
    parts["word/document.xml"] = _xml(
        '<w:document xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main" '
        'xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships"><w:body><w:p>'
        '<w:hyperlink r:id="rIdExternal"><w:r><w:t>External target marker</w:t></w:r></w:hyperlink></w:p>'
        '<w:sectPr/></w:body></w:document>'
    )
    parts["word/_rels/document.xml.rels"] = _xml(
        '<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">'
        '<Relationship Id="rIdExternal" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/hyperlink" '
        'Target="https://example.invalid/fixture" TargetMode="External"/></Relationships>'
    )
    return _zip_package(list(parts.items()))


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


def _build_pdf_variant(index: int) -> tuple[bytes, dict[str, int]]:
    """Build a small PDF with distinct page count, geometry, and authored text."""
    page_count = 1 + index % 5
    width, height = ((612, 792), (792, 612), (595, 842))[index % 3]
    objects = [b"<< /Type /Catalog /Pages 2 0 R >>"]
    page_refs = [3 + page * 2 for page in range(page_count)]
    objects.append(b"<< /Type /Pages /Kids [" + b" ".join(f"{ref} 0 R".encode("ascii") for ref in page_refs) + f"] /Count {page_count} >>".encode("ascii"))
    for page in range(page_count):
        content_ref = 4 + page * 2
        objects.extend([
            f"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {width} {height}] /Resources << /Font << /F1 {3 + 2 * page_count} 0 R >> >> /Contents {content_ref} 0 R >>".encode("ascii"),
            b"<< /Length " + str(len(stream := f"BT /F1 12 Tf 36 {height - 48} Td (Synthetic PDF case {index:03d}, page {page + 1}) Tj ET\n".encode("ascii"))).encode("ascii") + b" >>\nstream\n" + stream + b"endstream",
        ])
    objects.append(b"<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>")
    output = bytearray(b"%PDF-1.4\n%PaperBatch locally authored synthetic variant\n")
    offsets = [0]
    for number, body in enumerate(objects, start=1):
        offsets.append(len(output))
        output.extend(f"{number} 0 obj\n".encode("ascii") + body + b"\nendobj\n")
    xref_offset = len(output)
    output.extend(f"xref\n0 {len(offsets)}\n0000000000 65535 f \n".encode("ascii"))
    for offset in offsets[1:]:
        output.extend(f"{offset:010d} 00000 n \n".encode("ascii"))
    output.extend(f"trailer\n<< /Size {len(offsets)} /Root 1 0 R >>\nstartxref\n{xref_offset}\n%%EOF\n".encode("ascii"))
    return bytes(output), {"page_count": page_count, "page_width_points": width, "page_height_points": height, "renderer_output": False}


def _scaled_specs() -> list[tuple[str, str, str, str, dict[str, Any], bytes]]:
    """Create bounded variants whose document content or structure changes per case."""
    from io import BytesIO

    specs: list[tuple[str, str, str, str, dict[str, Any], bytes]] = []
    for index in range(30):
        # DOCX variants add a varying number of paragraphs and change paper size.
        parts = dict(_docx_parts())
        paragraphs = "".join(
            f"<w:p><w:r><w:t>Case {index:03d} section {part + 1}: multilingual sample Ελληνικά 日本語 العربية</w:t></w:r></w:p>"
            for part in range(1 + index % 6)
        )
        geometry = ((12240, 15840), (15840, 12240), (11906, 16838))[index % 3]
        xml = parts["word/document.xml"].decode("utf-8").replace(
            "<w:sectPr><w:pgSz w:w=\"15840\" w:h=\"12240\" w:orient=\"landscape\"/></w:sectPr>",
            f"{paragraphs}<w:sectPr><w:pgSz w:w=\"{geometry[0]}\" w:h=\"{geometry[1]}\"/></w:sectPr>",
        ).replace("PaperBatch synthetic corpus document", f"Synthetic DOCX case {index:03d}")
        parts["word/document.xml"] = xml.encode("utf-8")
        specs.append((f"docx_variant_{index:03d}", f"variants/docx_{index:03d}.docx", "docx",
                      f"Synthetic DOCX variant with {1 + index % 6} authored paragraphs and page geometry {geometry[0]} by {geometry[1]} twips.",
                      {"paragraphs_added": 1 + index % 6, "page_width_twips": geometry[0], "page_height_twips": geometry[1]}, _zip_package(list(parts.items()))))

        # XLSX variants vary row count, cell content, and declared print range.
        package = _build_xlsx()
        with zipfile.ZipFile(BytesIO(package)) as source:
            xparts = {name: source.read(name) for name in source.namelist()}
        rows = 1 + index % 8
        row_xml = "".join(f'<row r="{row}"><c r="A{row}" t="inlineStr"><is><t>Case {index:03d} row {row}</t></is></c><c r="B{row}"><v>{index * 10 + row}</v></c></row>' for row in range(1, rows + 1))
        sheet_xml = xparts["xl/worksheets/sheet1.xml"].decode("utf-8")
        sheet_prefix, sheet_data = sheet_xml.split("<sheetData>", 1)
        sheet_prefix = sheet_prefix.replace('<dimension ref="A1:B3"/>', "")
        _, sheet_suffix = sheet_data.split("</sheetData>", 1)
        xparts["xl/worksheets/sheet1.xml"] = (sheet_prefix + f'<dimension ref="A1:B{rows}"/><sheetData>' + row_xml + "</sheetData>" + sheet_suffix).encode("utf-8")
        sheet_name = f"Data{index:03d}"
        print_area = f"{sheet_name}!$A$1:$B${rows}"
        xparts["xl/workbook.xml"] = xparts["xl/workbook.xml"].replace(
            b"VisibleData!$A$1:$B$3", print_area.encode("ascii")).replace(b"VisibleData", sheet_name.encode("ascii"))
        specs.append((f"xlsx_variant_{index:03d}", f"variants/xlsx_{index:03d}.xlsx", "xlsx",
                      f"Synthetic workbook with {rows} distinct data rows and a case-specific print area.",
                      {"data_rows": rows, "case_number": index, "print_area_end_row": rows}, _zip_package(list(xparts.items()))))

        # PPTX variants vary slide text density and shape count using authored text shapes.
        pptx = _build_pptx()
        with zipfile.ZipFile(BytesIO(pptx)) as source:
            pparts = {name: source.read(name) for name in source.namelist()}
        lines = "".join(f"<a:r><a:rPr lang=\"en-US\"/><a:t>Case {index:03d} text shape {shape + 1}</a:t></a:r><a:br/>" for shape in range(1 + index % 5))
        slide = _pptx_slide(f"Synthetic slide case {index:03d}").replace(b"</a:p></p:txBody>", lines.encode("utf-8") + b"</a:p></p:txBody>")
        pparts["ppt/slides/slide1.xml"] = slide
        specs.append((f"pptx_variant_{index:03d}", f"variants/pptx_{index:03d}.pptx", "pptx",
                      f"Synthetic presentation with case-specific text and {1 + index % 5} additional slide text runs.",
                      {"slides": 2, "additional_text_runs": 1 + index % 5, "case_number": index}, _zip_package(list(pparts.items()))))

        pdf_data, pdf_expected = _build_pdf_variant(index)
        specs.append((f"pdf_variant_{index:03d}", f"variants/pdf_{index:03d}.pdf", "pdf",
                      f"Hand-authored synthetic PDF variant with {pdf_expected['page_count']} pages and case-specific text; no renderer output.",
                      pdf_expected, pdf_data))

        # Deliberately truncated package prefixes exercise distinct safe failure offsets.
        malformed = _zip_package([(f"synthetic_{index:03d}.xml", _xml(f"<root><case>{index:03d}</case></root>"))])
        cut = 24 + index
        specs.append((f"malformed_variant_{index:03d}", f"variants/malformed_{index:03d}.zip", "malformed",
                      f"Synthetic ZIP prefix truncated at byte {cut} before its central directory.",
                      {"zip_package": False, "truncated_at_byte": cut}, malformed[:cut]))
    return specs


def _build_pdf_annotations_forms() -> bytes:
    stream = b"BT /F1 12 Tf 72 720 Td (PaperBatch synthetic annotation and form sample) Tj ET\n"
    objects = [
        b"<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [4 0 R] /NeedAppearances true >> >>",
        b"<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
        b"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 7 0 R >> >> /Contents 6 0 R /Annots [4 0 R 5 0 R] >>",
        b"<< /Type /Annot /Subtype /Widget /FT /Tx /T (synthetic_field) /Rect [72 680 240 704] /P 3 0 R /F 4 /DA (/F1 12 Tf 0 g) >>",
        b"<< /Type /Annot /Subtype /Text /Contents (Synthetic note) /Rect [260 680 280 700] /P 3 0 R /F 4 >>",
        b"<< /Length " + str(len(stream)).encode("ascii") + b" >>\nstream\n" + stream + b"endstream",
        b"<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>",
    ]
    output = bytearray(b"%PDF-1.4\n%PaperBatch synthetic annotation/form fixture\x00\n%\xe2\xe3\xcf\xd3\n")
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


def _expanded_specs() -> list[tuple[str, str, str, str, dict[str, Any], bytes]]:
    return [
        ("docx_multilingual_fields_forms", "synthetic_multilingual_fields_forms.docx", "docx",
         "Synthetic multilingual text with field, form-control, and mixed-font references.",
         {"languages": ["en", "el", "ja", "ar", "hi", "he", "ko", "es"], "fields": 1, "form_controls": 1},
         _build_multilingual_docx()),
        ("docx_annotations", "synthetic_annotations.docx", "docx",
         "Synthetic comment range and footnote markers authored as package markup.",
         {"comment_ranges": 1, "footnotes": 1}, _build_annotated_docx()),
        ("xlsx_mixed_page_sizes", "synthetic_mixed_page_sizes.xlsx", "xlsx",
         "Synthetic spreadsheet print settings with two page-size declarations and a print area.",
         {"print_areas": 1, "page_setup_variants": 2}, _build_mixed_page_xlsx()),
        ("pptx_multilingual_fonts", "synthetic_multilingual_fonts.pptx", "pptx",
         "Synthetic multilingual slide text with generic font family names.",
         {"languages": ["en", "ja", "ar", "es"], "font_families": ["Liberation Sans", "Noto Sans"]},
         _build_multilingual_pptx()),
        ("pdf_annotations_forms", "synthetic_annotations_forms.pdf", "pdf",
         "Hand-authored synthetic PDF object structure with a text annotation and AcroForm widget; not renderer output.",
         {"annotations": 1, "form_fields": 1, "renderer_output": False}, _build_pdf_annotations_forms()),
        ("encrypted_package_simulation", "synthetic_encrypted_package.bin", "simulation",
         "Opaque synthetic bytes mark an encrypted-container scenario; no encryption or password is implemented.",
         {"encrypted": True, "real_encryption": False}, b"PAPERBATCH-SYNTHETIC-ENCRYPTED-SCENARIO\x00"),
        ("legacy_ole_signature_simulation", "synthetic_legacy_ole.bin", "simulation",
         "Synthetic legacy-format signature only; not a valid Office binary document.",
         {"legacy_signature": True, "valid_ole_document": False}, bytes.fromhex("D0CF11E0A1B11AE1") + b"PaperBatch synthetic only"),
        ("fault_zip_path_traversal", "fault_path_traversal.zip", "zip",
         "Synthetic hostile archive member path; fixture is inspected as metadata and never extracted.",
         {"unsafe_member": "../outside.txt", "expected_rejection": True}, _zip_package([("../outside.txt", b"synthetic") ])),
        ("fault_oversized_member", "fault_oversized_member.zip", "zip",
         "Bounded synthetic archive with a 1 MiB stored member for size-limit rejection tests.",
         {"member_size_bytes": 1048576, "expected_rejection": True}, _zip_package([("payload.bin", b"X" * 1048576)])),
        ("opc_external_hyperlink_no_network", "opc_external_hyperlink_no_network.zip", "zip",
         "Synthetic OPC external hyperlink relationship for inert parsing; no network request is made.",
         {"external_target": "https://example.invalid/fixture", "relationship_valid_in_opc": True, "network_fetch_expected": False},
         _build_external_relationship_package()),
        ("fault_duplicate_member", "fault_duplicate_member.zip", "zip",
         "Synthetic archive with duplicate names to exercise package ambiguity rejection.",
         {"duplicate_names": ["part.xml"], "expected_rejection": True}, _duplicate_name_package()),
    ]


def _duplicate_name_package() -> bytes:
    # Duplicate names are the deliberate fault condition, so suppress zipfile's
    # construction-time warning while preserving the malformed archive bytes.
    with warnings.catch_warnings():
        warnings.filterwarnings("ignore", message="Duplicate name:.*", category=UserWarning)
        return _zip_package([("part.xml", b"one"), ("part.xml", b"two")])


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
    ] + _expanded_specs() + _scaled_specs()


def generate_corpus(output_dir: Path) -> dict[str, Any]:
    """Write all synthetic fixtures and their deterministic integrity manifest."""
    output_dir = Path(output_dir)
    output_dir.mkdir(parents=True, exist_ok=True)
    entries = []
    for fixture_id, filename, file_format, scenario, expected, data in _fixture_specs():
        fixture_path = output_dir / filename
        fixture_path.parent.mkdir(parents=True, exist_ok=True)
        fixture_path.write_bytes(data)
        entries.append(
            {
                "id": fixture_id,
                "path": filename,
                "format": file_format,
                "stratum": fixture_id.split("_", 1)[0],
                "scenario": scenario,
                "expected": expected,
                "evidence_class": "synthetic",
                "simulation_only": True,
                "coverage_tags": [fixture_id.split("_", 1)[0], *fixture_id.split("_")[1:]],
                "expected_outcome": {
                    "status": "unsupported" if fixture_id in {"encrypted_package_simulation", "legacy_ole_signature_simulation"}
                    else "rejected" if file_format == "malformed"
                    else "rejected" if fixture_id in {
                        "docx_truncated_package", "fault_zip_path_traversal", "fault_oversized_member",
                        "fault_duplicate_member",
                    }
                    else "synthetic_only" if fixture_id in {
                        "docx_multilingual_fields_forms", "docx_annotations", "xlsx_mixed_page_sizes",
                        "pptx_multilingual_fonts", "pdf_annotations_forms", "opc_external_hyperlink_no_network",
                    } else "accepted",
                    "reason": "Truncated synthetic ZIP input is expected to be rejected safely." if file_format == "malformed"
                    else "Simulation marker only; no supported encrypted or legacy document is present." if fixture_id in {
                        "encrypted_package_simulation", "legacy_ole_signature_simulation",
                    } else "Valid external relationship metadata is inspected without requesting its target." if fixture_id == "opc_external_hyperlink_no_network"
                    else "Malformed or hazardous package is expected to be rejected safely." if fixture_id in {
                        "docx_truncated_package", "fault_zip_path_traversal", "fault_oversized_member",
                        "fault_duplicate_member",
                    } else "Synthetic fixture only; real engine and device behavior is unmeasured.",
                },
                "sha256": sha256(data),
                "size_bytes": len(data),
            }
        )
    manifest: dict[str, Any] = {
        "schema_version": 2,
        "synthetic": True,
        "evidence_class": "synthetic",
        "simulation_only": True,
        "provenance": "Generated by tools/generate_document_corpus.py; no renderer or external documents were used.",
        "counts": {
            "synthetic_fixtures": len(entries),
            "syntheticFixtureCount": len(entries),
            "compatibilityEvidenceCount": 0,
            "realEngineDeviceEvidenceCount": 0,
            "parameter_variants_counted_as_compatibility": 0,
        },
        "coverage_target": {"minimum": 150, "maximum": 300},
        "evidence_gaps": {
            "engine_generated": "absent",
            "human_reviewed": "absent",
            "oem_driver": "absent",
            "physical_output": "absent",
            "office_wps": "absent",
            "real_engine_compatibility": "absent",
        },
        "fixtures": entries,
    }
    manifest_bytes = (json.dumps(manifest, ensure_ascii=False, indent=2) + "\n").encode("utf-8")
    (output_dir / "manifest.json").write_bytes(manifest_bytes)
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
