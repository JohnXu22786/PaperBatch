import contextlib
import hashlib
import io
import json
import os
import sys
import tempfile
import threading
import unittest
import zipfile
from unittest import mock
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "tools"))
import probe_libreoffice_export


class PdfSuffixSliceTrackingBytes(bytes):
    def __new__(cls, value: bytes, xref_offset: int):
        instance = super().__new__(cls, value)
        instance.xref_offset = xref_offset
        instance.full_suffix_slice_count = 0
        return instance

    def __getitem__(self, key):
        if (
            isinstance(key, slice)
            and key.start is not None
            and key.start < self.xref_offset
            and key.stop in (None, self.xref_offset)
        ):
            self.full_suffix_slice_count += 1
        return super().__getitem__(key)


class XrefSectionWithoutSplitlines(bytes):
    def splitlines(self, *args, **kwargs):
        raise AssertionError("the xref parser must not materialize every section line")


class PdfXrefSplitlinesGuardingBytes(bytes):
    def __new__(cls, value: bytes, xref_offset: int, startxref_position: int):
        instance = super().__new__(cls, value)
        instance.xref_offset = xref_offset
        instance.startxref_position = startxref_position
        return instance

    def __getitem__(self, key):
        value = super().__getitem__(key)
        if (
            isinstance(key, slice)
            and key.start == self.xref_offset
            and key.stop == self.startxref_position
        ):
            return XrefSectionWithoutSplitlines(value)
        return value


def minimal_pdf(
    root_type: str = "Catalog",
    root_extra: bytes = b"",
    root_generation: int = 0,
    trailer_suffix: bytes = b"",
    page_count: int = 1,
    page_content: bytes = b"BT /F1 11 Tf (PaperBatch U0 export probe) Tj ET\n",
    page_content_streams: list[bytes] | None = None,
    page_contents: list[bytes] | None = None,
    unreferenced_content: bytes | None = None,
    page_dictionary_extra: bytes = b"",
    extra_objects: list[bytes] | None = None,
    shared_page_stream: bool = False,
) -> bytes:
    header = b"%PDF-1.7\n"
    objects = [
        (
            f"1 {root_generation} obj\n<< /Type /{root_type} ".encode("ascii")
            + root_extra
            + b" /Pages 2 0 R >>\nendobj\n"
        ),
        b"",
    ]
    if page_count > 0:
        content_streams = page_content_streams if page_content_streams is not None else [page_content]
        if page_contents is not None and len(page_contents) != page_count:
            raise ValueError("page_contents must have one content body per page")
        page_stream_groups = (
            [[content] for content in page_contents]
            if page_contents is not None
            else [content_streams for _ in range(page_count)]
        )
        if shared_page_stream:
            if page_contents is not None or len(content_streams) != 1:
                raise ValueError("shared_page_stream requires one page_content_stream")
            page_stream_groups = [[content_streams[0]] for _ in range(page_count)]
        page_references = b" ".join(f"{number} 0 R".encode("ascii") for number in range(3, 3 + page_count))
        inherited_resources = b""
        if b"/Resources" not in page_dictionary_extra:
            inherited_resources = b"/Resources << /Font << /F1 __DEFAULT_FONT__ 0 R >> >> "
        objects[1] = (
            b"2 0 obj\n<< /Type /Pages /Kids ["
            + page_references
            + b"] /Count "
            + str(page_count).encode("ascii")
            + b" "
            + inherited_resources
            + b">>\nendobj\n"
        )
        next_stream_number = 3 + page_count
        shared_stream_number = next_stream_number if shared_page_stream else None
        if shared_page_stream:
            next_stream_number += 1
        page_stream_numbers: list[list[int]] = []
        for page_index in range(page_count):
            page_number = 3 + page_index
            if shared_stream_number is not None:
                stream_numbers = [shared_stream_number]
            else:
                stream_numbers = list(range(next_stream_number, next_stream_number + len(page_stream_groups[page_index])))
                next_stream_number += len(stream_numbers)
            page_stream_numbers.append(stream_numbers)
            stream_references = b" ".join(
                f"{number} 0 R".encode("ascii")
                for number in stream_numbers
            )
            content_reference_value = stream_references
            if len(content_streams) != 1:
                content_reference_value = b"[" + stream_references + b"]"
            objects.append(
                f"{page_number} 0 obj\n".encode("ascii")
                + b"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents "
                + content_reference_value
                + b" "
                + page_dictionary_extra
                + b">>\nendobj\n"
            )
        if shared_stream_number is not None:
            content = page_stream_groups[0][0]
            objects.append(
                f"{shared_stream_number} 0 obj\n<< /Length {len(content)} >>\nstream\n".encode("ascii")
                + content
                + b"\nendstream\nendobj\n"
            )
        else:
            for page_index in range(page_count):
                for number, content in zip(page_stream_numbers[page_index], page_stream_groups[page_index]):
                    objects.append(
                        f"{number} 0 obj\n<< /Length {len(content)} >>\nstream\n".encode("ascii")
                        + content
                        + b"\nendstream\nendobj\n"
                    )
    else:
        objects[1] = b"2 0 obj\n<< /Type /Pages /Kids [] /Count 0 >>\nendobj\n"

    if unreferenced_content is not None:
        objects.append(
            f"{len(objects) + 1} 0 obj\n<< /Length {len(unreferenced_content)} >>\nstream\n".encode("ascii")
            + unreferenced_content
            + b"\nendstream\nendobj\n"
        )
    if extra_objects is not None:
        objects.extend(extra_objects)
    if page_count > 0 and b"/Resources" not in page_dictionary_extra:
        default_font_number = len(objects) + 1
        objects[1] = objects[1].replace(b"__DEFAULT_FONT__", str(default_font_number).encode("ascii"), 1)
        objects.append(
            (
                f"{default_font_number} 0 obj\n"
                "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>\n"
                "endobj\n"
            ).encode("ascii")
        )

    offsets = []
    data = bytearray(header)
    for obj in objects:
        offsets.append(len(data))
        data.extend(obj)
    xref_offset = len(data)
    xref = bytearray(f"xref\n0 {len(objects) + 1}\n0000000000 65535 f \n".encode("ascii"))
    for index, offset in enumerate(offsets, start=1):
        generation = root_generation if index == 1 else 0
        xref.extend(f"{offset:010d} {generation:05d} n \n".encode("ascii"))
    trailer = (
        f"trailer\n<< /Size {len(objects) + 1} /Root 1 ".encode("ascii")
        + str(root_generation).encode("ascii")
        + b" R >>\n"
        + trailer_suffix
        + b"startxref\n"
        + str(xref_offset).encode("ascii")
        + b"\n%%EOF\n"
    )
    return bytes(data) + bytes(xref) + trailer


def pdf_with_tounicode_cmap(cmap: bytes, page_contents: list[bytes]) -> tuple[bytes, tuple[int, int]]:
    page_count = len(page_contents)
    resources_number = 3 + (2 * page_count)
    font_number = resources_number + 1
    cmap_number = font_number + 1
    resource_dictionary = (
        f"{resources_number} 0 obj\n<< /Font << /F1 {font_number} 0 R >> >>\nendobj\n".encode("ascii")
    )
    font_dictionary = (
        f"{font_number} 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Fixture /Encoding /StandardEncoding /ToUnicode {cmap_number} 0 R >>\nendobj\n".encode("ascii")
    )
    cmap_stream = (
        f"{cmap_number} 0 obj\n<< /Length {len(cmap)} >>\nstream\n".encode("ascii")
        + cmap
        + b"\nendstream\nendobj\n"
    )
    pdf = minimal_pdf(
        page_count=page_count,
        page_contents=page_contents,
        page_dictionary_extra=f"/Resources {resources_number} 0 R ".encode("ascii"),
        extra_objects=[resource_dictionary, font_dictionary, cmap_stream],
    )
    return pdf, (cmap_number, 0)


def pdf_with_type1_font(base_font: bytes, page_content: bytes) -> bytes:
    resource_dictionary = b"5 0 obj\n<< /Font << /F1 6 0 R >> >>\nendobj\n"
    font_dictionary = (
        b"6 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /"
        + base_font
        + b" >>\nendobj\n"
    )
    return minimal_pdf(
        page_content=page_content,
        page_dictionary_extra=b"/Resources 5 0 R ",
        extra_objects=[resource_dictionary, font_dictionary],
    )


def run_pinned_probe(executable: Path) -> tuple[dict[str, object], int]:
    digest = hashlib.sha256(executable.read_bytes()).hexdigest()
    return probe_libreoffice_export.run_probe(
        None,
        timeout_seconds=5,
        engine_binary=str(executable),
        expected_engine_sha256=digest,
    )


def fixture_classic_xref_is_valid(
    data: bytes,
    expected_text: str,
    expected_content_observation: list[bool],
) -> bool:
    startxref_position = data.rfind(b"startxref\n")
    if startxref_position < 0:
        return False
    xref_line = data[startxref_position + len(b"startxref\n"):].splitlines()
    if not xref_line:
        return False
    try:
        xref_offset = int(xref_line[0])
    except ValueError:
        return False
    return probe_libreoffice_export.classic_xref_is_valid(
        data,
        xref_offset,
        startxref_position,
        expected_text=expected_text,
        expected_content_observation=expected_content_observation,
    )


class LibreOfficeProbeFixtureTests(unittest.TestCase):
    @unittest.skipUnless(sys.platform.startswith("linux") and os.uname().machine.lower() in {"x86_64", "amd64"}, "Linux x86_64 seccomp regression")
    def test_launcher_mode_uses_verified_runtime_wrapper_under_no_network_sandbox(self):
        with tempfile.TemporaryDirectory(prefix="paperbatch-lo-launcher-mode-test-") as directory:
            program = Path(directory) / "program"
            program.mkdir()
            engine = program / "soffice.bin"
            launcher = program / "soffice"
            engine.write_text("#!/bin/sh\n[ \"$1\" = --wrapper-start ] || exit 81\nprintf 'pinned-engine-started\\n'\n", encoding="utf-8")
            launcher.write_text("#!/bin/sh\nexec \"$(dirname \"$0\")/soffice.bin\" --wrapper-start\n", encoding="utf-8")
            engine.chmod(0o700)
            launcher.chmod(0o700)
            digest = hashlib.sha256(engine.read_bytes()).hexdigest()

            direct = probe_libreoffice_export.invoke(
                engine, ["--version"], Path(directory) / "direct-profile", 5,
                expected_engine_sha256=digest, disable_network=True, max_log_chars=100,
            )
            wrapped = probe_libreoffice_export.invoke(
                engine, ["--version"], Path(directory) / "wrapper-profile", 5,
                expected_engine_sha256=digest, disable_network=True, max_log_chars=100, use_launcher=True,
                temporary_directory=Path(directory) / "intermediate",
            )

        self.assertEqual(direct["exitCode"], 81)
        self.assertEqual(wrapped["exitCode"], 0)
        self.assertEqual(wrapped["stdout"], "pinned-engine-started")
        self.assertTrue(wrapped["executionTarget"].endswith("/soffice"))
        self.assertTrue(wrapped["identityVerified"])

    @unittest.skipUnless(sys.platform.startswith("linux"), "Linux runtime deadline regression")
    def test_expired_absolute_deadline_does_not_launch_after_runtime_preparation(self):
        with tempfile.TemporaryDirectory(prefix="paperbatch-lo-deadline-test-") as directory:
            engine = Path(directory) / "soffice.bin"
            engine.write_text("#!/bin/sh\nexit 0\n", encoding="utf-8")
            engine.chmod(0o700)
            digest = hashlib.sha256(engine.read_bytes()).hexdigest()

            with mock.patch.object(probe_libreoffice_export.subprocess, "run") as run_mock:
                result = probe_libreoffice_export.invoke(
                    engine, ["--version"], Path(directory) / "profile", 5,
                    expected_engine_sha256=digest, deadline_monotonic=0,
                )

        run_mock.assert_not_called()
        self.assertTrue(result["budgetExpiredBeforeLaunch"])
        self.assertFalse(result["timedOut"])
        self.assertIsNone(result["exitCode"])

    @unittest.skipUnless(sys.platform.startswith("linux"), "Linux post-process identity regression")
    def test_post_process_identity_failure_preserves_exit_and_bounded_logs(self):
        with tempfile.TemporaryDirectory(prefix="paperbatch-lo-post-identity-test-") as directory:
            engine = Path(directory) / "soffice.bin"
            engine.write_text("#!/bin/sh\nprintf 'conversion-finished\\n'\nexit 0\n", encoding="utf-8")
            engine.chmod(0o700)
            digest = hashlib.sha256(engine.read_bytes()).hexdigest()
            verify = probe_libreoffice_export.PinnedEngine.verify_runtime_directory
            calls = 0

            def fail_after_process(instance, **kwargs):
                nonlocal calls
                calls += 1
                if calls == 2:
                    raise probe_libreoffice_export.PinnedEngineError("runtime identity changed")
                return verify(instance, **kwargs)

            with mock.patch.object(
                probe_libreoffice_export.PinnedEngine,
                "verify_runtime_directory",
                autospec=True,
                side_effect=fail_after_process,
            ):
                result = probe_libreoffice_export.invoke(
                    engine, ["--version"], Path(directory) / "profile", 5,
                    expected_engine_sha256=digest, max_log_chars=100,
                )

        self.assertEqual(result["exitCode"], 0)
        self.assertEqual(result["stdout"], "conversion-finished")
        self.assertIn("identity verification failed", result["stderr"])
        self.assertFalse(result["identityVerified"])
        self.assertTrue(result["launchAttempted"])

    def test_classic_xref_rejects_an_in_use_object_zero_catalog(self):
        valid_pdf = minimal_pdf()
        xref_offset = valid_pdf.index(b"xref\n")
        startxref_position = valid_pdf.rfind(b"startxref\n")
        xref_lines = valid_pdf[xref_offset:startxref_position].splitlines(keepends=True)
        self.assertGreaterEqual(len(xref_lines), 4)
        self.assertEqual(b"xref\n", xref_lines[0])

        root_offset = valid_pdf.index(b"1 0 obj\n")
        malformed_xref = b"".join(
            xref_lines[:2]
            + [f"{root_offset:010d} 00000 n \n".encode("ascii")]
            + [b"0000000000 00001 f \n"]
            + xref_lines[4:]
        )
        malformed_pdf = (
            valid_pdf[:xref_offset]
            + malformed_xref
            + valid_pdf[startxref_position:]
        )
        malformed_pdf = malformed_pdf.replace(b"1 0 obj\n<< /Type /Catalog", b"0 0 obj\n<< /Type /Catalog", 1)
        malformed_pdf = malformed_pdf.replace(b"/Root 1 0 R", b"/Root 0 0 R", 1)

        expected_content: list[bool] = []
        self.assertFalse(fixture_classic_xref_is_valid(
            malformed_pdf,
            expected_text="PaperBatch U0 export probe",
            expected_content_observation=expected_content,
        ))
        self.assertEqual([], expected_content)

    def test_classic_xref_object_zero_requires_free_generation_65535(self):
        malformed_pdf = minimal_pdf().replace(
            b"0000000000 65535 f ",
            b"0000000000 00000 f ",
            1,
        )
        expected_content: list[bool] = []

        self.assertFalse(fixture_classic_xref_is_valid(
            malformed_pdf,
            expected_text="PaperBatch U0 export probe",
            expected_content_observation=expected_content,
        ))
        self.assertEqual([], expected_content)

    def test_xref_object_validation_does_not_copy_pdf_suffix_per_object(self):
        pdf = minimal_pdf()
        startxref = pdf.rfind(b"startxref\n")
        xref_offset = int(pdf[startxref + len(b"startxref\n"):].splitlines()[0])
        tracked_pdf = PdfSuffixSliceTrackingBytes(pdf, xref_offset)
        expected_content: list[bool] = []

        self.assertTrue(fixture_classic_xref_is_valid(
            tracked_pdf,
            expected_text="PaperBatch U0 export probe",
            expected_content_observation=expected_content,
        ))
        self.assertEqual([True], expected_content)
        self.assertEqual(0, tracked_pdf.full_suffix_slice_count)

    def test_classic_xref_parser_does_not_materialize_the_section_lines(self):
        pdf = minimal_pdf()
        startxref = pdf.rfind(b"startxref\n")
        xref_offset = int(pdf[startxref + len(b"startxref\n"):].splitlines()[0])
        guarded_pdf = PdfXrefSplitlinesGuardingBytes(pdf, xref_offset, startxref)
        expected_content: list[bool] = []

        self.assertTrue(fixture_classic_xref_is_valid(
            guarded_pdf,
            expected_text="PaperBatch U0 export probe",
            expected_content_observation=expected_content,
        ))
        self.assertEqual([True], expected_content)

    def test_inline_image_payload_cannot_supply_fixture_text_show_evidence(self):
        image_payload = b"\x00BT /F1 11 Tf (PaperBatch U0 export probe) Tj ET\xff"
        content = b"BI /W 8 /H 1 /BPC 8 /CS /G ID\n" + image_payload + b"\nEI\n"

        self.assertFalse(probe_libreoffice_export.pdf_streams_contain_expected_text(
            [content],
            "PaperBatch U0 export probe",
            font_names={b"F1"},
            ascii_font_names={b"F1"},
        ))

    def test_content_stream_array_preserves_text_state_across_stream_boundaries(self):
        pdf = minimal_pdf(
            page_content_streams=[
                b"BT /F1 11 Tf ",
                b"(PaperBatch U0 export probe) Tj ET\n",
            ]
        )
        expected_content: list[bool] = []

        self.assertTrue(fixture_classic_xref_is_valid(
            pdf,
            expected_text="PaperBatch U0 export probe",
            expected_content_observation=expected_content,
        ))
        self.assertEqual([True], expected_content)

    def test_content_token_budget_rejects_excess_tokens(self):
        content = b"q " * (probe_libreoffice_export.MAX_PDF_PARSE_TOKENS + 1)

        with self.assertRaises(ValueError):
            probe_libreoffice_export._pdf_content_tokens(content)

    def test_page_content_stream_array_obeys_aggregate_decoded_byte_budget(self):
        first = b"BT /F1 11 Tf (PaperBatch " + b" " * 40
        second = b" U0 export probe) Tj ET\n"
        pdf = minimal_pdf(page_content_streams=[first, second])
        expected_content: list[bool] = []

        with mock.patch.object(
            probe_libreoffice_export,
            "MAX_PDF_PAGE_CONTENT_BYTES",
            64,
            create=True,
        ):
            self.assertTrue(fixture_classic_xref_is_valid(
                pdf,
                expected_text="PaperBatch U0 export probe",
                expected_content_observation=expected_content,
            ))

        self.assertEqual([False], expected_content)

    def test_shared_cmap_is_decoded_once_for_multiple_fonts(self):
        cmap = b"begincmap\n1 beginbfchar\n<01> <0050>\nendbfchar\nendcmap"
        content = b"BT /F1 11 Tf <01> Tj /F2 11 Tf <01> Tj ET"
        original_parser = probe_libreoffice_export.parse_tounicode_bfchar_map

        with mock.patch.object(
            probe_libreoffice_export,
            "parse_tounicode_bfchar_map",
            wraps=original_parser,
        ) as parse_cmap:
            result = probe_libreoffice_export.pdf_streams_contain_expected_text(
                [content],
                "P",
                {b"F1": cmap, b"F2": cmap},
                {b"F1", b"F2"},
            )

        self.assertTrue(result)
        self.assertEqual(1, parse_cmap.call_count)

    def test_aggregate_cmap_budget_rejects_unique_stream_expansion(self):
        content = b"BT /F1 11 Tf (P) Tj ET"
        with mock.patch.object(
            probe_libreoffice_export,
            "MAX_PDF_TOTAL_CMAP_BYTES",
            6,
            create=True,
        ):
            self.assertFalse(probe_libreoffice_export.pdf_streams_contain_expected_text(
                [content],
                "P",
                {b"F1": b"1234", b"F2": b"5678"},
                {b"F1", b"F2"},
                {b"F1", b"F2"},
            ))

    def test_shared_cmap_reference_is_read_once(self):
        content = b"BT /F1 11 Tf <01> Tj /F2 11 Tf <01> Tj ET"
        cmap = b"begincmap\n1 beginbfchar\n<01> <0050>\nendbfchar\nendcmap"
        content_reference = (1, 0)
        cmap_reference = (2, 0)
        read_calls = []

        def read_stream(_data, _entries, reference, _xref):
            read_calls.append(reference)
            return {}, content if reference == content_reference else cmap

        with mock.patch.object(
            probe_libreoffice_export,
            "read_indirect_stream",
            side_effect=read_stream,
        ):
            result = probe_libreoffice_export.pdf_content_references_contain_expected_text(
                b"",
                {},
                0,
                [content_reference],
                {b"F1", b"F2"},
                set(),
                [(b"F1", cmap_reference), (b"F2", cmap_reference)],
                "P",
            )

        self.assertTrue(result)
        self.assertEqual(1, read_calls.count(cmap_reference))

    def test_cmap_comment_marker_cannot_verify_page_text(self):
        invalid_cmap = b"% begincmap\n1 beginbfchar\n<01> <0050>\nendbfchar\nendcmap"
        pdf, _cmap_reference = pdf_with_tounicode_cmap(
            invalid_cmap,
            [b"BT /F1 11 Tf <01> Tj ET"],
        )
        expected_content: list[bool] = []

        self.assertTrue(fixture_classic_xref_is_valid(
            pdf,
            expected_text="P",
            expected_content_observation=expected_content,
        ))
        self.assertEqual([False], expected_content)

    def test_shared_cmap_is_decoded_once_across_pages(self):
        cmap = (
            b"begincmap\n2 beginbfchar\n<01> <0050>\n<02> <0051>\n"
            b"endbfchar\nendcmap"
        )
        pdf, cmap_reference = pdf_with_tounicode_cmap(
            cmap,
            [b"BT /F1 11 Tf <02> Tj ET", b"BT /F1 11 Tf <01> Tj ET"],
        )
        expected_content: list[bool] = []
        original_reader = probe_libreoffice_export.read_indirect_stream
        cmap_reads = 0

        def track_cmap_reads(data, entries, reference, xref_offset):
            nonlocal cmap_reads
            if reference == cmap_reference:
                cmap_reads += 1
            return original_reader(data, entries, reference, xref_offset)

        with mock.patch.object(
            probe_libreoffice_export,
            "read_indirect_stream",
            side_effect=track_cmap_reads,
        ):
            valid = fixture_classic_xref_is_valid(
                pdf,
                expected_text="P",
                expected_content_observation=expected_content,
            )

        self.assertTrue(valid)
        self.assertEqual([True], expected_content)
        self.assertEqual(1, cmap_reads)

    def test_document_cmap_budget_accumulates_unique_streams(self):
        state = probe_libreoffice_export.PdfCMapInspectionState()
        streams = {(1, 0): b"1234", (2, 0): b"5678"}

        with (
            mock.patch.object(probe_libreoffice_export, "MAX_PDF_TOTAL_CMAP_BYTES", 6, create=True),
            mock.patch.object(
                probe_libreoffice_export,
                "read_indirect_stream",
                side_effect=lambda _data, _entries, reference, _xref: ({}, streams[reference]),
            ),
        ):
            first = state.read_cmap(b"", {}, (1, 0), 0)
            second = state.read_cmap(b"", {}, (2, 0), 0)

        self.assertEqual(b"1234", first)
        self.assertIsNone(second)
        self.assertTrue(state.exceeded)
        self.assertEqual(4, state.total_decoded_bytes)

    def test_text_font_and_render_state_outside_text_objects_is_ignored(self):
        outside_font = b"/F1 11 Tf BT (PaperBatch U0 export probe) Tj ET"
        outside_render_reset = (
            b"BT /F1 11 Tf 3 Tr ET 0 Tr BT (PaperBatch U0 export probe) Tj ET"
        )
        for content in (outside_font, outside_render_reset):
            with self.subTest(content=content):
                self.assertFalse(probe_libreoffice_export.pdf_streams_contain_expected_text(
                    [content],
                    "PaperBatch U0 export probe",
                    font_names={b"F1"},
                    ascii_font_names={b"F1"},
                ))

    def test_page_text_observations_are_processed_incrementally(self):
        pdf = minimal_pdf(page_count=32)
        expected_content: list[bool] = []
        original_tree_inspector = probe_libreoffice_export.pdf_page_tree_has_nonempty_content

        with mock.patch.object(
            probe_libreoffice_export,
            "pdf_page_tree_has_nonempty_content",
            wraps=original_tree_inspector,
        ) as inspect_page_tree:
            valid = fixture_classic_xref_is_valid(
                pdf,
                expected_text="PaperBatch U0 export probe",
                expected_content_observation=expected_content,
            )

        self.assertTrue(valid)
        self.assertEqual([True], expected_content)
        self.assertEqual("PaperBatch U0 export probe", inspect_page_tree.call_args.kwargs["expected_text"])
        self.assertNotIn("page_observations_observation", inspect_page_tree.call_args.kwargs)

    def test_tounicode_mapping_inside_cmap_comment_is_ignored(self):
        cmap = b"% 1 beginbfchar\n<01> <0050>\nendbfchar\n"
        self.assertEqual({}, probe_libreoffice_export.parse_tounicode_bfchar_map(cmap))

    def test_tounicode_mapping_after_cmap_envelope_is_ignored(self):
        text = "PaperBatch U0 export probe"
        source_bytes = bytes(range(1, len(text) + 1))
        cmap_entries = b"\n".join(
            f"<{source:02X}> <{ord(character):04X}>".encode("ascii")
            for source, character in zip(source_bytes, text)
        )
        cmap = (
            b"begincmap\nendcmap\n"
            + str(len(text)).encode("ascii")
            + b" beginbfchar\n"
            + cmap_entries
            + b"\nendbfchar"
        )
        content = b"BT /F1 11 Tf <" + source_bytes.hex().encode("ascii") + b"> Tj ET\n"
        pdf, _reference = pdf_with_tounicode_cmap(cmap, [content])
        expected_content: list[bool] = []

        self.assertTrue(fixture_classic_xref_is_valid(
            pdf, expected_text=text, expected_content_observation=expected_content))
        self.assertEqual([False], expected_content)

    def test_truncated_cmap_cannot_hide_a_second_envelope(self):
        cmap = (
            b"begincmap\n1 beginbfchar\n<01> <0050>\nendbfchar\nendcmap\n"
            b"begincmap endcmap"
        )
        with mock.patch.object(probe_libreoffice_export, "MAX_PDF_PARSE_TOKENS", 7):
            self.assertEqual({}, probe_libreoffice_export.parse_tounicode_bfchar_map(cmap))

    def test_shared_page_content_stream_is_decoded_once_across_pages(self):
        content = b"BT /F1 11 Tf (different content) Tj ET\n"
        pdf = minimal_pdf(page_count=6, page_content=content, shared_page_stream=True)
        decode = probe_libreoffice_export.decode_pdf_stream
        decode_calls = 0
        read_payload = probe_libreoffice_export.read_indirect_stream_payload
        payload_reads = 0

        def count_decodes(dictionary, payload):
            nonlocal decode_calls
            if payload == content:
                decode_calls += 1
            return decode(dictionary, payload)

        def count_payload_reads(data, entries, reference, xref_offset):
            nonlocal payload_reads
            payload = read_payload(data, entries, reference, xref_offset)
            if payload == content:
                payload_reads += 1
            return payload

        expected_content: list[bool] = []
        with (
            mock.patch.object(probe_libreoffice_export, "decode_pdf_stream", side_effect=count_decodes),
            mock.patch.object(probe_libreoffice_export, "read_indirect_stream_payload", side_effect=count_payload_reads),
        ):
            self.assertTrue(fixture_classic_xref_is_valid(
                pdf,
                expected_text="PaperBatch U0 export probe",
                expected_content_observation=expected_content,
            ))

        self.assertEqual([False], expected_content)
        self.assertEqual(1, decode_calls)
        self.assertEqual(1, payload_reads)

    def test_document_content_decode_budget_accumulates_unique_page_streams(self):
        content = b"q Q\n"
        pdf = minimal_pdf(page_count=3, page_contents=[content, content, content])
        decode = probe_libreoffice_export.decode_pdf_stream
        decode_calls = 0

        def count_decodes(dictionary, payload):
            nonlocal decode_calls
            if payload == content:
                decode_calls += 1
            return decode(dictionary, payload)

        expected_content: list[bool] = []
        with (
            mock.patch.object(probe_libreoffice_export, "MAX_PDF_TOTAL_CONTENT_BYTES", 8, create=True),
            mock.patch.object(probe_libreoffice_export, "decode_pdf_stream", side_effect=count_decodes),
        ):
            self.assertTrue(fixture_classic_xref_is_valid(
                pdf,
                expected_text="PaperBatch U0 export probe",
                expected_content_observation=expected_content,
            ))

        self.assertEqual([False], expected_content)
        self.assertEqual(2, decode_calls)

    def test_unencoded_symbolic_type1_fonts_cannot_verify_ascii_fixture_text(self):
        page_content = b"BT /F1 11 Tf (PaperBatch U0 export probe) Tj ET\n"
        for base_font in (
            b"Symbol",
            b"ZapfDingbats",
            b"ABCDEF+Symbol",
            b"ABCDEF+ZapfDingbats",
            b"Sym#62ol",
            b"Zapf#44ingbats",
        ):
            with self.subTest(base_font=base_font):
                pdf = pdf_with_type1_font(base_font, page_content)
                expected_content: list[bool] = []

                self.assertTrue(fixture_classic_xref_is_valid(
                    pdf,
                    expected_text="PaperBatch U0 export probe",
                    expected_content_observation=expected_content,
                ))
                self.assertEqual([False], expected_content)

    def test_unencoded_custom_type1_font_cannot_verify_ascii_fixture_text(self):
        page_content = b"BT /F1 11 Tf (PaperBatch U0 export probe) Tj ET\n"
        pdf = pdf_with_type1_font(b"CustomBuiltInEncoding", page_content)
        expected_content: list[bool] = []

        self.assertTrue(fixture_classic_xref_is_valid(
            pdf,
            expected_text="PaperBatch U0 export probe",
            expected_content_observation=expected_content,
        ))
        self.assertEqual([False], expected_content)

    def test_tounicode_mapping_inside_literal_string_is_ignored(self):
        cmap = b"(1 beginbfchar <01> <0050> endbfchar) pop\n"
        self.assertEqual({}, probe_libreoffice_export.parse_tounicode_bfchar_map(cmap))

    def test_percent_inside_literal_string_does_not_hide_real_mapping(self):
        cmap = b"begincmap (% not a comment) pop 1 beginbfchar <01> <0050> endbfchar endcmap\n"
        self.assertEqual({b"\x01": "P"}, probe_libreoffice_export.parse_tounicode_bfchar_map(cmap))

    def test_malformed_cmap_delimiter_stops_mapping_parse(self):
        cmap = b"] 1 beginbfchar <01> <0050> endbfchar\n"
        self.assertEqual({}, probe_libreoffice_export.parse_tounicode_bfchar_map(cmap))

    @unittest.skipUnless(sys.platform.startswith("linux"), "Linux descriptor launch regression")
    def test_engine_source_mutation_after_pin_does_not_change_launched_image(self):
        with tempfile.TemporaryDirectory(prefix="paperbatch-engine-mutation-test-") as directory:
            executable = Path(directory) / "soffice.bin"
            executable.write_text("#!/bin/sh\nprintf 'original-engine\\n'\n", encoding="utf-8")
            executable.chmod(0o700)
            expected_sha256 = hashlib.sha256(executable.read_bytes()).hexdigest()
            original_run = probe_libreoffice_export.subprocess.run

            def mutate_source_then_launch(*args, **kwargs):
                executable.write_text("#!/bin/sh\nprintf 'modified-engine\\n'\n", encoding="utf-8")
                executable.chmod(0o700)
                return original_run(*args, **kwargs)

            with mock.patch.object(
                probe_libreoffice_export.subprocess,
                "run",
                side_effect=mutate_source_then_launch,
            ):
                result = probe_libreoffice_export.invoke(
                    executable,
                    ["--version"],
                    Path(directory) / "profile",
                    5,
                    expected_engine_sha256=expected_sha256,
                )

        self.assertEqual(0, result["exitCode"])
        self.assertEqual("original-engine", result["stdout"])

    @unittest.skipUnless(sys.platform.startswith("linux"), "Linux opened-descriptor runtime-path regression")
    def test_engine_path_redirection_to_symlink_blocks_before_launch(self):
        with tempfile.TemporaryDirectory(prefix="paperbatch-engine-runtime-redirection-test-") as directory:
            root = Path(directory)
            first_directory = root / "first-install" / "program"
            second_directory = root / "second-install" / "program"
            first_directory.mkdir(parents=True)
            second_directory.mkdir(parents=True)
            selected_engine = first_directory / "soffice.bin"
            second_engine = second_directory / "soffice.bin"
            selected_engine.write_bytes(b"pinned engine image")
            second_engine.write_bytes(selected_engine.read_bytes())
            first_fundamental = first_directory / "fundamentalrc"
            second_fundamental = second_directory / "fundamentalrc"
            first_fundamental.write_text("first runtime", encoding="utf-8")
            second_fundamental.write_text("second runtime", encoding="utf-8")
            expected_sha256 = hashlib.sha256(selected_engine.read_bytes()).hexdigest()
            original_hash = probe_libreoffice_export.sha256_stream
            original_run = probe_libreoffice_export.subprocess.run
            redirected = False
            child_calls = []

            def redirect_after_hash(stream):
                nonlocal redirected
                digest = original_hash(stream)
                if not redirected:
                    moved_engine = first_directory / "soffice.original"
                    selected_engine.rename(moved_engine)
                    selected_engine.symlink_to(second_engine)
                    redirected = True
                return digest

            def fake_subprocess_run(command, **kwargs):
                if "cwd" not in kwargs:
                    return original_run(command, **kwargs)
                runtime_directory = Path(kwargs["cwd"]).resolve()
                library_directory = Path(kwargs["env"]["LD_LIBRARY_PATH"]).resolve()
                bootstrap_path = Path(kwargs["env"]["URE_BOOTSTRAP"].removeprefix("file://")).resolve()
                child_calls.append((command, kwargs, runtime_directory, library_directory, bootstrap_path))
                if "--convert-to" in command:
                    output_directory = Path(command[command.index("--outdir") + 1])
                    (output_directory / "input.pdf").write_bytes(minimal_pdf())
                return probe_libreoffice_export.subprocess.CompletedProcess(
                    args=command,
                    returncode=0,
                    stdout="LibreOfficeDev 26.8.0.0.alpha0",
                    stderr="",
                )

            with (
                mock.patch.object(probe_libreoffice_export, "sha256_stream", side_effect=redirect_after_hash),
                mock.patch.object(probe_libreoffice_export.subprocess, "run", side_effect=fake_subprocess_run),
            ):
                report, exit_code = probe_libreoffice_export.run_probe(
                    None,
                    timeout_seconds=5,
                    engine_binary=str(selected_engine),
                    expected_engine_sha256=expected_sha256,
                )

        self.assertEqual(1, exit_code)
        self.assertEqual("blocked", report["libreOffice"]["status"])
        self.assertEqual("blocked", report["export"]["status"])
        self.assertIn("symbolic link", report["libreOffice"]["limitation"].lower())
        self.assertEqual([], child_calls)

    @unittest.skipUnless(sys.platform.startswith("linux"), "Linux runtime-directory descriptor regression")
    def test_runtime_directory_replacement_does_not_change_launch_inputs(self):
        with tempfile.TemporaryDirectory(prefix="paperbatch-runtime-directory-race-test-") as directory:
            root = Path(directory)
            program_directory = root / "program"
            moved_directory = root / "program-original"
            program_directory.mkdir()
            executable = program_directory / "soffice.bin"
            executable.write_text(
                "#!/bin/sh\nread runtime < \"$LD_LIBRARY_PATH/fundamentalrc\"\nprintf 'runtime=%s\\n' \"$runtime\"\n",
                encoding="utf-8",
            )
            executable.chmod(0o700)
            (program_directory / "fundamentalrc").write_text("original runtime", encoding="utf-8")
            expected_sha256 = hashlib.sha256(executable.read_bytes()).hexdigest()
            original_run = probe_libreoffice_export.subprocess.run
            replaced = False

            def replace_runtime_directory_then_launch(command, **kwargs):
                nonlocal replaced
                if not replaced:
                    program_directory.rename(moved_directory)
                    program_directory.mkdir()
                    (program_directory / "fundamentalrc").write_text("replacement runtime", encoding="utf-8")
                    replaced = True
                return original_run(command, **kwargs)

            with mock.patch.object(
                probe_libreoffice_export.subprocess,
                "run",
                side_effect=replace_runtime_directory_then_launch,
            ):
                result = probe_libreoffice_export.invoke(
                    executable,
                    ["--version"],
                    root / "profile",
                    timeout_seconds=5,
                    expected_engine_sha256=expected_sha256,
                )

        self.assertEqual(0, result["exitCode"])
        self.assertIn("runtime=original runtime", result["stdout"])

    @unittest.skipUnless(sys.platform.startswith("linux"), "Linux runtime-component mutation regression")
    def test_runtime_component_mutation_blocks_version_and_export_evidence(self):
        with tempfile.TemporaryDirectory(prefix="paperbatch-runtime-component-mutation-test-") as directory:
            root = Path(directory)
            program_directory = root / "program"
            program_directory.mkdir()
            executable = program_directory / "soffice.bin"
            executable.write_text(
                "#!/bin/sh\nprintf 'changed runtime\\n' > \"$LD_LIBRARY_PATH/fundamentalrc\"\nprintf 'LibreOfficeDev 26.8.0.0.alpha0\\n'\n",
                encoding="utf-8",
            )
            executable.chmod(0o700)
            (program_directory / "fundamentalrc").write_text("original runtime", encoding="utf-8")
            expected_sha256 = hashlib.sha256(executable.read_bytes()).hexdigest()

            report, exit_code = probe_libreoffice_export.run_probe(
                None,
                timeout_seconds=5,
                engine_binary=str(executable),
                expected_engine_sha256=expected_sha256,
            )

        self.assertEqual(1, exit_code)
        self.assertEqual("blocked", report["libreOffice"]["status"])
        self.assertFalse(report["libreOffice"]["versionCommand"]["identityVerified"])
        self.assertIn("runtime", report["libreOffice"]["versionCommand"]["identityFailure"].lower())
        self.assertIn("runtime", report["libreOffice"]["limitation"].lower())
        self.assertEqual("blocked", report["export"]["status"])

    @unittest.skipUnless(sys.platform.startswith("linux"), "Linux runtime-component prelaunch regression")
    def test_runtime_component_change_before_launch_blocks_without_spawning(self):
        with tempfile.TemporaryDirectory(prefix="paperbatch-runtime-component-prelaunch-test-") as directory:
            root = Path(directory)
            program_directory = root / "program"
            program_directory.mkdir()
            executable = program_directory / "soffice.bin"
            executable.write_text("#!/bin/sh\nprintf 'LibreOfficeDev 26.8.0.0.alpha0\\n'\n", encoding="utf-8")
            executable.chmod(0o700)
            fundamental = program_directory / "fundamentalrc"
            fundamental.write_text("original runtime", encoding="utf-8")
            expected_sha256 = hashlib.sha256(executable.read_bytes()).hexdigest()
            pinned = probe_libreoffice_export.open_pinned_engine(executable, expected_sha256)
            try:
                fundamental.write_text("changed runtime", encoding="utf-8")
                with mock.patch.object(probe_libreoffice_export.subprocess, "run") as run_mock:
                    result = probe_libreoffice_export.invoke(
                        executable,
                        ["--version"],
                        root / "profile",
                        timeout_seconds=5,
                        pinned_engine=pinned,
                    )
            finally:
                pinned.close()

        self.assertFalse(result["identityVerified"])
        self.assertIn("runtime components changed", result["identityFailure"].lower())
        run_mock.assert_not_called()

    @unittest.skipUnless(sys.platform.startswith("linux"), "Linux runtime-component timeout regression")
    def test_timeout_reports_runtime_mutation_as_unverified(self):
        with tempfile.TemporaryDirectory(prefix="paperbatch-runtime-component-timeout-test-") as directory:
            root = Path(directory)
            program_directory = root / "program"
            program_directory.mkdir()
            executable = program_directory / "soffice.bin"
            executable.write_text("#!/bin/sh\nsleep 30\n", encoding="utf-8")
            executable.chmod(0o700)
            fundamental = program_directory / "fundamentalrc"
            fundamental.write_text("original runtime", encoding="utf-8")
            pinned = probe_libreoffice_export.open_pinned_engine(
                executable,
                hashlib.sha256(executable.read_bytes()).hexdigest(),
            )
            try:
                def timeout_after_runtime_change(command, **_kwargs):
                    fundamental.write_text("changed runtime", encoding="utf-8")
                    raise probe_libreoffice_export.subprocess.TimeoutExpired(command, 1)

                with mock.patch.object(
                    probe_libreoffice_export.subprocess,
                    "run",
                    side_effect=timeout_after_runtime_change,
                ):
                    result = probe_libreoffice_export.invoke(
                        executable,
                        ["--version"],
                        root / "profile",
                        timeout_seconds=1,
                        pinned_engine=pinned,
                    )
            finally:
                pinned.close()

        self.assertTrue(result["timedOut"])
        self.assertFalse(result["identityVerified"])
        self.assertIn("runtime components changed", result["identityFailure"].lower())

    @unittest.skipUnless(sys.platform.startswith("linux"), "Linux opened-directory race regression")
    def test_runtime_directory_resolution_rejects_symlink_to_opened_engine(self):
        with tempfile.TemporaryDirectory(prefix="paperbatch-runtime-symlink-race-test-") as directory:
            root = Path(directory)
            program_directory = root / "program"
            moved_directory = root / "program-original"
            program_directory.mkdir()
            executable = program_directory / "soffice.bin"
            executable.write_text("#!/bin/sh\nprintf 'LibreOfficeDev 26.8.0.0.alpha0\\n'\n", encoding="utf-8")
            executable.chmod(0o700)
            expected_sha256 = hashlib.sha256(executable.read_bytes()).hexdigest()
            real_open = os.open
            replaced = False

            def replace_parent_before_open(path, flags, *args, **kwargs):
                nonlocal replaced
                if not replaced and Path(path) == program_directory:
                    program_directory.rename(moved_directory)
                    program_directory.mkdir()
                    (program_directory / executable.name).symlink_to(moved_directory / executable.name)
                    replaced = True
                return real_open(path, flags, *args, **kwargs)

            with mock.patch.object(probe_libreoffice_export.os, "open", side_effect=replace_parent_before_open):
                try:
                    pinned = probe_libreoffice_export.open_pinned_engine(executable, expected_sha256)
                except probe_libreoffice_export.PinnedEngineError:
                    pinned = None
                if pinned is not None:
                    pinned.close()
                    self.fail("runtime-directory replacement passed through a symlink to the opened engine")

            self.assertTrue(replaced)

    @unittest.skipUnless(sys.platform.startswith("linux"), "Linux opened-directory hardlink regression")
    def test_runtime_directory_resolution_rejects_hardlink_to_opened_engine(self):
        with tempfile.TemporaryDirectory(prefix="paperbatch-runtime-hardlink-race-test-") as directory:
            root = Path(directory)
            program_directory = root / "program"
            moved_directory = root / "program-original"
            program_directory.mkdir()
            executable = program_directory / "soffice.bin"
            executable.write_text("#!/bin/sh\nprintf 'LibreOfficeDev 26.8.0.0.alpha0\\n'\n", encoding="utf-8")
            executable.chmod(0o700)
            expected_sha256 = hashlib.sha256(executable.read_bytes()).hexdigest()
            real_open = os.open
            replaced = False

            def replace_parent_with_hardlink_before_open(path, flags, *args, **kwargs):
                nonlocal replaced
                if not replaced and Path(path) == program_directory:
                    program_directory.rename(moved_directory)
                    program_directory.mkdir()
                    os.link(moved_directory / executable.name, program_directory / executable.name)
                    replaced = True
                return real_open(path, flags, *args, **kwargs)

            with mock.patch.object(probe_libreoffice_export.os, "open", side_effect=replace_parent_with_hardlink_before_open):
                try:
                    pinned = probe_libreoffice_export.open_pinned_engine(executable, expected_sha256)
                except probe_libreoffice_export.PinnedEngineError:
                    pinned = None
                if pinned is not None:
                    pinned.close()
                    self.fail("runtime-directory replacement passed through a hard link to the opened engine")

            self.assertTrue(replaced)

    def test_non_linux_unix_file_inspection_rejects_before_open(self):
        with tempfile.TemporaryDirectory(prefix="paperbatch-unix-file-inspection-test-") as directory:
            candidate = Path(directory) / "candidate"
            candidate.write_bytes(b"candidate")
            with (
                mock.patch.object(probe_libreoffice_export.sys, "platform", "darwin"),
                mock.patch.object(probe_libreoffice_export.os, "name", "posix"),
                mock.patch.object(Path, "open", side_effect=AssertionError("unsupported Unix paths must not be opened")),
            ):
                with self.assertRaises(probe_libreoffice_export.PinnedEngineError):
                    probe_libreoffice_export.open_readonly_regular_file(candidate)

    @unittest.skipUnless(sys.platform.startswith("linux"), "Linux O_PATH race regression")
    def test_readonly_file_open_pins_regular_file_before_content_open(self):
        with tempfile.TemporaryDirectory(prefix="paperbatch-readonly-file-race-test-") as directory:
            candidate = Path(directory) / "selected-file"
            original = b"read the regular file that was selected"
            candidate.write_bytes(original)
            real_open = os.open
            swapped = False

            def replace_with_device_after_path_pin(path, flags, *args, **kwargs):
                nonlocal swapped
                if Path(path) == candidate and not swapped:
                    if flags & getattr(os, "O_PATH", 0x200000):
                        descriptor = real_open(path, flags, *args, **kwargs)
                        candidate.unlink()
                        candidate.symlink_to("/dev/null")
                        swapped = True
                        return descriptor
                    candidate.unlink()
                    candidate.symlink_to("/dev/null")
                    swapped = True
                return real_open(path, flags, *args, **kwargs)

            with mock.patch.object(
                probe_libreoffice_export.os,
                "open",
                side_effect=replace_with_device_after_path_pin,
            ):
                with probe_libreoffice_export.open_readonly_regular_file(candidate) as stream:
                    observed = stream.read()

        self.assertTrue(swapped)
        self.assertEqual(original, observed)

    @unittest.skipUnless(sys.platform.startswith("linux"), "Linux engine snapshot size bound regression")
    def test_oversized_engine_snapshot_is_rejected_before_memfd_creation(self):
        with tempfile.TemporaryDirectory(prefix="paperbatch-engine-size-limit-test-") as directory:
            executable = Path(directory) / "soffice.bin"
            executable.write_bytes(b"engine is too large")
            expected_sha256 = hashlib.sha256(executable.read_bytes()).hexdigest()

            with (
                mock.patch.object(probe_libreoffice_export, "MAX_ENGINE_SNAPSHOT_BYTES", 8, create=True),
                mock.patch.object(
                    probe_libreoffice_export.os,
                    "memfd_create",
                    side_effect=AssertionError("oversized engine must be rejected before memfd creation"),
                ),
            ):
                with self.assertRaises(probe_libreoffice_export.PinnedEngineError):
                    probe_libreoffice_export.open_pinned_engine(executable, expected_sha256)

    @unittest.skipUnless(sys.platform.startswith("linux"), "Linux selected-engine size-before-hash regression")
    def test_run_probe_blocks_oversized_engine_before_hashing_even_with_mismatched_pin(self):
        with tempfile.TemporaryDirectory(prefix="paperbatch-engine-size-before-hash-test-") as directory:
            executable = Path(directory) / "soffice.bin"
            executable.write_bytes(b"engine exceeds the test limit")

            with (
                mock.patch.object(probe_libreoffice_export, "MAX_ENGINE_SNAPSHOT_BYTES", 8),
                mock.patch.object(
                    probe_libreoffice_export,
                    "sha256_stream",
                    side_effect=AssertionError("oversized engine must be rejected before hashing"),
                ),
            ):
                report, exit_code = probe_libreoffice_export.run_probe(
                    None,
                    timeout_seconds=5,
                    engine_binary=str(executable),
                    expected_engine_sha256="0" * 64,
                )

        self.assertEqual(1, exit_code)
        self.assertEqual("blocked", report["libreOffice"]["status"])
        self.assertIn("snapshot limit", report["libreOffice"]["limitation"])

    @unittest.skipUnless(sys.platform.startswith("linux"), "Linux engine-path replacement regression")
    def test_engine_path_replacement_with_fifo_does_not_block_hashing(self):
        with tempfile.TemporaryDirectory(prefix="paperbatch-engine-fifo-race-test-") as directory:
            executable = Path(directory) / "soffice.bin"
            executable.write_text(
                "#!/bin/sh\n"
                "for argument in \"$@\"; do\n"
                "    if [ \"$argument\" = \"--version\" ]; then\n"
                "        printf 'LibreOfficeDev 26.8.0.0.alpha0\\n'\n"
                "    fi\n"
                "done\n"
                "exit 0\n",
                encoding="utf-8",
            )
            executable.chmod(0o700)
            expected_sha256 = hashlib.sha256(executable.read_bytes()).hexdigest()
            original_hash = probe_libreoffice_export.sha256_stream
            swapped = False
            outcomes = []

            def replace_with_fifo_then_hash(stream):
                nonlocal swapped
                if not swapped:
                    executable.unlink()
                    os.mkfifo(executable)
                    swapped = True
                return original_hash(stream)

            def run_probe():
                try:
                    outcomes.append(probe_libreoffice_export.run_probe(
                        None,
                        timeout_seconds=5,
                        engine_binary=str(executable),
                        expected_engine_sha256=expected_sha256,
                    ))
                except Exception as error:
                    outcomes.append(error)

            with mock.patch.object(probe_libreoffice_export, "sha256_stream", side_effect=replace_with_fifo_then_hash):
                worker = threading.Thread(target=run_probe, daemon=True)
                worker.start()
                worker.join(timeout=3)
                completed_without_fifo_writer = not worker.is_alive()
                if not completed_without_fifo_writer:
                    with executable.open("wb"):
                        pass
                    worker.join(timeout=3)
                fifo_path_persisted = executable.is_fifo()

        self.assertTrue(completed_without_fifo_writer, "engine hashing blocked after the selected path became a FIFO")
        self.assertFalse(worker.is_alive(), "the bounded FIFO regression worker did not finish")
        self.assertTrue(swapped, "the selected path was not replaced with a FIFO during the pinned-image hash")
        self.assertTrue(fifo_path_persisted, "the selected path is not the expected FIFO after the hash race")
        self.assertEqual(1, len(outcomes))
        self.assertIsInstance(outcomes[0], tuple)
        report, exit_code = outcomes[0]
        self.assertEqual(1, exit_code)
        self.assertEqual("blocked", report["libreOffice"]["status"])
        self.assertEqual("blocked", report["export"]["status"])
        self.assertIn("non-regular", report["libreOffice"]["limitation"].lower())

    @unittest.skipUnless(sys.platform.startswith("linux"), "Linux pinned-runtime reuse regression")
    def test_run_probe_reuses_the_opened_runtime_directory_for_version_and_export(self):
        with tempfile.TemporaryDirectory(prefix="paperbatch-runtime-directory-reuse-test-") as directory:
            root = Path(directory)
            program_directory = root / "program"
            moved_directory = root / "program-original"
            program_directory.mkdir()
            executable = program_directory / "soffice.bin"
            executable.write_text("pinned engine image", encoding="utf-8")
            (program_directory / "fundamentalrc").write_text("original runtime", encoding="utf-8")
            expected_sha256 = hashlib.sha256(executable.read_bytes()).hexdigest()
            observed = []

            def fake_invoke(_executable, args, _profile, _timeout_seconds, **kwargs):
                pinned_engine = kwargs.get("pinned_engine")
                self.assertIsNotNone(pinned_engine)
                runtime_path = Path(pinned_engine.launch_kwargs["env"]["LD_LIBRARY_PATH"])
                runtime_value = (runtime_path / "fundamentalrc").read_text(encoding="utf-8")
                observed.append((pinned_engine, runtime_value))
                if "--version" in args:
                    program_directory.rename(moved_directory)
                    program_directory.mkdir()
                    (program_directory / "fundamentalrc").write_text("replacement runtime", encoding="utf-8")
                    return {"exitCode": 0, "stdout": "LibreOfficeDev 26.8.0.0.alpha0", "stderr": "", "timedOut": False, "identityVerified": True}
                output_directory = Path(args[args.index("--outdir") + 1])
                (output_directory / "input.pdf").write_bytes(minimal_pdf())
                return {"exitCode": 0, "stdout": "", "stderr": "", "timedOut": False, "identityVerified": True}

            with mock.patch.object(probe_libreoffice_export, "invoke", side_effect=fake_invoke):
                report, exit_code = probe_libreoffice_export.run_probe(
                    None,
                    timeout_seconds=5,
                    engine_binary=str(executable),
                    expected_engine_sha256=expected_sha256,
                )

        self.assertEqual(0, exit_code)
        self.assertEqual("verified", report["export"]["status"])
        self.assertEqual(2, len(observed))
        self.assertIsNotNone(observed[0][0])
        self.assertIs(observed[0][0], observed[1][0])
        self.assertEqual(["original runtime", "original runtime"], [item[1] for item in observed])
        self.assertRegex(report["libreOffice"]["runtimeDirectorySha256"], r"^[0-9a-f]{64}$")

    @unittest.skipUnless(sys.platform.startswith("linux"), "Linux runtime-component symlink regression")
    def test_runtime_directory_symlinks_are_blocked_before_launch(self):
        with tempfile.TemporaryDirectory(prefix="paperbatch-runtime-symlink-component-test-") as directory:
            root = Path(directory)
            program_directory = root / "program"
            program_directory.mkdir()
            executable = program_directory / "soffice.bin"
            executable.write_text("#!/bin/sh\nprintf 'LibreOfficeDev 26.8.0.0.alpha0\\n'\n", encoding="utf-8")
            executable.chmod(0o700)
            external_library = root / "external.so"
            external_library.write_bytes(b"runtime dependency")
            (program_directory / "libexternal.so").symlink_to(external_library)
            expected_sha256 = hashlib.sha256(executable.read_bytes()).hexdigest()

            with self.assertRaises(probe_libreoffice_export.PinnedEngineError):
                probe_libreoffice_export.open_pinned_engine(executable, expected_sha256)

    @unittest.skipUnless(sys.platform.startswith("linux"), "Linux pinned-engine directory-entry replacement regression")
    def test_runtime_manifest_rejects_replaced_engine_entry_types(self):
        for replacement in ("symlink", "fifo", "regular"):
            with self.subTest(replacement=replacement), tempfile.TemporaryDirectory(
                prefix="paperbatch-runtime-engine-entry-test-"
            ) as directory:
                program_directory = Path(directory) / "program"
                program_directory.mkdir()
                executable = program_directory / "soffice.bin"
                executable.write_text("pinned engine image", encoding="utf-8")
                expected_sha256 = hashlib.sha256(executable.read_bytes()).hexdigest()
                pinned = probe_libreoffice_export.open_pinned_engine(executable, expected_sha256)
                try:
                    executable.unlink()
                    if replacement == "symlink":
                        target = Path(directory) / "external-engine"
                        target.write_text("replacement", encoding="utf-8")
                        executable.symlink_to(target)
                    elif replacement == "fifo":
                        os.mkfifo(executable)
                    else:
                        executable.write_text("replacement engine image", encoding="utf-8")

                    with self.assertRaises(probe_libreoffice_export.PinnedEngineError):
                        pinned.verify_runtime_directory()
                finally:
                    pinned.close()

    def test_runtime_entry_limit_stops_scanning_before_materializing_the_directory(self):
        with tempfile.TemporaryDirectory(prefix="paperbatch-runtime-entry-limit-test-") as directory:
            program_directory = Path(directory) / "program"
            program_directory.mkdir()
            for index in range(12):
                (program_directory / f"component-{index:02d}.so").write_bytes(b"x")

            original_scandir = probe_libreoffice_export.os.scandir
            entries_observed = 0

            class CountingScandir:
                def __init__(self, path):
                    self._iterator = original_scandir(path)

                def __enter__(self):
                    self._iterator.__enter__()
                    return self

                def __exit__(self, exc_type, exc_value, traceback):
                    return self._iterator.__exit__(exc_type, exc_value, traceback)

                def __iter__(self):
                    return self

                def __next__(self):
                    nonlocal entries_observed
                    entry = next(self._iterator)
                    entries_observed += 1
                    return entry

            with (
                mock.patch.object(probe_libreoffice_export, "MAX_RUNTIME_DIRECTORY_ENTRIES", 2),
                mock.patch.object(
                    probe_libreoffice_export.os,
                    "scandir",
                    side_effect=lambda path: CountingScandir(path),
                ),
                self.assertRaises(probe_libreoffice_export.PinnedEngineError),
            ):
                probe_libreoffice_export.runtime_directory_sha256(program_directory, "soffice.bin")

        self.assertEqual(3, entries_observed)

    @unittest.skipUnless(sys.platform.startswith("linux"), "Linux child environment regression")
    def test_linux_launch_drops_inherited_loader_overrides(self):
        with tempfile.TemporaryDirectory(prefix="paperbatch-loader-env-test-") as directory:
            executable = Path(directory) / "soffice.bin"
            executable.write_text("pinned engine image", encoding="utf-8")
            expected_sha256 = hashlib.sha256(executable.read_bytes()).hexdigest()
            child_result = probe_libreoffice_export.subprocess.CompletedProcess(
                args=[str(executable)],
                returncode=0,
                stdout="LibreOfficeDev 26.8.0.0.alpha0",
                stderr="",
            )
            observed_library_paths = []

            def capture_child_environment(*args, **kwargs):
                observed_library_paths.append(Path(kwargs["env"]["LD_LIBRARY_PATH"]).resolve())
                return child_result

            with (
                mock.patch.dict(
                    probe_libreoffice_export.os.environ,
                    {
                        "LD_PRELOAD": "/tmp/untrusted-preload.so",
                        "LD_AUDIT": "/tmp/untrusted-audit.so",
                        "LD_DEBUG": "all",
                        "LD_LIBRARY_PATH": "/tmp/untrusted-libraries",
                        "URE_BOOTSTRAP": "file:///tmp/untrusted-fundamentalrc",
                    },
                ),
                mock.patch.object(
                    probe_libreoffice_export.subprocess,
                    "run",
                    side_effect=capture_child_environment,
                ) as run_process,
            ):
                result = probe_libreoffice_export.invoke(
                    executable,
                    ["--version"],
                    Path(directory) / "profile",
                    5,
                    expected_engine_sha256=expected_sha256,
                    temporary_directory=Path(directory).parent / (Path(directory).name + "-isolated-temp"),
                )

        child_environment = run_process.call_args.kwargs["env"]
        for variable in ("TMPDIR", "TMP", "TEMP"):
            self.assertEqual(str((Path(directory).parent / (Path(directory).name + "-isolated-temp")).resolve()), child_environment[variable])
        self.assertEqual(0, result["exitCode"], result)
        self.assertEqual([executable.parent.resolve()], observed_library_paths)
        self.assertFalse("LD_PRELOAD" in child_environment, "LD_PRELOAD must be removed from the child environment")
        self.assertFalse("LD_AUDIT" in child_environment, "LD_AUDIT must be removed from the child environment")
        self.assertFalse("LD_DEBUG" in child_environment, "LD_DEBUG must be removed from the child environment")
        self.assertFalse("URE_BOOTSTRAP" in child_environment, "an inherited URE_BOOTSTRAP must be removed")

    @unittest.skipUnless(os.name == "nt", "Windows handle-path regression")
    def test_windows_launch_uses_path_resolved_from_locked_handle(self):
        with tempfile.TemporaryDirectory(prefix="paperbatch-windows-engine-path-test-") as directory:
            executable = Path(directory) / "soffice.bin"
            executable.write_text("pinned engine image", encoding="utf-8")
            expected_sha256 = hashlib.sha256(executable.read_bytes()).hexdigest()
            child_result = probe_libreoffice_export.subprocess.CompletedProcess(
                args=[str(executable)],
                returncode=0,
                stdout="LibreOfficeDev 26.8.0.0.alpha0",
                stderr="",
            )
            with mock.patch.object(
                probe_libreoffice_export.subprocess,
                "run",
                return_value=child_result,
            ) as run_process:
                result = probe_libreoffice_export.invoke(
                    executable,
                    ["--version"],
                    Path(directory) / "profile",
                    5,
                    expected_engine_sha256=expected_sha256,
                )

        self.assertEqual(0, result["exitCode"], result)
        command = run_process.call_args.args[0]
        self.assertTrue(command[0].startswith("\\\\?\\"))
        self.assertEqual(command[0], result["executionTarget"])

    def test_fixture_text_in_pdf_comment_cannot_verify_export(self):
        content = b"BT /F1 11 Tf % (PaperBatch U0 export probe) Tj\n () Tj ET\n"
        self.assertFalse(probe_libreoffice_export.pdf_streams_contain_expected_text(
            [content], "PaperBatch U0 export probe"))

    def test_text_show_state_is_sampled_at_each_operator(self):
        zero_size_at_show = b"BT /F1 0 Tf (PaperBatch U0 export probe) Tj /F1 11 Tf ET"
        positive_size_at_show = b"BT /F1 11 Tf (PaperBatch U0 export probe) Tj /F1 0 Tf ET"
        mode_three_at_show = b"BT /F1 11 Tf 3 Tr (PaperBatch U0 export probe) Tj 0 Tr ET"
        mode_zero_at_show = b"BT /F1 11 Tf (PaperBatch U0 export probe) Tj 3 Tr ET"

        self.assertFalse(probe_libreoffice_export.pdf_streams_contain_expected_text(
            [zero_size_at_show], "PaperBatch U0 export probe"))
        self.assertTrue(probe_libreoffice_export.pdf_streams_contain_expected_text(
            [positive_size_at_show], "PaperBatch U0 export probe", font_names={b"F1"}, ascii_font_names={b"F1"}))
        self.assertFalse(probe_libreoffice_export.pdf_streams_contain_expected_text(
            [mode_three_at_show], "PaperBatch U0 export probe"))
        self.assertTrue(probe_libreoffice_export.pdf_streams_contain_expected_text(
            [mode_zero_at_show], "PaperBatch U0 export probe", font_names={b"F1"}, ascii_font_names={b"F1"}))

    def test_text_rendering_mode_restores_across_graphics_state(self):
        invisible_after_restore = (
            b"BT /F1 11 Tf 3 Tr q 0 Tr Q (PaperBatch U0 export probe) Tj ET"
        )
        self.assertFalse(probe_libreoffice_export.pdf_streams_contain_expected_text(
            [invisible_after_restore],
            "PaperBatch U0 export probe",
            font_names={b"F1"},
            ascii_font_names={b"F1"},
        ))

    def test_text_show_evidence_does_not_check_rendered_visibility(self):
        white_text = b"BT 1 1 1 rg /F1 11 Tf (PaperBatch U0 export probe) Tj ET"
        self.assertTrue(probe_libreoffice_export.pdf_streams_contain_expected_text(
            [white_text], "PaperBatch U0 export probe", font_names={b"F1"}, ascii_font_names={b"F1"}))

    def test_nonempty_graphics_commands_without_fixture_content_cannot_verify_export(self):
        with tempfile.TemporaryDirectory(prefix="paperbatch-dropped-content-test-") as directory:
            executable = Path(directory) / "soffice"
            executable.write_text("mock executable", encoding="utf-8")

            def fake_invoke(_executable, args, _profile, _timeout_seconds, **_kwargs):
                if "--convert-to" in args:
                    output_dir = Path(args[args.index("--outdir") + 1])
                    (output_dir / "input.pdf").write_bytes(minimal_pdf(page_content=b"q Q\n"))
                    return {"exitCode": 0, "stdout": "", "stderr": "", "timedOut": False}
                return {"exitCode": 0, "stdout": "LibreOffice 1.2.3", "stderr": "", "timedOut": False}

            with mock.patch.object(probe_libreoffice_export, "invoke", side_effect=fake_invoke):
                report, exit_code = run_pinned_probe(executable)

        self.assertEqual(exit_code, 1)
        self.assertEqual(report["export"]["status"], "blocked")
        self.assertTrue(report["export"]["outputStructureValid"])
        self.assertFalse(report["export"]["fixtureTextShowOperatorPresent"])

    def test_nonzero_export_exit_reports_failure_when_pdf_is_valid(self):
        with tempfile.TemporaryDirectory(prefix="paperbatch-export-exit-report-test-") as directory:
            executable = Path(directory) / "soffice"
            executable.write_text("mock executable", encoding="utf-8")

            def fake_invoke(_executable, args, _profile, _timeout_seconds, **_kwargs):
                if "--convert-to" in args:
                    output_dir = Path(args[args.index("--outdir") + 1])
                    (output_dir / "input.pdf").write_bytes(minimal_pdf())
                    return {
                        "exitCode": 7,
                        "stdout": "",
                        "stderr": "converter reported failure after writing the PDF",
                        "timedOut": False,
                    }
                return {"exitCode": 0, "stdout": "LibreOffice 1.2.3", "stderr": "", "timedOut": False}

            with mock.patch.object(probe_libreoffice_export, "invoke", side_effect=fake_invoke):
                report, exit_code = run_pinned_probe(executable)

        self.assertEqual(exit_code, 1)
        self.assertEqual(report["export"]["status"], "blocked")
        self.assertTrue(report["export"]["outputStructureValid"])
        self.assertTrue(report["export"]["fixtureTextShowOperatorPresent"])
        self.assertIn("exit code 7", report["export"]["limitation"])
        self.assertNotIn("structurally valid PDF", report["export"]["limitation"])

    def test_identity_failure_reports_failure_when_pdf_is_valid(self):
        with tempfile.TemporaryDirectory(prefix="paperbatch-export-identity-report-test-") as directory:
            executable = Path(directory) / "soffice"
            executable.write_text("mock executable", encoding="utf-8")

            def fake_invoke(_executable, args, _profile, _timeout_seconds, **_kwargs):
                if "--convert-to" in args:
                    output_dir = Path(args[args.index("--outdir") + 1])
                    (output_dir / "input.pdf").write_bytes(minimal_pdf())
                    return {
                        "exitCode": 0,
                        "stdout": "",
                        "stderr": "",
                        "timedOut": False,
                        "identityVerified": False,
                        "identityFailure": "runtime components changed",
                    }
                return {"exitCode": 0, "stdout": "LibreOffice 1.2.3", "stderr": "", "timedOut": False}

            with mock.patch.object(probe_libreoffice_export, "invoke", side_effect=fake_invoke):
                report, exit_code = run_pinned_probe(executable)

        self.assertEqual(exit_code, 1)
        self.assertEqual(report["export"]["status"], "blocked")
        self.assertTrue(report["export"]["outputStructureValid"])
        self.assertTrue(report["export"]["fixtureTextShowOperatorPresent"])
        self.assertIn("runtime components changed", report["export"]["limitation"])
        self.assertNotIn("structurally valid PDF", report["export"]["limitation"])

    def test_expected_phrase_with_unresolved_font_cannot_verify_export(self):
        with tempfile.TemporaryDirectory(prefix="paperbatch-unresolved-font-test-") as directory:
            executable = Path(directory) / "soffice"
            executable.write_text("mock executable", encoding="utf-8")
            unresolved_font_pdf = minimal_pdf(
                page_dictionary_extra=b"/Resources 5 0 R ",
                extra_objects=[
                    b"5 0 obj\n<< /Font << /F2 6 0 R >> >>\nendobj\n",
                    b"6 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>\nendobj\n",
                ],
            )

            def fake_invoke(_executable, args, _profile, _timeout_seconds, **_kwargs):
                if "--convert-to" in args:
                    output_dir = Path(args[args.index("--outdir") + 1])
                    (output_dir / "input.pdf").write_bytes(unresolved_font_pdf)
                    return {"exitCode": 0, "stdout": "", "stderr": "", "timedOut": False}
                return {"exitCode": 0, "stdout": "LibreOffice 1.2.3", "stderr": "", "timedOut": False}

            with mock.patch.object(probe_libreoffice_export, "invoke", side_effect=fake_invoke):
                report, exit_code = run_pinned_probe(executable)

        self.assertEqual(1, exit_code)
        self.assertEqual("blocked", report["export"]["status"])
        self.assertTrue(report["export"]["outputStructureValid"])
        self.assertFalse(report["export"]["fixtureTextShowOperatorPresent"])

    def test_reported_pdf_hash_matches_the_bytes_used_for_inspection(self):
        with tempfile.TemporaryDirectory(prefix="paperbatch-output-hash-race-test-") as directory:
            executable = Path(directory) / "soffice"
            executable.write_text("mock executable", encoding="utf-8")
            accepted_pdf = minimal_pdf()
            replacement_pdf = minimal_pdf(page_content=b"BT /F1 11 Tf (different output) Tj ET\n")
            original_inspect = probe_libreoffice_export.inspect_pdf

            def fake_invoke(_executable, args, _profile, _timeout_seconds, **_kwargs):
                if "--convert-to" in args:
                    output_dir = Path(args[args.index("--outdir") + 1])
                    (output_dir / "input.pdf").write_bytes(accepted_pdf)
                    return {"exitCode": 0, "stdout": "", "stderr": "", "timedOut": False}
                return {"exitCode": 0, "stdout": "LibreOffice 1.2.3", "stderr": "", "timedOut": False}

            def inspect_then_replace(path):
                result = original_inspect(path)
                Path(path).write_bytes(replacement_pdf)
                return result

            with (
                mock.patch.object(probe_libreoffice_export, "invoke", side_effect=fake_invoke),
                mock.patch.object(probe_libreoffice_export, "inspect_pdf", side_effect=inspect_then_replace),
            ):
                report, exit_code = run_pinned_probe(executable)

        self.assertEqual(0, exit_code)
        self.assertEqual(hashlib.sha256(accepted_pdf).hexdigest(), report["export"]["outputSha256"])
        self.assertTrue(report["export"]["fixtureTextShowOperatorPresent"])

    @unittest.skipUnless(sys.platform.startswith("linux"), "Linux FIFO PDF-output regression")
    def test_pdf_inspection_does_not_block_on_fifo_output(self):
        with tempfile.TemporaryDirectory(prefix="paperbatch-pdf-output-fifo-test-") as directory:
            output_path = Path(directory) / "input.pdf"
            os.mkfifo(output_path)
            outcomes = []
            worker = threading.Thread(
                target=lambda: outcomes.append(probe_libreoffice_export.inspect_pdf(output_path)),
                daemon=True,
            )
            worker.start()
            worker.join(timeout=2)
            completed_without_fifo_writer = not worker.is_alive()
            if not completed_without_fifo_writer:
                with output_path.open("wb"):
                    pass
                worker.join(timeout=2)

        self.assertTrue(completed_without_fifo_writer, "PDF inspection blocked while opening a FIFO output")
        self.assertFalse(worker.is_alive(), "the bounded FIFO regression worker did not finish")
        self.assertEqual(1, len(outcomes))
        self.assertFalse(outcomes[0][2], "a FIFO must not be accepted as inspected PDF output")

    def test_expected_text_in_an_unreferenced_object_cannot_verify_export(self):
        with tempfile.TemporaryDirectory(prefix="paperbatch-orphan-text-test-") as directory:
            executable = Path(directory) / "soffice"
            executable.write_text("mock executable", encoding="utf-8")

            def fake_invoke(_executable, args, _profile, _timeout_seconds, **_kwargs):
                if "--convert-to" in args:
                    output_dir = Path(args[args.index("--outdir") + 1])
                    orphan = b"BT /F1 11 Tf (PaperBatch U0 export probe) Tj ET\n"
                    (output_dir / "input.pdf").write_bytes(
                        minimal_pdf(page_content=b"q Q\n", unreferenced_content=orphan))
                    return {"exitCode": 0, "stdout": "", "stderr": "", "timedOut": False}
                return {"exitCode": 0, "stdout": "LibreOffice 1.2.3", "stderr": "", "timedOut": False}

            with mock.patch.object(probe_libreoffice_export, "invoke", side_effect=fake_invoke):
                report, exit_code = run_pinned_probe(executable)

        self.assertEqual(exit_code, 1)
        self.assertEqual(report["export"]["status"], "blocked")
        self.assertFalse(report["export"]["fixtureTextShowOperatorPresent"])

    def test_zero_size_and_nonpainting_render_modes_cannot_verify_export(self):
        for content in (
            b"BT /F1 11 Tf 3 Tr (PaperBatch U0 export probe) Tj ET\n",
            b"BT /F1 0 Tf (PaperBatch U0 export probe) Tj ET\n",
        ):
            with self.subTest(content=content):
                with tempfile.TemporaryDirectory(prefix="paperbatch-nonpainting-text-test-") as directory:
                    executable = Path(directory) / "soffice"
                    executable.write_text("mock executable", encoding="utf-8")

                    def fake_invoke(_executable, args, _profile, _timeout_seconds, **_kwargs):
                        if "--convert-to" in args:
                            output_dir = Path(args[args.index("--outdir") + 1])
                            (output_dir / "input.pdf").write_bytes(minimal_pdf(page_content=content))
                            return {"exitCode": 0, "stdout": "", "stderr": "", "timedOut": False}
                        return {"exitCode": 0, "stdout": "LibreOffice 1.2.3", "stderr": "", "timedOut": False}

                    with mock.patch.object(probe_libreoffice_export, "invoke", side_effect=fake_invoke):
                        report, exit_code = run_pinned_probe(executable)

                self.assertEqual(exit_code, 1)
                self.assertEqual(report["export"]["status"], "blocked")
                self.assertFalse(report["export"]["fixtureTextShowOperatorPresent"])

    def test_text_inside_text_object_without_a_show_operator_cannot_verify_export(self):
        streams = [b"BT /F1 11 Tf (PaperBatch U0 export probe) ET"]
        self.assertFalse(probe_libreoffice_export.pdf_streams_contain_expected_text(
            streams, "PaperBatch U0 export probe"))
        outside_text_object = b"/F1 11 Tf (PaperBatch U0 export probe) Tj"
        self.assertFalse(probe_libreoffice_export.pdf_streams_contain_expected_text(
            [outside_text_object], "PaperBatch U0 export probe"))

    def test_text_show_operators_reject_incompatible_string_operands(self):
        array_passed_to_tj = b"BT /F1 11 Tf [(PaperBatch U0 export probe)] Tj ET"
        string_passed_to_tj_array = b"BT /F1 11 Tf (PaperBatch U0 export probe) TJ ET"
        valid_tj_array = b"BT /F1 11 Tf [(PaperBatch ) -10 (U0 export probe)] TJ ET"

        for content in (array_passed_to_tj, string_passed_to_tj_array):
            with self.subTest(content=content):
                self.assertFalse(probe_libreoffice_export.pdf_streams_contain_expected_text(
                    [content],
                    "PaperBatch U0 export probe",
                    font_names={b"F1"},
                    ascii_font_names={b"F1"},
                ))
        self.assertTrue(probe_libreoffice_export.pdf_streams_contain_expected_text(
            [valid_tj_array],
            "PaperBatch U0 export probe",
            font_names={b"F1"},
            ascii_font_names={b"F1"},
        ))

    def test_expected_fixture_text_is_decoded_from_tounicode_hex_text(self):
        text = "PaperBatch U0 export probe"
        source_bytes = b"".join(source.to_bytes(2, "big") for source in range(1, len(text) + 1))
        cmap_entries = b"\n".join(
            f"<{source:04X}> <{ord(character):04X}>".encode("ascii")
            for source, character in zip(range(1, len(text) + 1), text)
        )
        cmap = b"begincmap\n" + str(len(text)).encode("ascii") + b" beginbfchar\n" + cmap_entries + b"\nendbfchar\nendcmap"
        content = b"BT /F1 11 Tf <" + source_bytes.hex().encode("ascii") + b"> Tj ET\n"
        pdf = minimal_pdf(
            page_content=content,
            page_dictionary_extra=b"/Resources 5 0 R ",
            extra_objects=[
                b"5 0 obj\n<< /Font << /F1 6 0 R >> >>\nendobj\n",
                b"6 0 obj\n<< /Type /Font /Subtype /Type0 /Encoding /Identity-H /ToUnicode 7 0 R >>\nendobj\n",
                f"7 0 obj\n<< /Length {len(cmap)} >>\nstream\n".encode("ascii")
                + cmap
                + b"\nendstream\nendobj\n",
            ],
        )
        expected_content: list[bool] = []
        self.assertTrue(fixture_classic_xref_is_valid(
            pdf, expected_text=text, expected_content_observation=expected_content))
        self.assertEqual([True], expected_content)

    def test_type0_font_without_supported_encoding_cannot_verify_fixture_text(self):
        text = "PaperBatch U0 export probe"
        source_bytes = bytes(range(1, len(text) + 1))
        cmap_entries = b"\n".join(
            f"<{source:02X}> <{ord(character):04X}>".encode("ascii")
            for source, character in zip(source_bytes, text)
        )
        cmap = b"begincmap\n" + str(len(text)).encode("ascii") + b" beginbfchar\n" + cmap_entries + b"\nendbfchar\nendcmap"
        content = b"BT /F1 11 Tf <" + source_bytes.hex().encode("ascii") + b"> Tj ET\n"
        pdf = minimal_pdf(
            page_content=content,
            page_dictionary_extra=b"/Resources 5 0 R ",
            extra_objects=[
                b"5 0 obj\n<< /Font << /F1 6 0 R >> >>\nendobj\n",
                b"6 0 obj\n<< /Type /Font /Subtype /Type0 /ToUnicode 7 0 R >>\nendobj\n",
                f"7 0 obj\n<< /Length {len(cmap)} >>\nstream\n".encode("ascii")
                + cmap
                + b"\nendstream\nendobj\n",
            ],
        )
        expected_content: list[bool] = []

        self.assertTrue(fixture_classic_xref_is_valid(
            pdf, text, expected_content))
        self.assertEqual([False], expected_content)

    def test_tounicode_decoder_rejects_ambiguous_code_widths(self):
        unicode_map = {
            b"\x01": "X",
            b"\x01\x02": "PaperBatch U0 export probe",
        }

        self.assertNotEqual(
            "PaperBatch U0 export probe",
            probe_libreoffice_export.decode_pdf_shown_text(b"\x01\x02", unicode_map),
        )

    def test_unreferenced_tounicode_cmap_cannot_decode_page_text(self):
        text = "PaperBatch U0 export probe"
        source_bytes = bytes(range(1, len(text) + 1))
        cmap_entries = b"\n".join(
            f"<{source:02X}> <{ord(character):04X}>".encode("ascii")
            for source, character in zip(source_bytes, text)
        )
        unrelated_cmap = b"begincmap\n" + str(len(text)).encode("ascii") + b" beginbfchar\n" + cmap_entries + b"\nendbfchar\nendcmap"
        content = b"BT /F1 11 Tf <" + source_bytes.hex().encode("ascii") + b"> Tj ET\n"
        pdf = minimal_pdf(
            page_content=content,
            unreferenced_content=unrelated_cmap,
        )
        expected_content: list[bool] = []
        self.assertTrue(fixture_classic_xref_is_valid(
            pdf, expected_text=text, expected_content_observation=expected_content))
        self.assertEqual([False], expected_content)

    def test_zero_page_pdf_cannot_verify_export(self):
        with tempfile.TemporaryDirectory(prefix="paperbatch-zero-page-pdf-test-") as directory:
            executable = Path(directory) / "soffice"
            executable.write_text("mock executable", encoding="utf-8")

            def fake_invoke(_executable, args, _profile, _timeout_seconds, **_kwargs):
                if "--convert-to" in args:
                    output_dir = Path(args[args.index("--outdir") + 1])
                    (output_dir / "input.pdf").write_bytes(minimal_pdf(page_count=0))
                    return {"exitCode": 0, "stdout": "", "stderr": "", "timedOut": False}
                return {"exitCode": 0, "stdout": "LibreOffice 1.2.3", "stderr": "", "timedOut": False}

            with mock.patch.object(probe_libreoffice_export, "invoke", side_effect=fake_invoke):
                report, exit_code = run_pinned_probe(executable)

        self.assertEqual(exit_code, 1)
        self.assertEqual(report["export"]["status"], "blocked")
        self.assertFalse(report["export"]["outputStructureValid"])

    def test_empty_page_content_cannot_verify_export(self):
        with tempfile.TemporaryDirectory(prefix="paperbatch-empty-page-content-test-") as directory:
            executable = Path(directory) / "soffice"
            executable.write_text("mock executable", encoding="utf-8")

            def fake_invoke(_executable, args, _profile, _timeout_seconds, **_kwargs):
                if "--convert-to" in args:
                    output_dir = Path(args[args.index("--outdir") + 1])
                    (output_dir / "input.pdf").write_bytes(minimal_pdf(page_content=b""))
                    return {"exitCode": 0, "stdout": "", "stderr": "", "timedOut": False}
                return {"exitCode": 0, "stdout": "LibreOffice 1.2.3", "stderr": "", "timedOut": False}

            with mock.patch.object(probe_libreoffice_export, "invoke", side_effect=fake_invoke):
                report, exit_code = run_pinned_probe(executable)

        self.assertEqual(exit_code, 1)
        self.assertEqual(report["export"]["status"], "blocked")
        self.assertFalse(report["export"]["outputStructureValid"])

    def test_page_content_stream_must_end_its_indirect_object(self):
        with tempfile.TemporaryDirectory(prefix="paperbatch-page-stream-object-test-") as directory:
            executable = Path(directory) / "soffice"
            executable.write_text("mock executable", encoding="utf-8")

            def fake_invoke(_executable, args, _profile, _timeout_seconds, **_kwargs):
                if "--convert-to" in args:
                    output_dir = Path(args[args.index("--outdir") + 1])
                    malformed = minimal_pdf().replace(b"endstream\nendobj\n", b"endstream\nxxxxxx\n", 1)
                    (output_dir / "input.pdf").write_bytes(malformed)
                    return {"exitCode": 0, "stdout": "", "stderr": "", "timedOut": False}
                return {"exitCode": 0, "stdout": "LibreOffice 1.2.3", "stderr": "", "timedOut": False}

            with mock.patch.object(probe_libreoffice_export, "invoke", side_effect=fake_invoke):
                report, exit_code = run_pinned_probe(executable)

        self.assertEqual(exit_code, 1)
        self.assertEqual(report["export"]["status"], "blocked")
        self.assertFalse(report["export"]["outputStructureValid"])

    def test_launcher_banner_cannot_replace_a_pinned_engine_binary(self):
        with tempfile.TemporaryDirectory(prefix="paperbatch-unpinned-lo-test-") as directory:
            executable = Path(directory) / "soffice"
            executable.write_text("self-reported shim", encoding="utf-8")

            def fake_invoke(_executable, args, _profile, _timeout_seconds, **_kwargs):
                if "--convert-to" in args:
                    output_dir = Path(args[args.index("--outdir") + 1])
                    (output_dir / "input.pdf").write_bytes(minimal_pdf())
                    return {"exitCode": 0, "stdout": "", "stderr": "", "timedOut": False}
                return {"exitCode": 0, "stdout": "LibreOffice 1.2.3", "stderr": "", "timedOut": False}

            with mock.patch.object(probe_libreoffice_export, "invoke", side_effect=fake_invoke) as invoke_mock:
                report, exit_code = probe_libreoffice_export.run_probe(
                    str(executable), timeout_seconds=5, engine_binary=None, expected_engine_sha256=None
                )

        self.assertEqual(exit_code, 1)
        self.assertEqual(report["libreOffice"]["status"], "blocked")
        self.assertEqual(report["export"]["status"], "blocked")
        invoke_mock.assert_not_called()

    @unittest.skipUnless(sys.platform.startswith("linux"), "requires Linux /proc/self/fd execution")
    def test_path_replacement_after_pin_executes_the_opened_engine_image(self):
        with tempfile.TemporaryDirectory(prefix="paperbatch-pinned-image-race-test-") as directory:
            runtime_directory = Path(directory) / "program"
            runtime_directory.mkdir()
            engine = runtime_directory / "soffice.bin"
            replacement = Path(directory) / "replacement.bin"
            moved_engine = runtime_directory / "pinned-original.bin"
            engine.write_text("#!/bin/sh\nprintf '%s' original-image\n", encoding="utf-8")
            replacement.write_text("#!/bin/sh\nprintf '%s' replacement-image\n", encoding="utf-8")
            engine.chmod(0o755)
            replacement.chmod(0o755)
            expected_sha256 = hashlib.sha256(engine.read_bytes()).hexdigest()
            real_run = probe_libreoffice_export.subprocess.run
            observed_child_output = []

            def replace_path_then_launch(command, **kwargs):
                engine.rename(moved_engine)
                replacement.rename(engine)
                completed = real_run(command, **kwargs)
                observed_child_output.append(completed.stdout.strip())
                return completed

            with mock.patch.object(probe_libreoffice_export.subprocess, "run", side_effect=replace_path_then_launch):
                result = probe_libreoffice_export.invoke(
                    engine,
                    ["--version"],
                    Path(directory) / "profile",
                    timeout_seconds=5,
                    expected_engine_sha256=expected_sha256,
                )

        self.assertEqual(result["exitCode"], 0)
        self.assertEqual(observed_child_output, ["original-image"])
        self.assertEqual(result["stdout"], "original-image")
        self.assertEqual(result["command"][0], result["executionTarget"])
        self.assertTrue(result["executionTarget"].startswith("/proc/self/fd/"))
        self.assertFalse(result["identityVerified"])
        self.assertIn("runtime components changed", result["identityFailure"].lower())
        self.assertRegex(result["runtimeDirectorySha256"], r"^[0-9a-f]{64}$")

    def test_mismatched_binary_digest_blocks_before_launch(self):
        with tempfile.TemporaryDirectory(prefix="paperbatch-mismatched-lo-pin-test-") as directory:
            executable = Path(directory) / "soffice"
            executable.write_text("self-reported shim", encoding="utf-8")

            with mock.patch.object(probe_libreoffice_export, "invoke") as invoke_mock:
                report, exit_code = probe_libreoffice_export.run_probe(
                    None,
                    timeout_seconds=5,
                    engine_binary=str(executable),
                    expected_engine_sha256="0" * 64,
                )

        self.assertEqual(exit_code, 1)
        self.assertEqual(report["libreOffice"]["status"], "blocked")
        self.assertEqual(report["export"]["status"], "blocked")
        invoke_mock.assert_not_called()

    def test_docx_fixture_is_deterministic_and_contains_expected_parts(self):
        with tempfile.TemporaryDirectory(prefix="paperbatch-fixture-test-") as directory:
            first = Path(directory) / "first.docx"
            second = Path(directory) / "second.docx"
            probe_libreoffice_export.write_fixture(first)
            probe_libreoffice_export.write_fixture(second)

            self.assertEqual(hashlib.sha256(first.read_bytes()).digest(), hashlib.sha256(second.read_bytes()).digest())
            with zipfile.ZipFile(first) as archive:
                self.assertEqual(
                    set(archive.namelist()),
                    {"[Content_Types].xml", "_rels/.rels", "word/document.xml"},
                )
                self.assertIn(b"PaperBatch U0 export probe", archive.read("word/document.xml"))

    def test_unavailable_report_keeps_gate_open_and_has_no_native_passes(self):
        report = probe_libreoffice_export.unavailable_report("not installed")

        self.assertEqual(report["libreOffice"]["status"], "unavailable")
        self.assertEqual(report["libreOffice"]["source"], "environment")
        self.assertEqual(report["export"]["status"], "unavailable")
        self.assertEqual(report["export"]["source"], "native")
        self.assertEqual(report["coexistence"]["status"], "unavailable")
        self.assertEqual(report["nativeCompatibilityGate"], "open")
        self.assertEqual(report["validatedNativeBehaviors"], [])

    def test_environment_identifier_requires_explicit_opt_in(self):
        with mock.patch.dict(probe_libreoffice_export.os.environ, {"PAPERBATCH_PROBE_ENVIRONMENT_ID": "lab-42"}):
            default = probe_libreoffice_export.unavailable_report("not installed")
            opted_in = probe_libreoffice_export.unavailable_report("not installed", include_device_identifiers=True)

        self.assertNotIn("environmentId", default["environment"])
        self.assertEqual(opted_in["environment"]["environmentId"], "lab-42")

    def test_cli_device_identifier_flag_is_forwarded_into_the_report(self):
        with tempfile.TemporaryDirectory(prefix="paperbatch-device-id-test-") as directory:
            executable = Path(directory) / "soffice"
            executable.write_text("mock executable", encoding="utf-8")

            def fake_invoke(_executable, args, _profile, _timeout_seconds, **_kwargs):
                if "--convert-to" in args:
                    output_dir = Path(args[args.index("--outdir") + 1])
                    (output_dir / "input.pdf").write_bytes(minimal_pdf())
                    return {"exitCode": 0, "stdout": "", "stderr": "", "timedOut": False}
                return {"exitCode": 0, "stdout": "LibreOffice 1.2.3", "stderr": "", "timedOut": False}

            output = io.StringIO()
            with (
                mock.patch.dict(probe_libreoffice_export.os.environ, {"PAPERBATCH_PROBE_ENVIRONMENT_ID": "lab-42"}),
                mock.patch.object(probe_libreoffice_export, "invoke", side_effect=fake_invoke),
                contextlib.redirect_stdout(output),
            ):
                exit_code = probe_libreoffice_export.main(
                    [
                        "--engine-binary", str(executable),
                        "--expected-engine-sha256", hashlib.sha256(executable.read_bytes()).hexdigest(),
                        "--include-device-identifiers",
                    ]
                )

        report = json.loads(output.getvalue())
        self.assertEqual(exit_code, 0)
        self.assertEqual(report["environment"]["environmentId"], "lab-42")

    def test_installed_suite_coexistence_is_not_run_without_controlled_session(self):
        with tempfile.TemporaryDirectory(prefix="paperbatch-installed-suite-test-") as directory:
            executable = Path(directory) / "soffice"
            executable.write_text("mock executable", encoding="utf-8")

            def fake_invoke(_executable, args, _profile, _timeout_seconds, **_kwargs):
                if "--convert-to" in args:
                    output_dir = Path(args[args.index("--outdir") + 1])
                    (output_dir / "input.pdf").write_bytes(minimal_pdf())
                    return {"exitCode": 0, "stdout": "", "stderr": "", "timedOut": False}
                return {"exitCode": 0, "stdout": "LibreOffice 1.2.3", "stderr": "", "timedOut": False}

            with mock.patch.object(probe_libreoffice_export, "invoke", side_effect=fake_invoke):
                report, exit_code = run_pinned_probe(executable)

        self.assertEqual(exit_code, 0)
        self.assertEqual(report["export"]["status"], "verified")
        self.assertTrue(report["export"]["outputStructureValid"])
        self.assertTrue(report["export"]["fixtureTextShowOperatorPresent"])
        self.assertEqual(report["coexistence"]["status"], "not-run")

    def test_pdf_header_without_cross_reference_and_eof_is_blocked(self):
        with tempfile.TemporaryDirectory(prefix="paperbatch-truncated-pdf-test-") as directory:
            executable = Path(directory) / "soffice"
            executable.write_text("mock executable", encoding="utf-8")

            def fake_invoke(_executable, args, _profile, _timeout_seconds, **_kwargs):
                if "--convert-to" in args:
                    output_dir = Path(args[args.index("--outdir") + 1])
                    (output_dir / "input.pdf").write_bytes(b"%PDF-1.7\nmock export")
                    return {"exitCode": 0, "stdout": "", "stderr": "", "timedOut": False}
                return {"exitCode": 0, "stdout": "LibreOffice 1.2.3", "stderr": "", "timedOut": False}

            with mock.patch.object(probe_libreoffice_export, "invoke", side_effect=fake_invoke):
                report, exit_code = run_pinned_probe(executable)

        self.assertEqual(exit_code, 1)
        self.assertEqual(report["export"]["status"], "blocked")
        self.assertFalse(report["export"]["outputStructureValid"])

    def test_structural_pdf_inspection_does_not_require_smoke_fixture_text(self):
        without_fixture_text = minimal_pdf(page_content=b"BT /F1 11 Tf (Corpus fixture text) Tj ET\n")
        with tempfile.TemporaryDirectory(prefix="paperbatch-corpus-pdf-test-") as directory:
            pdf_path = Path(directory) / "corpus.pdf"
            pdf_path.write_bytes(without_fixture_text)

            smoke_result = probe_libreoffice_export.inspect_pdf(pdf_path)
            corpus_result = probe_libreoffice_export.inspect_pdf(pdf_path, expected_text=None)

        self.assertTrue(smoke_result[2])
        self.assertFalse(smoke_result[3])
        self.assertTrue(corpus_result[2])
        self.assertFalse(corpus_result[3])

    def test_startxref_text_inside_a_comment_cannot_verify_pdf_structure(self):
        with tempfile.TemporaryDirectory(prefix="paperbatch-commented-startxref-test-") as directory:
            pdf_path = Path(directory) / "commented-startxref.pdf"
            valid_pdf = minimal_pdf()
            actual_startxref = valid_pdf.rfind(b"startxref\n")
            xref_offset = int(
                valid_pdf[actual_startxref + len(b"startxref\n"):].splitlines()[0]
            )
            eof_position = valid_pdf.rfind(b"%%EOF")
            pdf_path.write_bytes(
                valid_pdf[:actual_startxref]
                + b"% startxref\n"
                + str(xref_offset).encode("ascii")
                + b"\n"
                + valid_pdf[eof_position:]
            )

            result = probe_libreoffice_export.inspect_pdf(pdf_path)

        size, header_valid, structure_valid, fixture_text_present, digest = result
        self.assertGreater(size or 0, 0)
        self.assertTrue(header_valid)
        self.assertFalse(structure_valid)
        self.assertFalse(fixture_text_present)
        self.assertRegex(digest or "", r"^[0-9a-f]{64}$")

    def test_startxref_inside_unterminated_literal_cannot_verify_pdf_structure(self):
        with tempfile.TemporaryDirectory(prefix="paperbatch-literal-startxref-test-") as directory:
            pdf_path = Path(directory) / "literal-startxref.pdf"
            valid_pdf = minimal_pdf()
            xref_offset = valid_pdf.index(b"xref\n")
            actual_startxref = valid_pdf.rfind(b"startxref\n")
            fake_xref_offset = xref_offset + 1
            malformed_pdf = (
                valid_pdf[:xref_offset]
                + b"("
                + valid_pdf[xref_offset:actual_startxref]
                + b"startxref\n"
                + str(fake_xref_offset).encode("ascii")
                + b"\n%%EOF\n"
            )
            pdf_path.write_bytes(malformed_pdf)

            result = probe_libreoffice_export.inspect_pdf(pdf_path)

        size, header_valid, structure_valid, fixture_text_present, digest = result
        self.assertGreater(size or 0, 0)
        self.assertTrue(header_valid)
        self.assertFalse(structure_valid)
        self.assertFalse(fixture_text_present)
        self.assertRegex(digest or "", r"^[0-9a-f]{64}$")

    def test_startxref_inside_unterminated_header_literal_cannot_verify_pdf_structure(self):
        with tempfile.TemporaryDirectory(prefix="paperbatch-header-literal-startxref-test-") as directory:
            pdf_path = Path(directory) / "header-literal-startxref.pdf"
            valid_pdf = minimal_pdf()
            xref_offset = valid_pdf.index(b"xref\n")
            actual_startxref = valid_pdf.rfind(b"startxref\n")
            malformed_pdf = (
                valid_pdf[:actual_startxref].replace(b"%PDF-1.7\n", b"%PDF-1.7(", 1)
                + b"startxref\n"
                + str(xref_offset).encode("ascii")
                + b"\n%%EOF\n"
            )
            pdf_path.write_bytes(malformed_pdf)

            result = probe_libreoffice_export.inspect_pdf(pdf_path)

        _size, _header_valid, structure_valid, fixture_text_present, _digest = result
        self.assertFalse(structure_valid)
        self.assertFalse(fixture_text_present)

    def test_vertical_tab_is_not_pdf_whitespace_in_structure_tokens(self):
        valid_pdf = minimal_pdf()
        startxref_offset = valid_pdf.rfind(b"startxref\n")
        mutations = [
            (
                "object header separator",
                valid_pdf.replace(b"1 0 obj", b"1\x0b0 obj", 1),
            ),
            (
                "xref token suffix",
                valid_pdf.replace(b"xref\n", b"xref\x0b\n", 1),
            ),
            (
                "xref subsection separator",
                valid_pdf.replace(b"xref\n0 ", b"xref\n0\x0b", 1),
            ),
            (
                "xref subsection suffix",
                valid_pdf.replace(b"0 6\n", b"0 6\x0b\n", 1),
            ),
            (
                "xref entry separator",
                valid_pdf.replace(b"0000000000 65535 f", b"0000000000\x0b65535 f", 1),
            ),
            (
                "xref entry suffix",
                valid_pdf.replace(b"0000000000 65535 f \n", b"0000000000 65535 f\x0b\n", 1),
            ),
            (
                "startxref separator",
                valid_pdf[:startxref_offset]
                + valid_pdf[startxref_offset:].replace(b"startxref\n", b"startxref\x0b", 1),
            ),
            ("trailing EOF byte", valid_pdf + b"\x0b"),
        ]

        for name, malformed_pdf in mutations:
            with self.subTest(name=name), tempfile.TemporaryDirectory(prefix="paperbatch-vt-pdf-test-") as directory:
                pdf_path = Path(directory) / "vertical-tab.pdf"
                pdf_path.write_bytes(malformed_pdf)

                result = probe_libreoffice_export.inspect_pdf(pdf_path)

            self.assertFalse(result[2], f"{name} was accepted")

    def test_indirect_object_marker_requires_a_pdf_token_boundary(self):
        malformed_object = b"5 0 obj.1 endobj\n"
        self.assertIsNone(
            probe_libreoffice_export.PDF_INDIRECT_OBJECT_HEADER.match(malformed_object)
        )
        malformed_pdf = minimal_pdf(extra_objects=[malformed_object])

        with tempfile.TemporaryDirectory(prefix="paperbatch-object-marker-boundary-test-") as directory:
            pdf_path = Path(directory) / "malformed-object-marker.pdf"
            pdf_path.write_bytes(malformed_pdf)

            result = probe_libreoffice_export.inspect_pdf(pdf_path)

        self.assertFalse(result[2])

    def test_indirect_object_rejects_unrecognized_regular_tokens(self):
        malformed_pdf = minimal_pdf(
            extra_objects=[b"5 0 obj\nnot-a-PDF-object\nendobj\n"]
        )

        with tempfile.TemporaryDirectory(prefix="paperbatch-object-token-test-") as directory:
            pdf_path = Path(directory) / "invalid-object-token.pdf"
            pdf_path.write_bytes(malformed_pdf)

            result = probe_libreoffice_export.inspect_pdf(pdf_path)

        self.assertFalse(result[2])

    def test_indirect_object_accepts_pdf_booleans_null_and_real_numbers(self):
        valid_pdf = minimal_pdf(
            extra_objects=[b"5 0 obj\n[true false null -1.25 .5 3.]\nendobj\n"]
        )

        with tempfile.TemporaryDirectory(prefix="paperbatch-valid-object-values-test-") as directory:
            pdf_path = Path(directory) / "valid-object-values.pdf"
            pdf_path.write_bytes(valid_pdf)

            result = probe_libreoffice_export.inspect_pdf(pdf_path)

        self.assertTrue(result[2])

    def test_pdf_marker_words_without_valid_trailer_or_xref_are_blocked(self):
        with tempfile.TemporaryDirectory(prefix="paperbatch-malformed-pdf-test-") as directory:
            executable = Path(directory) / "soffice"
            executable.write_text("mock executable", encoding="utf-8")

            def fake_invoke(_executable, args, _profile, _timeout_seconds, **_kwargs):
                if "--convert-to" in args:
                    output_dir = Path(args[args.index("--outdir") + 1])
                    header = b"%PDF-1.7\n"
                    body = b"This body contains /Root but no catalog.\n"
                    xref_offset = len(header) + len(body)
                    malformed = (
                        header
                        + body
                        + b"xref-not-a-table\n"
                        + b"trailer << /Root invalid >>\nstartxref\n"
                        + str(xref_offset).encode("ascii")
                        + b"\n%%EOF\n"
                    )
                    (output_dir / "input.pdf").write_bytes(malformed)
                    return {"exitCode": 0, "stdout": "", "stderr": "", "timedOut": False}
                return {"exitCode": 0, "stdout": "LibreOffice 1.2.3", "stderr": "", "timedOut": False}

            with mock.patch.object(probe_libreoffice_export, "invoke", side_effect=fake_invoke):
                report, exit_code = run_pinned_probe(executable)

        self.assertEqual(exit_code, 1)
        self.assertEqual(report["export"]["status"], "blocked")
        self.assertFalse(report["export"]["outputStructureValid"])

    def test_pdf_trailer_root_must_resolve_to_a_catalog(self):
        with tempfile.TemporaryDirectory(prefix="paperbatch-wrong-root-pdf-test-") as directory:
            executable = Path(directory) / "soffice"
            executable.write_text("mock executable", encoding="utf-8")

            def fake_invoke(_executable, args, _profile, _timeout_seconds, **_kwargs):
                if "--convert-to" in args:
                    output_dir = Path(args[args.index("--outdir") + 1])
                    (output_dir / "input.pdf").write_bytes(minimal_pdf(root_type="Page"))
                    return {"exitCode": 0, "stdout": "", "stderr": "", "timedOut": False}
                return {"exitCode": 0, "stdout": "LibreOffice 1.2.3", "stderr": "", "timedOut": False}

            with mock.patch.object(probe_libreoffice_export, "invoke", side_effect=fake_invoke):
                report, exit_code = run_pinned_probe(executable)

        self.assertEqual(exit_code, 1)
        self.assertEqual(report["export"]["status"], "blocked")
        self.assertFalse(report["export"]["outputStructureValid"])

    def test_catalog_marker_inside_a_string_does_not_make_root_a_catalog(self):
        with tempfile.TemporaryDirectory(prefix="paperbatch-catalog-string-test-") as directory:
            executable = Path(directory) / "soffice"
            executable.write_text("mock executable", encoding="utf-8")

            def fake_invoke(_executable, args, _profile, _timeout_seconds, **_kwargs):
                if "--convert-to" in args:
                    output_dir = Path(args[args.index("--outdir") + 1])
                    (output_dir / "input.pdf").write_bytes(
                        minimal_pdf(root_type="Page", root_extra=b"/Label (/Type /Catalog)")
                    )
                    return {"exitCode": 0, "stdout": "", "stderr": "", "timedOut": False}
                return {"exitCode": 0, "stdout": "LibreOffice 1.2.3", "stderr": "", "timedOut": False}

            with mock.patch.object(probe_libreoffice_export, "invoke", side_effect=fake_invoke):
                report, exit_code = run_pinned_probe(executable)

        self.assertEqual(exit_code, 1)
        self.assertEqual(report["export"]["status"], "blocked")
        self.assertFalse(report["export"]["outputStructureValid"])

    def test_pdf_trailer_rejects_syntax_after_its_dictionary(self):
        with tempfile.TemporaryDirectory(prefix="paperbatch-pdf-trailing-trailer-test-") as directory:
            executable = Path(directory) / "soffice"
            executable.write_text("mock executable", encoding="utf-8")

            def fake_invoke(_executable, args, _profile, _timeout_seconds, **_kwargs):
                if "--convert-to" in args:
                    output_dir = Path(args[args.index("--outdir") + 1])
                    (output_dir / "input.pdf").write_bytes(minimal_pdf(trailer_suffix=b"unexpected-token\n"))
                    return {"exitCode": 0, "stdout": "", "stderr": "", "timedOut": False}
                return {"exitCode": 0, "stdout": "LibreOffice 1.2.3", "stderr": "", "timedOut": False}

            with mock.patch.object(probe_libreoffice_export, "invoke", side_effect=fake_invoke):
                report, exit_code = run_pinned_probe(executable)

        self.assertEqual(exit_code, 1)
        self.assertEqual(report["export"]["status"], "blocked")
        self.assertFalse(report["export"]["outputStructureValid"])

    def test_pdf_reference_generation_must_fit_the_spec_limit(self):
        with tempfile.TemporaryDirectory(prefix="paperbatch-pdf-generation-test-") as directory:
            executable = Path(directory) / "soffice"
            executable.write_text("mock executable", encoding="utf-8")

            def fake_invoke(_executable, args, _profile, _timeout_seconds, **_kwargs):
                if "--convert-to" in args:
                    output_dir = Path(args[args.index("--outdir") + 1])
                    (output_dir / "input.pdf").write_bytes(minimal_pdf(root_generation=99999))
                    return {"exitCode": 0, "stdout": "", "stderr": "", "timedOut": False}
                return {"exitCode": 0, "stdout": "LibreOffice 1.2.3", "stderr": "", "timedOut": False}

            with mock.patch.object(probe_libreoffice_export, "invoke", side_effect=fake_invoke):
                report, exit_code = run_pinned_probe(executable)

        self.assertEqual(exit_code, 1)
        self.assertEqual(report["export"]["status"], "blocked")
        self.assertFalse(report["export"]["outputStructureValid"])

    def test_oversized_pdf_object_numbers_are_blocked_without_raising(self):
        with tempfile.TemporaryDirectory(prefix="paperbatch-pdf-number-limit-test-") as directory:
            executable = Path(directory) / "soffice"
            executable.write_text("mock executable", encoding="utf-8")
            long_number = b"9" * 5000
            malformed_pdfs = (
                minimal_pdf().replace(b"xref\n0 ", b"xref\n" + long_number + b" 1\n", 1),
                minimal_pdf().replace(b"1 0 obj\n", long_number + b" 0 obj\n", 1),
            )

            for malformed in malformed_pdfs:
                with self.subTest(malformed_prefix=malformed[:32]):
                    def fake_invoke(_executable, args, _profile, _timeout_seconds, **_kwargs):
                        if "--convert-to" in args:
                            output_dir = Path(args[args.index("--outdir") + 1])
                            (output_dir / "input.pdf").write_bytes(malformed)
                            return {"exitCode": 0, "stdout": "", "stderr": "", "timedOut": False}
                        return {"exitCode": 0, "stdout": "LibreOffice 1.2.3", "stderr": "", "timedOut": False}

                    with mock.patch.object(probe_libreoffice_export, "invoke", side_effect=fake_invoke):
                        report, exit_code = run_pinned_probe(executable)

                    self.assertEqual(exit_code, 1)
                    self.assertEqual(report["export"]["status"], "blocked")
                    self.assertFalse(report["export"]["outputStructureValid"])

    def test_pdf_xref_entries_must_point_to_their_declared_objects(self):
        with tempfile.TemporaryDirectory(prefix="paperbatch-bad-xref-pdf-test-") as directory:
            executable = Path(directory) / "soffice"
            executable.write_text("mock executable", encoding="utf-8")

            def fake_invoke(_executable, args, _profile, _timeout_seconds, **_kwargs):
                if "--convert-to" in args:
                    output_dir = Path(args[args.index("--outdir") + 1])
                    malformed = minimal_pdf().replace(b"0000000009 00000 n", b"9999999999 00000 n")
                    (output_dir / "input.pdf").write_bytes(malformed)
                    return {"exitCode": 0, "stdout": "", "stderr": "", "timedOut": False}
                return {"exitCode": 0, "stdout": "LibreOffice 1.2.3", "stderr": "", "timedOut": False}

            with mock.patch.object(probe_libreoffice_export, "invoke", side_effect=fake_invoke):
                report, exit_code = run_pinned_probe(executable)

        self.assertEqual(exit_code, 1)
        self.assertEqual(report["export"]["status"], "blocked")
        self.assertFalse(report["export"]["outputStructureValid"])

    def test_unreadable_executable_hash_returns_blocked_evidence(self):
        with tempfile.TemporaryDirectory(prefix="paperbatch-unreadable-binary-test-") as directory:
            executable = Path(directory) / "soffice"
            executable.write_text("mock executable", encoding="utf-8")
            expected_sha256 = hashlib.sha256(executable.read_bytes()).hexdigest()

            with mock.patch.object(probe_libreoffice_export, "sha256_stream", side_effect=PermissionError("read denied")):
                report, exit_code = probe_libreoffice_export.run_probe(
                    None,
                    timeout_seconds=5,
                    engine_binary=str(executable),
                    expected_engine_sha256=expected_sha256,
                )

        self.assertEqual(exit_code, 1)
        self.assertEqual(report["libreOffice"]["status"], "blocked")
        self.assertEqual(report["libreOffice"]["limitation"], "read denied")
        self.assertEqual(report["export"]["status"], "blocked")

    def test_non_libreoffice_binary_cannot_produce_libreoffice_evidence(self):
        with tempfile.TemporaryDirectory(prefix="paperbatch-other-suite-test-") as directory:
            executable = Path(directory) / "other-converter"
            executable.write_text("mock executable", encoding="utf-8")

            def fake_invoke(_executable, args, _profile, _timeout_seconds, **_kwargs):
                self.assertNotIn("--convert-to", args)
                return {"exitCode": 0, "stdout": "Other Converter 3.0", "stderr": "", "timedOut": False}

            with mock.patch.object(probe_libreoffice_export, "invoke", side_effect=fake_invoke):
                report, exit_code = run_pinned_probe(executable)

        self.assertEqual(exit_code, 1)
        self.assertEqual(report["libreOffice"]["status"], "blocked")
        self.assertIn("does not identify as LibreOffice", report["libreOffice"]["limitation"])
        self.assertEqual(report["export"]["status"], "blocked")
        self.assertEqual(report["validatedNativeBehaviors"], [])


if __name__ == "__main__":
    unittest.main()
