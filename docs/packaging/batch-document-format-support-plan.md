# Batch document-format support plan (pre-U10)

## Purpose
PaperBatch must let users add multiple supported documents to one run and render them into printable PDFs inside PaperBatch. LibreOffice may act as the conversion engine when explicitly configured. This is an in-app conversion path; it does not launch Microsoft Office or WPS and does not claim equivalent output fidelity. U10 real-user trials remain deferred.

## Current gap
The production shell registers PDF pass-through and built-in JPEG/PNG rendering only. The repository has a hardened `LibreOfficeRendererAdapter`, but neither the desktop production composition nor coordinator IPC registers it, and the adapter's capability check currently rejects Windows. Consequently document intake can recognize OOXML and other formats without enabling those files in actual batch preparation/printing.

## Unit BDF1 — production conversion and mixed-batch CI
- Register the existing LibreOffice adapter in both desktop and coordinator production renderer factories only when explicit engine-path and trusted SHA-256 configuration is present. Keep PDFium as the separately required PDF inspection/composition runtime. No auto-discovery, download, bundled proprietary app, WPS/Office launch, or silent fallback.
- Extend the safe direct-engine conversion path to Windows and Linux, with private per-operation profiles, macro/link protections, bounded source/output/process limits, cancellation and process cleanup. MacOS product conversion remains explicitly unsupported unless already safe and supported by the implementation.
- Make UI availability state explain whether document conversion is configured; unsupported/unavailable input must fail before any print submission.
- Exercise DOCX, XLSX, PPTX and OpenDocument/RTF fixtures through the same production batch preparation/import path. Assert order, persisted PDF artifact metadata, stable source identity, mixed PDF/image/document batches, conversion failures, no fallback, and no duplicate submission. Submit only to an injected fake spooler.
- CI must run on hosted Windows and Linux with a verifiably pinned LibreOffice engine and synthetic fixtures; report exact engine/tool versions, runner, scenario outcomes and evidence limits. Existing macOS CI remains a protocol/unsupported-platform lane, not PaperBatch macOS support. Never claim Office/WPS equivalence, human fidelity, driver compatibility, or paper output.

## Acceptance gates
One isolated Coding unit; focused red/green regressions; relevant managed and CI suites pass; two independent complete full-diff Reviewer rounds return no findings; correctly titled draft PR; exact-head CI green; parent verifies all evidence and auto-squash-merges; exact post-merge main CI and U9 matrix pass. No release/tag, paid resources, physical printing, or U10.

## Follow-up
After BDF1, refresh the U9 final-evidence review against current `main` and record any remaining partial/blocked evidence. Archived GitHub artifact bodies remain uninspected until an authorized route can retrieve them; never infer their contents from metadata.
