# Renderer pipeline boundary

This follow-up adds a private persisted PDF artifact store, an exact-byte PDF
pass-through adapter, a built-in one-page JPEG/PNG-to-PDF adapter, and an
isolated LibreOffice command-line adapter. Production composition registers the
PDF and built-in image adapters for background rendering. LibreOffice remains
unregistered and unavailable until stable pinned-build CI validates it. PDF pass-through validates the cached
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

The following work remains before a source can produce a `PreparedRun` or a final
preview:

1. Extend built-in image rendering to PNG alpha/palette/interlaced variants,
JPEGs with EXIF rotation or mirroring, and TIFF with an explicit multi-page
policy. Current built-in support is limited to 8-bit RGB/grayscale PNG without
interlacing or alpha and JPEGs with no orientation change.
2. Pin the Linux LibreOffice package/build in CI and exercise the adapter with
sanitized synthetic documents. The adapter requires an explicit executable
digest and a PDFium runtime; it reports unavailable on Windows in this unit.
3. Exercise LibreOffice export with sanitized synthetic DOCX/ODT fixtures and
record semantic, page-count, and rendered-output comparisons with bounded
variance. Confirm the declared format table against the pinned build.
4. Add the preparation coordinator that assembles source snapshots, renderer
receipts, page maps, and the PDF artifact into a persisted `PreparedRun`, then
serves preview bytes from that exact artifact ID.
5. Wire only adapters that pass runtime capability checks. Keep native Microsoft
Office and WPS unavailable until a supported interactive path is implemented
and tested; LibreOffice does not establish their support.

The desktop can persist PDF pass-through and supported JPEG/PNG render artifacts,
but there is no preparation coordinator to bind those artifacts to a `PreparedRun`
or serve final preview bytes. It reports prepared-run/final-preview/submission as
unavailable. This work does not close G1 or G2 and does not implement printing or
device output.
