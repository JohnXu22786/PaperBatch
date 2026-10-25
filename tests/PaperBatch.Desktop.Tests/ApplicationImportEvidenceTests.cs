using System.Security.Cryptography;
using System.Buffers.Binary;
using System.IO.Compression;
using System.Text.Json;
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
            var pngPath = Path.Combine(root, "synthetic_one_pixel.png");
            File.WriteAllBytes(pngPath, CreateOnePixelPng());
            var fixtures = new[]
            {
                Fixture("synthetic_two_page.pdf", "pdf", "accepted"),
                Fixture(pngPath, "png", "accepted"),
                Fixture("synthetic_sections_table.docx", "docx", "unsupported"),
                Fixture("synthetic_mixed_page_sizes.xlsx", "xlsx", "unsupported"),
                Fixture("synthetic_two_slides.pptx", "pptx", "unsupported"),
                Fixture("synthetic_truncated_package.docx", "docx", "rejected")
            };

            var queueItems = presentation.ImportAsync(fixtures.Select(item => item.Path)).GetAwaiter().GetResult();
            Require(queueItems.Count == fixtures.Length, "Production import did not return one queue row per selected fixture.");

            var outcomes = new List<object>(fixtures.Length);
            for (var index = 0; index < fixtures.Length; index++)
            {
                var fixture = fixtures[index];
                var queueItem = queueItems[index];
                var findings = presentation.GetFindings(queueItem.Id);
                var rendererFinding = findings.FirstOrDefault(item => item.Title == "Renderer");
                string actualOutcome;
                string? rendererStatus = rendererFinding?.Status;
                string? intakeDiagnostic = null;

                if (fixture.ExpectedOutcome == "accepted")
                {
                    actualOutcome = "accepted";
                    Require(queueItem.Status.StartsWith("Ready to prepare · ", StringComparison.Ordinal),
                        $"{fixture.Extension} did not become an application queue item ready to prepare.");
                    Require(rendererStatus == RenderStatus.Succeeded.ToString(), $"{fixture.Extension} import did not persist an application render artifact.");
                    Require(rendererFinding!.Detail.Contains("persisted", StringComparison.Ordinal), $"{fixture.Extension} import did not report its persisted artifact result.");
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

            WriteReportIfRequested(outcomes);
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

    private static void WriteReportIfRequested(IReadOnlyList<object> outcomes)
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
            outcomes,
            evidenceLimits = new[]
            {
                "This runs PaperBatch's production queue import path and registered production renderers with synthetic fixtures.",
                "PDF intake and pass-through artifact persistence are accepted; DOCX, XLSX, and PPTX pass intake checks but have no production renderer and are reported unsupported.",
                "Malformed OOXML is rejected before rendering. LibreOffice is not invoked by this application import run; standalone LibreOffice conversion does not establish PaperBatch import support.",
                "No Office/WPS compatibility, visual fidelity, desktop printing, or physical output is established."
            }
        };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(report, new JsonSerializerOptions { WriteIndented = true });
        if (bytes.Length > 16 * 1024) throw new InvalidOperationException("The application import evidence report exceeds its size limit.");
        var fullPath = Path.GetFullPath(reportPath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllBytes(fullPath, bytes);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
