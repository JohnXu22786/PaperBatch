# Renderer pipeline boundary

This follow-up adds a private persisted PDF artifact store, an exact-byte PDF
pass-through adapter, a built-in one-page JPEG/PNG-to-PDF adapter, and an
isolated LibreOffice command-line adapter. Production composition registers the
PDF and built-in image adapters for background rendering. LibreOffice remains
unregistered and unavailable to production until the tested engine is included
in the application's trusted offline runtime. CI pins LibreOffice 26.2.2.2 for
an Ubuntu x86_64 adapter integration check. PDF pass-through validates the cached
snapshot with source preflight checks, verifies the snapshot digest and length,
stores the bytes under a content-derived ID, and verifies that ID again when
opening the artifact. Pass-through does not claim that the document was
rendered, and it does not claim a page count.

Pinned native executables/libraries are copied to owner-only worker directories,
then the staged bytes are checked against their configured digests before launch
or load. PDFium uses Unix owner-only mode bits and the repository's owner-only
Windows ACL helper; cleanup checks its exact runtime owner marker. LibreOffice
uses a Linux-only owner-only operation directory and marker. Its source program
tree and shared-library paths must also be non-writable by the current user or
other untrusted accounts; otherwise its capability probe reports unavailable.

The following renderer limitations remain:

1. Extend built-in image rendering to PNG alpha/palette/interlaced variants,
JPEGs with EXIF rotation or mirroring, and TIFF with an explicit multi-page
policy. Current built-in support is limited to 8-bit RGB/grayscale PNG without
interlacing or alpha and JPEGs with no orientation change.
2. Run the pinned LibreOffice 26.2.2.2 Linux x86_64 adapter test in CI against
   a generated DOCX and a separately pinned PDFium library. It checks engine
   identity, one-page output, PDF structure, artifact provenance,
   source/snapshot immutability, temporary-operation cleanup, and that an
   unrelated LibreOffice process remains alive. This does not measure layout
   fidelity or qualify ODT, spreadsheet, or presentation formats.
3. Extend the corpus across DOCX/ODT/spreadsheets/presentations and record
   rendered-output comparisons with bounded variance. Confirm the declared
   format table against the pinned build before advertising those formats.
4. Wire only adapters that pass runtime capability checks. Keep native Microsoft
   Office and WPS unavailable until a supported interactive path is implemented
   and tested; LibreOffice does not establish their support.

The production desktop now has a `PreparedArtifactCoordinator` that binds source snapshots, renderer receipts, page maps, and the final PDF artifact into a persisted `PreparedRun`; preview reads that same artifact. These PDF/JPEG/PNG preparation and preview paths require an explicitly configured PDFium runtime and digest, and PDF source inspection rejects forms and annotations. The desktop still has no production printer submission path or device output. This work does not close G1 or G2.
