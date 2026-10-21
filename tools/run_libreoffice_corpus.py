#!/usr/bin/env python3
"""Run bounded, isolated LibreOffice conversions for synthetic OOXML variants."""

from __future__ import annotations

import argparse
import ctypes
import hashlib
import json
import os
import re
import sys
import tempfile
import time
import zipfile
from pathlib import Path, PurePosixPath
from typing import Any
from xml.etree import ElementTree

import probe_libreoffice_export as probe

REPOSITORY_ROOT = Path(__file__).resolve().parents[1]
DEFAULT_CORPUS = REPOSITORY_ROOT / "tests" / "fixtures" / "synthetic_documents"
FORMATS = {
    "docx": ("word/document.xml",),
    "xlsx": ("xl/workbook.xml", "xl/worksheets/sheet1.xml"),
    "pptx": ("ppt/presentation.xml", "ppt/slides/slide1.xml"),
}
VARIANT_ID = re.compile(r"^(docx|xlsx|pptx)_variant_(\d{3})$")
MAX_SELECTED_PER_FORMAT = 30
MAX_INPUT_BYTES = 1024 * 1024
MAX_TOTAL_INPUT_BYTES = 64 * 1024 * 1024
MAX_PACKAGE_ENTRIES = 256
MAX_PACKAGE_UNCOMPRESSED_BYTES = 8 * 1024 * 1024
MAX_LOG_CHARS = 2_000
MAX_PDF_BYTES = 64 * 1024 * 1024


def file_sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def validate_package(path: Path, file_format: str) -> str | None:
    try:
        with zipfile.ZipFile(path) as archive:
            infos = archive.infolist()
            if len(infos) > MAX_PACKAGE_ENTRIES:
                return "package entry count exceeds the bounded limit"
            total_size = sum(item.file_size for item in infos)
            if total_size > MAX_PACKAGE_UNCOMPRESSED_BYTES:
                return "package uncompressed size exceeds the bounded limit"
            names = set()
            for item in infos:
                name = item.filename
                normalized = PurePosixPath(name)
                if normalized.is_absolute() or ".." in normalized.parts or "\\" in name:
                    return "package contains an unsafe member path"
                if name in names:
                    return "package contains duplicate member names"
                names.add(name)
            if "[Content_Types].xml" not in names or "_rels/.rels" not in names:
                return "package is missing required OPC metadata"
            if not set(FORMATS[file_format]).issubset(names):
                return f"package is missing required {file_format.upper()} parts"
            if archive.testzip() is not None:
                return "package member CRC check failed"
            for name in names:
                lowered_name = name.lower()
                if "vbaproject" in lowered_name:
                    return "package contains macro-related content"
                if not name.endswith((".xml", ".rels")):
                    continue
                contents = archive.read(name)
                lowered = contents.lower()
                if b"vbaproject" in lowered or b"macroenabled" in lowered:
                    return "package contains macro-related content"
                root = ElementTree.fromstring(contents)
                if name.endswith(".rels"):
                    for relationship in root:
                        if relationship.attrib.get("TargetMode", "").lower() == "external":
                            return "package contains an external relationship"
            return None
    except (OSError, zipfile.BadZipFile, RuntimeError, ElementTree.ParseError):
        return "package is malformed or truncated"


def select_fixtures(manifest: dict[str, Any], corpus_dir: Path) -> tuple[list[dict[str, Any]], list[dict[str, str]]]:
    if manifest.get("synthetic") is not True or manifest.get("evidence_class") != "synthetic" or manifest.get("simulation_only") is not True:
        raise ValueError("corpus manifest is not explicitly synthetic and simulation-only")
    selected: list[dict[str, Any]] = []
    skipped: list[dict[str, str]] = []
    total_selected_bytes = 0
    fixtures = manifest.get("fixtures")
    if not isinstance(fixtures, list):
        raise ValueError("corpus manifest has no fixture list")
    for fixture in fixtures:
        fixture_id = fixture.get("id", "<missing-id>")
        match = VARIANT_ID.fullmatch(fixture_id)
        expected = fixture.get("expected_outcome", {})
        reason = None
        if fixture.get("evidence_class") != "synthetic" or fixture.get("simulation_only") is not True:
            reason = "manifest does not classify this record as synthetic simulation data"
        elif not match:
            reason = expected.get("reason") or "outside the selected scaled DOCX/XLSX/PPTX variant set"
        elif int(match.group(2)) >= MAX_SELECTED_PER_FORMAT:
            reason = "variant index exceeds the per-format selection cap"
        elif expected.get("status") != "accepted":
            reason = expected.get("reason") or f"manifest outcome is {expected.get('status', 'unspecified')}"
        elif fixture.get("format") != match.group(1):
            reason = "fixture ID and manifest format disagree"
        path_value = fixture.get("path")
        if reason is None and (not isinstance(path_value, str) or not path_value):
            reason = "manifest path is missing"
        if reason is None:
            relative = PurePosixPath(path_value)
            if relative.is_absolute() or ".." in relative.parts:
                reason = "manifest path escapes the corpus directory"
            else:
                path = corpus_dir.joinpath(*relative.parts)
                try:
                    path.resolve(strict=True).relative_to(corpus_dir.resolve(strict=True))
                except (OSError, ValueError):
                    reason = "fixture path is missing or resolves outside the corpus"
        if reason is None:
            size = path.stat().st_size
            if size <= 0 or size > MAX_INPUT_BYTES:
                reason = "input size is empty or exceeds the per-file bound"
            elif size != fixture.get("size_bytes"):
                raise ValueError(f"manifest size mismatch for {fixture_id}")
            elif file_sha256(path) != fixture.get("sha256"):
                raise ValueError(f"manifest digest mismatch for {fixture_id}")
            elif total_selected_bytes + size > MAX_TOTAL_INPUT_BYTES:
                reason = "selected corpus exceeds the total input byte bound"
            else:
                reason = validate_package(path, match.group(1))
        if reason is None:
            record = dict(fixture)
            record["absolutePath"] = str(path)
            record["manifestSha256"] = fixture["sha256"]
            selected.append(record)
            total_selected_bytes += size
        else:
            skipped.append({
                "id": fixture_id,
                "path": path_value or "",
                "sha256": fixture.get("sha256"),
                "sizeBytes": fixture.get("size_bytes"),
                "reason": str(reason),
            })
    selected.sort(key=lambda item: item["id"])
    return selected, skipped


def result_record(
    fixture: dict[str, Any], *, status: str, input_sha256: str,
    output_bytes: int | None, output_sha256: str | None, page_count: int | None,
    command: dict[str, Any] | None, reason: str | None = None,
    page_inspection_status: str = "structural-parser", structure_valid: bool = False,
) -> dict[str, Any]:
    if not reason and status == "failed":
        reason = _conversion_failure_reason(command, output_bytes, structure_valid)
    return {
        "inputId": fixture["id"],
        "inputPath": fixture["path"],
        "inputSha256": input_sha256,
        "manifestSha256": fixture["sha256"],
        "inputBytes": fixture["size_bytes"],
        "status": status,
        "exitCode": command.get("exitCode") if command else None,
        "timedOut": command.get("timedOut") if command else False,
        "launchAttempted": command.get("launchAttempted") if command else False,
        "identityVerified": command.get("identityVerified") if command else False,
        "identityFailure": command.get("identityFailure") if command else None,
        "durationSeconds": command.get("durationSeconds") if command else 0,
        "outputPdfBytes": output_bytes,
        "outputPdfSha256": output_sha256,
        "pageInspection": {"status": page_inspection_status, "structureValid": structure_valid, "pageCount": page_count},
        "stdout": _bounded_log(command.get("stdout", "")) if command else "",
        "stderr": _bounded_log(command.get("stderr", "")) if command else "",
        "reason": reason,
    }


def _conversion_failure_reason(
    command: dict[str, Any] | None, output_bytes: int | None, structure_valid: bool,
) -> str:
    if command is None:
        return "LibreOffice was not launched; no output PDF was produced."
    if command.get("budgetExpiredBeforeLaunch"):
        return "total corpus runtime budget exhausted before LibreOffice launch."
    if command.get("launchAttempted") is False:
        detail = command.get("stderr") or command.get("identityFailure") or "runtime preparation failed"
        return f"LibreOffice was not launched; {_bounded_log(str(detail))}."
    if command.get("timedOut"):
        process = f"LibreOffice exceeded its per-file timeout after {command.get('durationSeconds')} seconds"
    else:
        exit_code = command.get("exitCode")
        if isinstance(exit_code, int) and exit_code < 0:
            try:
                signal_name = probe.signal.Signals(-exit_code).name
            except ValueError:
                signal_name = f"signal {-exit_code}"
            process = f"LibreOffice terminated by {signal_name}"
        elif exit_code is None:
            process = "LibreOffice did not report an exit status"
        else:
            process = f"LibreOffice exited with code {exit_code} after {command.get('durationSeconds')} seconds"
    output = "no output PDF was produced" if output_bytes is None else f"output PDF was {output_bytes} bytes"
    if output_bytes is not None and not structure_valid:
        output += " but failed PDF structure validation"
    if command.get("identityVerified") is False:
        identity_failure = command.get("identityFailure") or command.get("stderr") or "pinned engine/runtime identity could not be verified"
        return f"LibreOffice output was rejected because {_bounded_log(str(identity_failure))}; {output}."
    empty_logs = [name for name in ("stdout", "stderr") if not command.get(name)]
    details = f"; {', '.join(empty_logs)} empty" if empty_logs else ""
    return f"{process}; {output}{details}."


def _bounded_log(value: str) -> str:
    if len(value) <= MAX_LOG_CHARS:
        return value
    return value[:MAX_LOG_CHARS] + " [truncated]"


def classify_report(
    *, selected: list[dict[str, Any]], skipped: list[dict[str, str]],
    results: list[dict[str, Any]], engine: dict[str, Any],
) -> dict[str, Any]:
    return {
        "schemaVersion": "1.0",
        "evidenceClass": "synthetic-source-and-real-libreoffice-execution",
        "classification": "LibreOffice conversion is engine execution only; it is not fidelity review or Office/WPS/device/physical output evidence.",
        "engine": engine,
        "limits": {
            "selectedPerFormat": MAX_SELECTED_PER_FORMAT,
            "maximumInputBytesPerFile": MAX_INPUT_BYTES,
            "maximumSelectedInputBytes": MAX_TOTAL_INPUT_BYTES,
            "maximumPackageEntries": MAX_PACKAGE_ENTRIES,
            "maximumPackageUncompressedBytes": MAX_PACKAGE_UNCOMPRESSED_BYTES,
            "maximumOutputPdfBytes": MAX_PDF_BYTES,
            "maximumLogCharactersPerStream": MAX_LOG_CHARS,
        },
        "counts": {
            "syntheticSourceSpecimens": len(selected) + len(skipped),
            "selectedForLibreOffice": len(selected),
            "successfulLibreOfficeConversions": sum(result["status"] == "converted" for result in results),
            "failedLibreOfficeConversions": sum(result["status"] == "failed" for result in results),
            "humanReviewedFidelity": 0,
            "officeWpsRuns": 0,
            "oemDriverDeviceRuns": 0,
            "physicalOutputRuns": 0,
            "compatibilityEvidenceCount": 0,
            "realEngineDeviceEvidenceCount": 0,
        },
        "evidenceGates": {"G1": "open", "G3": "open", "G4": "open", "U8NativeDeviceCleanPc": "open"},
        "skipped": skipped,
        "results": results,
    }


def _pdfium_page_count(pdf_path: Path) -> tuple[int | None, str]:
    library = os.environ.get("PAPERBATCH_PDFIUM_TEST_PATH")
    expected_sha256 = os.environ.get("PAPERBATCH_PDFIUM_TEST_SHA256")
    if not library or not expected_sha256:
        return None, "pinned-pdfium-unavailable"
    library_path = Path(library)
    try:
        if not re.fullmatch(r"[0-9a-fA-F]{64}", expected_sha256):
            return None, "pinned-pdfium-invalid-digest-pin"
        if file_sha256(library_path) != expected_sha256.lower():
            return None, "pinned-pdfium-digest-mismatch"
        pdfium = ctypes.CDLL(str(library_path))
        pdfium.FPDF_InitLibrary()
        try:
            pdfium.FPDF_LoadDocument.argtypes = [ctypes.c_char_p, ctypes.c_char_p]
            pdfium.FPDF_LoadDocument.restype = ctypes.c_void_p
            pdfium.FPDF_GetPageCount.argtypes = [ctypes.c_void_p]
            pdfium.FPDF_GetPageCount.restype = ctypes.c_int
            pdfium.FPDF_CloseDocument.argtypes = [ctypes.c_void_p]
            document = pdfium.FPDF_LoadDocument(os.fsencode(pdf_path), None)
            if not document:
                return None, "pinned-pdfium-rejected-output"
            try:
                return pdfium.FPDF_GetPageCount(document), "pinned-pdfium"
            finally:
                pdfium.FPDF_CloseDocument(document)
        finally:
            pdfium.FPDF_DestroyLibrary()
    except (OSError, AttributeError, ctypes.ArgumentError):
        return None, "pinned-pdfium-inspection-failed"


def run_corpus(engine_binary: str, expected_engine_sha256: str, timeout_seconds: int, budget_seconds: int, corpus_dir: Path) -> tuple[dict[str, Any], int]:
    if not sys.platform.startswith("linux"):
        raise ValueError("the corpus runner requires Linux for its no-network process sandbox")
    started = time.monotonic()
    deadline = started + budget_seconds
    manifest_path = corpus_dir / "manifest.json"
    manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
    selected, skipped = select_fixtures(manifest, corpus_dir)
    engine_path = Path(engine_binary).expanduser().resolve(strict=True)
    pinned = probe.open_pinned_engine(engine_path, expected_engine_sha256)
    results: list[dict[str, Any]] = []
    try:
        with tempfile.TemporaryDirectory(prefix="paperbatch-lo-corpus-") as temporary:
            root = Path(temporary)
            remaining = deadline - time.monotonic()
            if remaining > 0:
                version = probe.invoke(engine_path, ["--version"], root / "version-profile",
                                       min(timeout_seconds, remaining), expected_engine_sha256=expected_engine_sha256,
                                       pinned_engine=pinned, max_output_file_bytes=MAX_PDF_BYTES,
                                       max_log_chars=MAX_LOG_CHARS, disable_network=True,
                                       deadline_monotonic=deadline)
            else:
                version = {"exitCode": None, "timedOut": False, "durationSeconds": 0,
                           "identityVerified": False, "stderr": "total corpus runtime budget exhausted before version probe"}
            version_output = version.get("stdout") or version.get("stderr") or ""
            version_ok = (not version.get("timedOut") and version.get("exitCode") == 0
                          and version.get("identityVerified") and re.match(r"^LibreOffice(?:Dev)?\s+\S+", version_output, re.I))
            engine = {"version": version_output if version_ok else None, "binarySha256": pinned.sha256,
                      "expectedBinarySha256": expected_engine_sha256.lower(),
                      "runtimeDirectorySha256": pinned.runtime_directory_sha256,
                      "versionCommand": {key: version.get(key) for key in ("exitCode", "timedOut", "durationSeconds")}}
            if not version_ok:
                for fixture in selected:
                    results.append(result_record(fixture, status="failed", input_sha256=fixture["sha256"], output_bytes=None,
                                                 output_sha256=None, page_count=None, command=version,
                                                 reason="pinned engine version verification failed"))
            else:
                for index, fixture in enumerate(selected):
                    remaining = deadline - time.monotonic()
                    if remaining <= 0:
                        for pending in selected[index:]:
                            results.append(result_record(pending, status="failed", input_sha256=pending["sha256"],
                                                         output_bytes=None, output_sha256=None, page_count=None, command=None,
                                                         reason="total corpus runtime budget exhausted"))
                        break
                    source = Path(fixture["absolutePath"])
                    source_bytes = source.read_bytes()
                    input_sha256 = hashlib.sha256(source_bytes).hexdigest()
                    if input_sha256 != fixture["sha256"]:
                        results.append(result_record(fixture, status="failed", input_sha256=input_sha256, output_bytes=None,
                                                     output_sha256=None, page_count=None, command=None,
                                                     reason="input digest changed after manifest selection"))
                        continue
                    run_dir = root / f"case-{index:03d}"
                    output_dir = run_dir / "output"
                    temporary_directory = run_dir / "intermediate"
                    output_dir.mkdir(parents=True)
                    temporary_directory.mkdir(parents=True)
                    copied_input = run_dir / Path(fixture["path"]).name
                    copied_input.write_bytes(source_bytes)
                    remaining = deadline - time.monotonic()
                    if remaining <= 0:
                        results.append(result_record(fixture, status="failed", input_sha256=input_sha256,
                            output_bytes=None, output_sha256=None, page_count=None, command=None,
                            reason="total corpus runtime budget exhausted"))
                        continue
                    command = probe.invoke(engine_path,
                        ["--convert-to", "pdf", "--outdir", str(output_dir), str(copied_input)],
                        run_dir / "profile", min(timeout_seconds, remaining),
                        expected_engine_sha256=expected_engine_sha256, pinned_engine=pinned,
                        max_output_file_bytes=MAX_PDF_BYTES, max_log_chars=MAX_LOG_CHARS, disable_network=True,
                        temporary_directory=temporary_directory, use_launcher=True,
                        deadline_monotonic=deadline)
                    if command.get("budgetExpiredBeforeLaunch") or command.get("launchAttempted") is False:
                        results.append(result_record(fixture, status="failed", input_sha256=input_sha256,
                            output_bytes=None, output_sha256=None, page_count=None, command=command,
                            reason=_conversion_failure_reason(command, None, False)))
                        continue
                    if time.monotonic() >= deadline:
                        results.append(result_record(fixture, status="failed", input_sha256=input_sha256,
                            output_bytes=None, output_sha256=None, page_count=None, command=command,
                            reason="total corpus runtime budget exhausted during conversion"))
                        continue
                    pdf = output_dir / f"{copied_input.stem}.pdf"
                    size, header, structure_valid, _text_present, output_hash = probe.inspect_pdf(pdf, expected_text=None)
                    if size is not None and size > MAX_PDF_BYTES:
                        structure_valid = False
                        output_hash = None
                    page_count = None
                    pdfium_status = "structural-parser"
                    if structure_valid:
                        page_count, pdfium_status = _pdfium_page_count(pdf)
                    success = (not command.get("timedOut") and command.get("exitCode") == 0
                               and command.get("identityVerified") and structure_valid and header and output_hash)
                    reason = None if success else _conversion_failure_reason(command, size, structure_valid)
                    results.append(result_record(fixture, status="converted" if success else "failed",
                        input_sha256=input_sha256, output_bytes=size, output_sha256=output_hash,
                        page_count=page_count, command=command, reason=reason, page_inspection_status=pdfium_status,
                        structure_valid=structure_valid))
            report = classify_report(selected=selected, skipped=skipped, results=results, engine=engine)
            report["elapsedSeconds"] = round(time.monotonic() - started, 3)
            return report, 0 if report["counts"]["failedLibreOfficeConversions"] == 0 else 1
    finally:
        pinned.close()


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--engine-binary", required=True)
    parser.add_argument("--expected-engine-sha256", required=True)
    parser.add_argument("--timeout-seconds", type=int, default=60)
    parser.add_argument("--budget-seconds", type=int, default=900)
    parser.add_argument("--corpus-dir", type=Path, default=DEFAULT_CORPUS)
    parser.add_argument("--output", type=Path)
    args = parser.parse_args(argv)
    if not 1 <= args.timeout_seconds <= 300:
        parser.error("--timeout-seconds must be between 1 and 300")
    if not 1 <= args.budget_seconds <= 1800:
        parser.error("--budget-seconds must be between 1 and 1800")
    try:
        report, exit_code = run_corpus(args.engine_binary, args.expected_engine_sha256, args.timeout_seconds,
                                       args.budget_seconds, args.corpus_dir)
    except (OSError, ValueError, json.JSONDecodeError, zipfile.BadZipFile) as error:
        report, exit_code = {"schemaVersion": "1.0", "status": "blocked", "reason": str(error),
                             "compatibilityEvidenceCount": 0}, 1
    output = json.dumps(report, ensure_ascii=False, indent=2) + "\n"
    if args.output:
        args.output.write_text(output, encoding="utf-8")
    else:
        sys.stdout.write(output)
    return exit_code


if __name__ == "__main__":
    raise SystemExit(main())
