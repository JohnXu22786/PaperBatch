using System.Security.Cryptography;
using System.Buffers.Binary;
using System.IO.Compression;
using System.Text.Json;
using PaperBatch.Core.Domain;
using PaperBatch.Core.Layout;
using PaperBatch.Core.Persistence;
using PaperBatch.Core.Preparation;
using PaperBatch.Core.Printing;
using PaperBatch.Core.Intake;
using PaperBatch.Core.Rendering;
using PaperBatch.Desktop.Presentation;

internal static class ApplicationImportEvidenceTests
{
    internal static void ProductionQueueImportReportsFormatOutcomes()
    {
        var root = Path.Combine(Path.GetTempPath(), "paperbatch-app-import-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var cache = new SourceSnapshotCache(new SourceSnapshotCacheOptions { RootPath = Path.Combine(root, "cache") });
            var artifacts = new RenderArtifactStore(Path.Combine(root, "artifacts"));
            var renderer = DesktopComposition.CreateProductionRenderer(cache, artifacts);
            var presentation = new ProductionPresentationSources(new SourceIntakeService(cache), renderer, cache,
                rendererAvailable: true, artifacts: artifacts);
            var enginePath = Environment.GetEnvironmentVariable("PAPERBATCH_LO_ENGINE_PATH");
            var engineHash = Environment.GetEnvironmentVariable("PAPERBATCH_LO_ENGINE_SHA256");
            var conversionConfigured = (OperatingSystem.IsWindows() || OperatingSystem.IsLinux()) &&
                !string.IsNullOrWhiteSpace(enginePath) && engineHash?.Length == 64 && engineHash.All(Uri.IsHexDigit);
            var pngPath = Path.Combine(root, "synthetic_one_pixel.png");
            var rtfPath = Path.Combine(root, "synthetic_text.rtf");
            File.WriteAllText(rtfPath, "{\\rtf1\\ansi Synthetic RTF fixture}", System.Text.Encoding.ASCII);
            File.WriteAllBytes(pngPath, CreateOnePixelPng());
            var odtPath = WriteOdfFixture(root, "synthetic_text.odt", "application/vnd.oasis.opendocument.text", "text");
            var odsPath = WriteOdfFixture(root, "synthetic_table.ods", "application/vnd.oasis.opendocument.spreadsheet", "spreadsheet");
            var odpPath = WriteOdfFixture(root, "synthetic_slide.odp", "application/vnd.oasis.opendocument.presentation", "presentation");
            var fixtures = new[]
            {
                Fixture("synthetic_two_page.pdf", "pdf", "accepted"),
                Fixture(pngPath, "png", "accepted"),
                Fixture("synthetic_sections_table.docx", "docx", conversionConfigured ? "converted" : "unsupported"),
                Fixture("synthetic_mixed_page_sizes.xlsx", "xlsx", conversionConfigured ? "converted" : "unsupported"),
                Fixture("synthetic_two_slides.pptx", "pptx", conversionConfigured ? "converted" : "unsupported"),
                Fixture(rtfPath, "rtf", conversionConfigured ? "converted" : "unsupported"),
                Fixture(odtPath, "odt", conversionConfigured ? "converted" : "unsupported"),
                Fixture(odsPath, "ods", conversionConfigured ? "converted" : "unsupported"),
                Fixture(odpPath, "odp", conversionConfigured ? "converted" : "unsupported"),
                Fixture("synthetic_truncated_package.docx", "docx", "rejected")
            };

            var queueItems = presentation.ImportAsync(fixtures.Select(item => item.Path)).GetAwaiter().GetResult();
            Require(queueItems.Count == fixtures.Length, "Production import did not return one queue row per selected fixture.");

            var outcomes = new List<object>(fixtures.Length);
            for (var index = 0; index < fixtures.Length; index++)
            {
                var fixture = fixtures[index];
                var queueItem = queueItems[index];
                Require(queueItem.Title == fixture.Name, "Production import changed the selected document order or identity.");
                var findings = presentation.GetFindings(queueItem.Id);
                var rendererFinding = findings.FirstOrDefault(item => item.Title == "Renderer");
                string actualOutcome;
                string? rendererStatus = rendererFinding?.Status;
                string? intakeDiagnostic = null;

                if (fixture.ExpectedOutcome is "accepted" or "converted")
                {
                    actualOutcome = fixture.ExpectedOutcome;
                    Require(queueItem.Status.StartsWith("Ready to prepare · ", StringComparison.Ordinal),
                        $"{fixture.Extension} did not become an application queue item ready to prepare.");
                    Require(rendererStatus == RenderStatus.Succeeded.ToString(), $"{fixture.Extension} import did not persist an application render artifact.");
                    Require(rendererFinding!.Detail.Contains("persisted", StringComparison.Ordinal), $"{fixture.Extension} import did not report its persisted artifact result.");
                    if (fixture.ExpectedOutcome == "converted")
                    {
                        var tokens = rendererFinding.Detail.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                        Require(tokens.Length >= 3 && tokens[0] == "Rendered" && tokens[1] == "artifact",
                            $"{fixture.Extension} did not report a persisted converted artifact identity.");
                        var receipt = artifacts.GetReceipt(tokens[2]);
                        Require(receipt.EngineId == "libreoffice", $"{fixture.Extension} artifact did not record LibreOffice.");
                        Require(receipt.EngineVersion == Environment.GetEnvironmentVariable("PAPERBATCH_LO_EXPECTED_VERSION"),
                            $"{fixture.Extension} artifact did not record the actual pinned LibreOffice version.");
                        Require(receipt.Options.TryGetValue("source-format", out var sourceFormat) &&
                            string.Equals(sourceFormat, fixture.Extension, StringComparison.OrdinalIgnoreCase),
                            $"{fixture.Extension} artifact metadata did not preserve source format.");
                        Require(receipt.Options.TryGetValue("engine-sha256", out var recordedHash) && recordedHash == engineHash?.ToLowerInvariant(),
                            $"{fixture.Extension} artifact metadata did not preserve the configured engine digest.");
                        Require(receipt.Options.TryGetValue("macro-policy", out var macroPolicy) && macroPolicy == "disabled" &&
                            receipt.Options.TryGetValue("link-refresh-policy", out var linkPolicy) && linkPolicy == "disabled",
                            $"{fixture.Extension} artifact did not record macro and external-link protections.");
                    }
                }
                else if (fixture.ExpectedOutcome == "unsupported")
                {
                    actualOutcome = "unsupported";
                    Require(queueItem.Status == "Unsupported format", $"{fixture.Extension} did not report its unsupported production renderer outcome.");
                    Require(rendererStatus == RenderStatus.Unsupported.ToString(), $"{fixture.Extension} did not return the production renderer's Unsupported outcome.");
                }
                else
                {
                    actualOutcome = "rejected";
                    Require(queueItem.Status == "Needs attention", "Malformed OOXML did not remain out of the ready queue.");
                    intakeDiagnostic = findings.FirstOrDefault(item => item.Status == "Error")?.Title;
                    Require(intakeDiagnostic == IntakeDiagnosticCode.MalformedContainer.ToString(),
                        "Malformed OOXML did not report the expected intake rejection.");
                    Require(rendererFinding is null, "Malformed input reached the production renderer.");
                }

                outcomes.Add(new
                {
                    fixture = fixture.Name,
                    format = fixture.Extension,
                    sha256 = fixture.Sha256,
                    applicationOutcome = actualOutcome,
                    queueStatus = queueItem.Status,
                    rendererStatus,
                    intakeDiagnostic
                });
            }

            var mixedBatch = OperatingSystem.IsWindows() && conversionConfigured &&
                !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("PAPERBATCH_PDFIUM_RUNTIME_PATH"))
                ? PrepareAndFakePrintMixedBatch(fixtures.Where(item => item.ExpectedOutcome is "accepted" or "converted").ToArray(), cache, artifacts, renderer)
                : null;
            WriteReportIfRequested(outcomes, Environment.GetEnvironmentVariable("PAPERBATCH_LO_EXPECTED_VERSION"), engineHash, mixedBatch);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        static (string Name, string Extension, string ExpectedOutcome, string Path, string Sha256) Fixture(
            string name, string extension, string expectedOutcome)
        {
            var path = Path.IsPathRooted(name) ? name : Path.Combine(AppContext.BaseDirectory, "Fixtures", name);
            var bytes = File.ReadAllBytes(path);
            return (Path.GetFileName(name), extension, expectedOutcome, path, Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());
        }
    }

    private static object PrepareAndFakePrintMixedBatch(
        (string Name, string Extension, string ExpectedOutcome, string Path, string Sha256)[] fixtures,
        SourceSnapshotCache cache, RenderArtifactStore artifacts, RendererCoordinator renderer)
    {
        var pdfiumPath = Environment.GetEnvironmentVariable("PAPERBATCH_PDFIUM_RUNTIME_PATH")!;
        var pdfiumHash = Environment.GetEnvironmentVariable("PAPERBATCH_PDFIUM_RUNTIME_SHA256")!;
        var pdfium = new PdfiumArtifactInspector(pdfiumPath, pdfiumHash);
        pdfium.ValidateConfiguredRuntime();
        var root = Path.Combine(Path.GetTempPath(), "paperbatch-lo-mixed-batch-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var intake = new SourceIntakeService(cache);
            var runs = new SqliteRunStore(Path.Combine(root, "runs.db"));
            runs.Initialize();
            var composer = new PdfiumPageCompositionAdapter(pdfiumPath, pdfiumHash);
            var preparation = new PreparedArtifactCoordinator(intake, cache, renderer, pdfium, pdfium, composer, artifacts, runs);
            var sources = new ProductionPresentationSources(intake, renderer, cache, rendererAvailable: true,
                artifacts: artifacts, previewRenderer: pdfium, runStore: runs, preparationAvailable: true,
                coordinatorTransport: new PreparedArtifactTestTransport(preparation));
            var imported = sources.ImportAsync(fixtures.Select(item => item.Path)).GetAwaiter().GetResult();
            Require(imported.Select(item => item.Title).SequenceEqual(fixtures.Select(item => item.Name)),
                "Mixed-batch production import did not preserve source order.");
            var prepared = sources.PrepareQueueAsync(
                new PrintSettings(1, ColorMode.Color, DuplexMode.Simplex, "Letter", Orientation.Auto, ScalingMode.ShrinkToFit),
                new LayoutOptions()).GetAwaiter().GetResult();
            Require(prepared.Succeeded, "Production mixed-batch preparation failed: " + string.Join("; ", prepared.Diagnostics));
            var run = prepared.Run!;
            Require(run.Items.Length == fixtures.Length, "Prepared batch omitted a supported source.");
            Require(run.Items.Select(item => item.Source.Sha256).SequenceEqual(fixtures.Select(item => item.Sha256)),
                "Prepared batch changed source identity or order.");
            Require(run.FinalArtifact is not null, "Prepared batch did not persist its final PDF.");

            var backend = new CapturingPrintBackend();
            var print = new WindowsPrintCoordinator(runs, backend, () => "fake-libreo-batch-run", artifacts);
            var first = print.SubmitRunAsync(run, "libreoffice-mixed-batch-submit", DateTimeOffset.UtcNow).GetAwaiter().GetResult();
            Require(first.Count == 1 && first[0].Status == PrintSubmissionStatus.Accepted && backend.SubmissionCount == 1,
                "Prepared batch was not submitted once through the injected fake print backend.");
            var replay = print.SubmitRunAsync(run, "libreoffice-mixed-batch-submit", DateTimeOffset.UtcNow.AddMinutes(1)).GetAwaiter().GetResult();
            Require(replay.Count == 0 && backend.SubmissionCount == 1, "A repeated batch submission reached the print backend twice.");
            return new
            {
                sourceCount = run.Items.Length,
                sourceOrder = run.Items.Select(item => item.Source.Sha256).ToArray(),
                preparedArtifact = new { sha256 = run.FinalArtifact!.Sha256, lengthBytes = run.FinalArtifact.LengthBytes, pageCount = run.FinalArtifact.PageCount },
                segmentCount = run.Segments.Length,
                jobCount = prepared.Persistence!.Jobs.Length,
                fakeBackendSubmissions = backend.SubmissionCount,
                replaySubmissions = replay.Count,
                physicalOutput = "not produced; fake backend only"
            };
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static string WriteOdfFixture(string root, string name, string mimeType, string family)
    {
        var path = Path.Combine(root, name);
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        var mime = archive.CreateEntry("mimetype", CompressionLevel.NoCompression);
        using (var writer = new StreamWriter(mime.Open(), System.Text.Encoding.ASCII)) writer.Write(mimeType);
        var manifest = archive.CreateEntry("META-INF/manifest.xml");
        using (var writer = new StreamWriter(manifest.Open(), System.Text.Encoding.UTF8))
            writer.Write($"<manifest:manifest xmlns:manifest=\"urn:oasis:names:tc:opendocument:xmlns:manifest:1.0\"><manifest:file-entry manifest:full-path=\"/\" manifest:media-type=\"{mimeType}\"/></manifest:manifest>");
        var content = archive.CreateEntry("content.xml");
        using (var writer = new StreamWriter(content.Open(), System.Text.Encoding.UTF8))
        {
            var body = family switch
            {
                "text" => "<office:body><office:text><text:p>Synthetic ODT fixture</text:p></office:text></office:body>",
                "spreadsheet" => "<office:body><office:spreadsheet><table:table table:name=\"Sheet1\"><table:table-row><table:table-cell office:value-type=\"string\"><text:p>Synthetic ODS fixture</text:p></table:table-cell></table:table-row></table:table></office:spreadsheet></office:body>",
                _ => "<office:body><office:presentation><draw:page draw:name=\"Slide1\" draw:style-name=\"dp1\"><draw:frame draw:style-name=\"gr1\" draw:text-style-name=\"P1\" svg:width=\"8cm\" svg:height=\"2cm\" svg:x=\"1cm\" svg:y=\"1cm\"><draw:text-box><text:p>Synthetic ODP fixture</text:p></draw:text-box></draw:frame></draw:page></office:presentation></office:body>"
            };
            writer.Write($"<office:document-content xmlns:office=\"urn:oasis:names:tc:opendocument:xmlns:office:1.0\" xmlns:text=\"urn:oasis:names:tc:opendocument:xmlns:text:1.0\" xmlns:table=\"urn:oasis:names:tc:opendocument:xmlns:table:1.0\" xmlns:draw=\"urn:oasis:names:tc:opendocument:xmlns:drawing:1.0\" xmlns:svg=\"urn:oasis:names:tc:opendocument:xmlns:svg-compatible:1.0\">{body}</office:document-content>");
        }
        return path;
    }

    private static byte[] CreateOnePixelPng()
    {
        using var output = new MemoryStream();
        output.Write([137, 80, 78, 71, 13, 10, 26, 10]);
        WriteChunk("IHDR", [0, 0, 0, 1, 0, 0, 0, 1, 8, 2, 0, 0, 0]);
        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
            zlib.Write([0, 24, 95, 112]);
        WriteChunk("IDAT", compressed.ToArray());
        WriteChunk("IEND", []);
        return output.ToArray();

        void WriteChunk(string type, byte[] payload)
        {
            Span<byte> length = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(length, checked((uint)payload.Length));
            output.Write(length);
            var typeBytes = System.Text.Encoding.ASCII.GetBytes(type);
            output.Write(typeBytes);
            output.Write(payload);
            var crcInput = new byte[typeBytes.Length + payload.Length];
            typeBytes.CopyTo(crcInput, 0);
            payload.CopyTo(crcInput, typeBytes.Length);
            Span<byte> crc = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(crc, Crc32(crcInput));
            output.Write(crc);
        }
    }

    private static uint Crc32(ReadOnlySpan<byte> bytes)
    {
        var crc = 0xffffffffu;
        foreach (var value in bytes)
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++) crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xedb88320u : crc >> 1;
        }
        return ~crc;
    }

    private static void WriteReportIfRequested(IReadOnlyList<object> outcomes, string? libreOfficeVersion,
        string? libreOfficeEngineSha256, object? mixedBatchPreparation)
    {
        var reportPath = Environment.GetEnvironmentVariable("PAPERBATCH_APPLICATION_IMPORT_REPORT");
        if (string.IsNullOrWhiteSpace(reportPath)) return;
        var report = new
        {
            schemaVersion = 1,
            evidenceType = "paperbatch-production-queue-import",
            sourceCommit = Environment.GetEnvironmentVariable("PB_SOURCE_COMMIT"),
            workflow = Environment.GetEnvironmentVariable("GITHUB_WORKFLOW"),
            runId = Environment.GetEnvironmentVariable("GITHUB_RUN_ID"),
            runAttempt = Environment.GetEnvironmentVariable("GITHUB_RUN_ATTEMPT"),
            runnerOs = Environment.GetEnvironmentVariable("RUNNER_OS"),
            runnerImage = Environment.GetEnvironmentVariable("ImageOS") ?? Environment.GetEnvironmentVariable("ImageVersion"),
            libreOffice = string.IsNullOrWhiteSpace(libreOfficeVersion) ? null : new { version = libreOfficeVersion, engineSha256 = libreOfficeEngineSha256 },
            outcomes,
            mixedBatchPreparation,
            evidenceLimits = new[]
            {
                "This runs PaperBatch's production queue import path and registered production renderers with synthetic fixtures.",
                "PDF and PNG pass-through/raster rendering are accepted. Configured LibreOffice conversion records the actual engine version and executable SHA-256; conversion is not Microsoft Office/WPS equivalence or visual-fidelity evidence.",
                "The private conversion profile disables macros and external-link refresh. PDFium remains separately required for page inspection, composition, and batch preparation.",
                "Malformed OOXML is rejected before rendering. macOS conversion remains unsupported.",
                "Windows mixed-batch preparation and submission use an injected fake print backend that creates no OS print job.",
                "No Office/WPS compatibility, visual fidelity, desktop printing, or physical output is established."
            }
        };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(report, new JsonSerializerOptions { WriteIndented = true });
        if (bytes.Length > 16 * 1024) throw new InvalidOperationException("The application import evidence report exceeds its size limit.");
        var fullPath = Path.GetFullPath(reportPath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllBytes(fullPath, bytes);
    }

    private sealed class CapturingPrintBackend : IWindowsPrintBackend
    {
        public int SubmissionCount { get; private set; }

        public Task<PrinterCapabilitySnapshot> GetCapabilitiesAsync(string queueIdentity, CancellationToken cancellationToken = default)
        {
            var options = new Dictionary<PrintOption, PrintOptionCapability>
            {
                [PrintOption.Media] = new(CapabilityState.Supported, ["Letter"], null),
                [PrintOption.Color] = new(CapabilityState.Supported, ["Color"], null),
                [PrintOption.Duplex] = new(CapabilityState.Supported, [nameof(DuplexMode.Simplex)], null),
                [PrintOption.Copies] = new(CapabilityState.Supported, ["1..1"], null)
            };
            return Task.FromResult(new PrinterCapabilitySnapshot(queueIdentity, "Synthetic queue", "Fake test backend",
                "none", "fake-capability-fingerprint", options));
        }

        public async Task<OsJobReceipt> SubmitAsync(WindowsPrintSubmission submission, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var bytes = new MemoryStream();
            await submission.PdfContent.CopyToAsync(bytes, cancellationToken).ConfigureAwait(false);
            Require(bytes.Length > 0, "Fake print backend received an empty PDF artifact.");
            SubmissionCount++;
            return new OsJobReceipt(submission.Options.QueueIdentity, "fake-os-job-" + SubmissionCount,
                submission.RunNonce, submission.SubmittedAtUtc, "Fake test backend", "none", "fake-capability-fingerprint");
        }

        public Task<PrintBackendJobStatus> GetStatusAsync(OsJobReceipt receipt, CancellationToken cancellationToken = default) =>
            Task.FromResult(PrintBackendJobStatus.Accepted);

        public Task<bool> CancelAsync(OsJobReceipt receipt, CancellationToken cancellationToken = default) => Task.FromResult(false);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
