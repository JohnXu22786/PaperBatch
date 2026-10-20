using PaperBatch.Core.Intake;
using PaperBatch.Core.Rendering;
using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.AccessControl;
using System.Runtime.Versioning;
using System.Text;
using System.Diagnostics;
using System.Security.Cryptography;

internal static class RendererTests
{
    [SupportedOSPlatform("linux")]
    public static void LibreOfficeVersionQueryStopsOnTimeout()
    {
        var root = Path.Combine(Path.GetTempPath(), $"paperbatch-lo-version-timeout-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var fixture = Path.Combine(root, "hanging-version-engine");
        File.WriteAllText(fixture, "#!/bin/sh\nexec /bin/sleep 30\n");
        File.SetUnixFileMode(fixture, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        try
        {
            Assert.Throws<TimeoutException>(() => ReadLibreOfficeVersion(fixture, TimeSpan.FromMilliseconds(250)));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [SupportedOSPlatform("linux")]
    public static void PinnedLibreOfficeRendersSyntheticDocx()
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("The pinned LibreOffice integration runs only on Linux CI.");
        const string pinnedVersion = "LibreOffice 26.2.2.2";
        const string pinnedEngineSha256 = "bcfd617b90f741cd9aa778e70d6aa317ec6c8971e238f602f3f69ea0ad210c0a";
        var enginePath = Environment.GetEnvironmentVariable("PAPERBATCH_LO_ENGINE_PATH");
        var launcherPath = Environment.GetEnvironmentVariable("PAPERBATCH_LO_LAUNCHER_PATH");
        var expectedVersion = Environment.GetEnvironmentVariable("PAPERBATCH_LO_EXPECTED_VERSION");
        var expectedHash = Environment.GetEnvironmentVariable("PAPERBATCH_LO_ENGINE_SHA256");
        var pdfiumPath = Environment.GetEnvironmentVariable("PAPERBATCH_PDFIUM_TEST_PATH");
        var pdfiumHash = Environment.GetEnvironmentVariable("PAPERBATCH_PDFIUM_TEST_SHA256");
        if (string.IsNullOrWhiteSpace(enginePath) || string.IsNullOrWhiteSpace(launcherPath) || string.IsNullOrWhiteSpace(expectedVersion) ||
            string.IsNullOrWhiteSpace(expectedHash) || string.IsNullOrWhiteSpace(pdfiumPath) || string.IsNullOrWhiteSpace(pdfiumHash))
            throw new InvalidOperationException("The pinned LibreOffice CI runtime configuration is incomplete.");
        Assert.Equal(pinnedVersion, expectedVersion);
        Assert.Equal(pinnedEngineSha256, expectedHash);
        Assert.True(ReadLibreOfficeVersion(enginePath).StartsWith(pinnedVersion, StringComparison.Ordinal),
            "The verified LibreOffice executable did not report the pinned version.");

        var root = Path.Combine(Path.GetTempPath(), $"paperbatch-lo-integration-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var sourcePath = Path.Combine(root, "synthetic.docx");
        var originalBytes = CreateSyntheticDocx();
        File.WriteAllBytes(sourcePath, originalBytes);
        Process? unrelatedProcess = null;
        try
        {
            var snapshots = new SourceSnapshotCache(new SourceSnapshotCacheOptions { RootPath = Path.Combine(root, "cache") });
            var snapshot = snapshots.CaptureAsync(sourcePath).GetAwaiter().GetResult();
            var artifacts = new RenderArtifactStore(Path.Combine(root, "artifacts"));
            var inspector = new PdfiumArtifactInspector(pdfiumPath, pdfiumHash);
            var adapter = new LibreOfficeRendererAdapter(snapshots, artifacts, inspector, enginePath, expectedHash);
            var request = new RenderRequest(snapshot.SnapshotId, SourceFormat.Docx, RendererExecutionContext.BackgroundWorker);

            var profile = Path.Combine(root, "unrelated-profile");
            Directory.CreateDirectory(profile, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var profileUri = new Uri(profile + Path.DirectorySeparatorChar).AbsoluteUri;
            var programDirectory = Path.GetDirectoryName(Path.GetFullPath(enginePath))!;
            var unrelatedStart = new ProcessStartInfo(launcherPath)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WorkingDirectory = programDirectory
            };
            unrelatedStart.Environment.Remove("LD_LIBRARY_PATH");
            unrelatedStart.Environment.Remove("URE_BOOTSTRAP");
            unrelatedStart.ArgumentList.Add("--headless");
            unrelatedStart.ArgumentList.Add("--nologo");
            unrelatedStart.ArgumentList.Add("--nodefault");
            unrelatedStart.ArgumentList.Add("--nofirststartwizard");
            unrelatedStart.ArgumentList.Add("-env:UserInstallation=" + profileUri);
            unrelatedStart.ArgumentList.Add("--accept=pipe,name=paperbatch-unrelated-" + Guid.NewGuid().ToString("N") + ";urp;StarOffice.ServiceManager");
            unrelatedProcess = Process.Start(unrelatedStart) ?? throw new InvalidOperationException("Could not start the unrelated LibreOffice fixture process.");
            var unrelatedStdout = unrelatedProcess.StandardOutput.ReadToEndAsync();
            var unrelatedStderr = unrelatedProcess.StandardError.ReadToEndAsync();
            Thread.Sleep(TimeSpan.FromMilliseconds(500));
            Assert.False(unrelatedProcess.HasExited, DescribeExitedProcess(unrelatedProcess, unrelatedStdout, unrelatedStderr));

            var existingOperations = Directory.GetDirectories(Path.GetTempPath(), "paperbatch-lo-*", SearchOption.TopDirectoryOnly)
                .ToHashSet(StringComparer.Ordinal);
            Assert.True(adapter.CanRenderAsync(request, CancellationToken.None).AsTask().GetAwaiter().GetResult(),
                "The pinned adapter did not advertise the synthetic DOCX capability.");
            var result = adapter.RenderAsync(request, CancellationToken.None).GetAwaiter().GetResult();
            Assert.True(result.Status == RenderStatus.Succeeded,
                $"The pinned adapter render returned {result.Status}: {result.Error ?? "no diagnostic"}.");
            Assert.Equal(1, result.Artifact?.PageCount);
            Assert.True(Directory.GetDirectories(Path.GetTempPath(), "paperbatch-lo-*", SearchOption.TopDirectoryOnly)
                .All(existingOperations.Contains));
            Assert.False(unrelatedProcess.HasExited, DescribeExitedProcess(unrelatedProcess, unrelatedStdout, unrelatedStderr));
            Assert.True(originalBytes.SequenceEqual(File.ReadAllBytes(sourcePath)));
            using (var captured = snapshots.OpenRead(snapshot.SnapshotId))
                Assert.Equal(snapshot.Sha256, Convert.ToHexString(SHA256.HashData(captured)).ToLowerInvariant());

            var receipt = artifacts.GetReceipt(result.Artifact!.ArtifactId);
            Assert.Equal("libreoffice", receipt.EngineId);
            Assert.True(receipt.EngineVersion.StartsWith(pinnedVersion, StringComparison.Ordinal));
            Assert.Equal(expectedHash, receipt.Options["engine-sha256"]);
            Assert.Equal("disabled", receipt.Options["macro-policy"]);
            Assert.Equal("disabled", receipt.Options["link-refresh-policy"]);
            Assert.Equal("private-temporary", receipt.Options["profile"]);
            using var exactArtifact = artifacts.OpenRead(result.Artifact.ArtifactId);
            Assert.Equal(SourcePreflightStatus.Ready, SourceFormatPreflight.Inspect(exactArtifact, ".pdf").Status);
        }
        finally
        {
            if (unrelatedProcess is not null)
            {
                try
                {
                    if (!unrelatedProcess.HasExited) unrelatedProcess.Kill(entireProcessTree: true);
                    if (!unrelatedProcess.WaitForExit(TimeSpan.FromSeconds(10)))
                        throw new TimeoutException("The unrelated LibreOffice fixture process did not stop during owned test cleanup.");
                }
                finally { unrelatedProcess.Dispose(); }
            }
            Directory.Delete(root, recursive: true);
        }
    }

    private static string DescribeExitedProcess(Process process, Task<string> stdout, Task<string> stderr)
    {
        if (!process.HasExited) return "The unrelated LibreOffice process is still running.";
        if (!process.WaitForExit(10_000)) return "The unrelated LibreOffice process exited but its output streams did not close.";
        return $"The unrelated LibreOffice process exited with code {process.ExitCode}. " +
            $"stdout: {stdout.GetAwaiter().GetResult()} stderr: {stderr.GetAwaiter().GetResult()}";
    }

    private static string ReadLibreOfficeVersion(string enginePath, TimeSpan? timeout = null)
    {
        var programDirectory = Path.GetDirectoryName(Path.GetFullPath(enginePath))!;
        var start = new ProcessStartInfo(enginePath)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = false,
            CreateNoWindow = true,
            WorkingDirectory = programDirectory
        };
        start.Environment["LD_LIBRARY_PATH"] = programDirectory;
        start.Environment["URE_BOOTSTRAP"] = new Uri(Path.Combine(programDirectory, "fundamentalrc")).AbsoluteUri;
        start.ArgumentList.Add("--version");
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not query the pinned LibreOffice version.");
        var outputTask = process.StandardOutput.ReadToEndAsync();
        if (!process.WaitForExit(timeout ?? TimeSpan.FromSeconds(15)))
        {
            try
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException) when (process.HasExited) { }
            if (!process.WaitForExit(TimeSpan.FromSeconds(10)))
                throw new TimeoutException("The owned LibreOffice version-query process did not stop after timeout.");
            _ = outputTask.GetAwaiter().GetResult();
            throw new TimeoutException("The pinned LibreOffice version query timed out.");
        }
        var output = outputTask.GetAwaiter().GetResult();
        Assert.Equal(0, process.ExitCode);
        return output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? string.Empty;
    }

    private static byte[] CreateSyntheticDocx()
    {
        using var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            AddDocxEntry(archive, "[Content_Types].xml", """
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                <Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">
                  <Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml" />
                  <Default Extension="xml" ContentType="application/xml" />
                  <Override PartName="/word/document.xml" ContentType="application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml" />
                </Types>
                """);
            AddDocxEntry(archive, "_rels/.rels", """
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
                  <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="word/document.xml" />
                </Relationships>
                """);
            AddDocxEntry(archive, "word/document.xml", """
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                <w:document xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main">
                  <w:body>
                    <w:p><w:r><w:t>PaperBatch synthetic LibreOffice fixture.</w:t></w:r></w:p>
                    <w:sectPr><w:pgSz w:w="12240" w:h="15840"/><w:pgMar w:top="1440" w:right="1440" w:bottom="1440" w:left="1440"/></w:sectPr>
                  </w:body>
                </w:document>
                """);
        }
        return output.ToArray();
    }

    private static void AddDocxEntry(ZipArchive archive, string path, string content)
    {
        var entry = archive.CreateEntry(path, CompressionLevel.Optimal);
        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        writer.Write(content);
    }

    public static void ArtifactStorePersistsStablePdfAndReceipt()
    {
        var root = Path.Combine(Path.GetTempPath(), $"paperbatch-artifacts-{Guid.NewGuid():N}");
        var bytes = OnePagePdf();
        try
        {
            var store = new RenderArtifactStore(root);
            var concurrent = Task.WhenAll(Enumerable.Range(0, 8).Select(_ => store.PersistPdfAsync(new MemoryStream(bytes), "test-engine", "4.2",
                new Dictionary<string, string> { ["profile"] = "private", ["macros"] = "disabled" }))).GetAwaiter().GetResult();
            var first = concurrent[0];

            Assert.True(concurrent.All(item => item.Artifact.ArtifactId == first.Artifact.ArtifactId));
            Assert.Equal(64, first.Artifact.Sha256.Length);
            Assert.Equal("test-engine", store.GetReceipt(first.Artifact.ArtifactId).EngineId);
            if (!OperatingSystem.IsWindows())
            {
                const UnixFileMode shared = UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute |
                    UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;
                Assert.Equal(0, (int)(File.GetUnixFileMode(root) & shared));
                Assert.Equal(0, (int)(File.GetUnixFileMode(Path.Combine(root, first.Artifact.ArtifactId, "content.pdf")) & shared));
            }
            using var readback = store.OpenRead(first.Artifact.ArtifactId);
            using var copy = new MemoryStream();
            readback.CopyTo(copy);
            Assert.True(bytes.SequenceEqual(copy.ToArray()));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    public static void ArtifactStoreRejectsChangedPdfOnReadback()
    {
        var root = Path.Combine(Path.GetTempPath(), $"paperbatch-artifacts-{Guid.NewGuid():N}");
        try
        {
            var store = new RenderArtifactStore(root);
            var stored = store.PersistPdfAsync(new MemoryStream(OnePagePdf()), "test-engine", "1",
                new Dictionary<string, string>()).GetAwaiter().GetResult();
            File.WriteAllText(Path.Combine(root, stored.Artifact.ArtifactId, "content.pdf"), "%PDF-1.7\nchanged");
            Assert.Throws<InvalidDataException>(() => store.OpenRead(stored.Artifact.ArtifactId));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    public static void ArtifactStoreRejectsMalformedReceiptAsInvalidData()
    {
        var root = Path.Combine(Path.GetTempPath(), $"paperbatch-artifacts-{Guid.NewGuid():N}");
        try
        {
            var store = new RenderArtifactStore(root);
            var stored = store.PersistPdfAsync(new MemoryStream(OnePagePdf()), "test-engine", "1",
                new Dictionary<string, string>()).GetAwaiter().GetResult();
            var id = stored.Artifact.ArtifactId;
            File.WriteAllText(Path.Combine(root, id, "receipt.json"),
                System.Text.Json.JsonSerializer.Serialize(new
                {
                    Artifact = new { ArtifactId = id, Sha256 = (string?)null, LengthBytes = 20 },
                    EngineId = "test-engine",
                    EngineVersion = "1",
                    Options = new Dictionary<string, string>()
                }));

            Assert.Throws<InvalidDataException>(() => store.GetReceipt(id));
            Assert.Throws<InvalidDataException>(() => store.OpenRead(id));

            var mismatchedDigest = (stored.Artifact.Sha256[0] == '0' ? '1' : '0') + stored.Artifact.Sha256[1..];
            File.WriteAllText(Path.Combine(root, id, "receipt.json"),
                System.Text.Json.JsonSerializer.Serialize(new
                {
                    Artifact = new { ArtifactId = id, Sha256 = mismatchedDigest, LengthBytes = stored.Artifact.LengthBytes },
                    EngineId = "test-engine",
                    EngineVersion = "1",
                    Options = new Dictionary<string, string>()
                }));
            Assert.Throws<InvalidDataException>(() => store.GetReceipt(id));
            Assert.Throws<InvalidDataException>(() => store.OpenRead(id));

            File.WriteAllText(Path.Combine(root, id, "receipt.json"), "{");
            Assert.Throws<InvalidDataException>(() => store.GetReceipt(id));
            Assert.Throws<InvalidDataException>(() => store.OpenRead(id));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    public static void ArtifactStoreProtectsItsWindowsDirectoryAcl()
    {
        if (!OperatingSystem.IsWindows()) return;
        var root = Path.Combine(Path.GetTempPath(), $"paperbatch-artifacts-{Guid.NewGuid():N}");
        try
        {
            _ = new RenderArtifactStore(root);
            AssertOwnerOnlyAcl(root);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    public static void LibreOfficeProfileProtectsItsWindowsDirectoryAcl()
    {
        if (!OperatingSystem.IsWindows()) return;
        var operation = Path.Combine(Path.GetTempPath(), $"paperbatch-renderer-{Guid.NewGuid():N}");
        Directory.CreateDirectory(operation);
        try
        {
            var profile = LibreOfficeProfilePolicy.CreatePrivateProfile(operation);
            AssertOwnerOnlyAcl(operation);
            AssertOwnerOnlyAcl(profile.LocalPath.TrimEnd(Path.DirectorySeparatorChar));
        }
        finally
        {
            if (Directory.Exists(operation)) Directory.Delete(operation, recursive: true);
        }
    }

    [SupportedOSPlatform("windows")]
    private static void AssertOwnerOnlyAcl(string directory)
    {
        var security = new DirectoryInfo(directory).GetAccessControl(AccessControlSections.Access);
        Assert.True(security.AreAccessRulesProtected);
        var rules = security.GetAccessRules(includeExplicit: true, includeInherited: true,
            typeof(System.Security.Principal.SecurityIdentifier));
        if (rules.Count != 1) throw new InvalidOperationException($"Expected one explicit owner ACL rule, got {rules.Count}.");
        var onlyRule = rules[0] ?? throw new InvalidOperationException("The explicit owner ACL rule is missing.");
        using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
        var userSid = identity.User ?? throw new InvalidOperationException("The current Windows user SID could not be resolved.");
        Assert.Equal(userSid.Value, ((System.Security.Principal.SecurityIdentifier)onlyRule.IdentityReference).Value);
    }

    public static void ArtifactStoreRejectsNonPdfInput()
    {
        var root = Path.Combine(Path.GetTempPath(), $"paperbatch-artifacts-{Guid.NewGuid():N}");
        try
        {
            var store = new RenderArtifactStore(root);
            Assert.Throws<InvalidDataException>(() => store.PersistPdfAsync(new MemoryStream("not a PDF"u8.ToArray()),
                "test-engine", "1", new Dictionary<string, string>()).GetAwaiter().GetResult());
            Assert.Throws<InvalidDataException>(() => store.PersistPdfAsync(new MemoryStream("%PDF-1.7\nthis has no PDF trailer or cross-reference"u8.ToArray()),
                "test-engine", "1", new Dictionary<string, string>()).GetAwaiter().GetResult());
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    public static void PdfPassThroughPersistsExactSnapshotBytesWithoutClaimingPageCount()
    {
        var root = Path.Combine(Path.GetTempPath(), $"paperbatch-pdf-pass-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var sourcePath = Path.Combine(root, "source.pdf");
        var cacheRoot = Path.Combine(root, "cache");
        var artifactRoot = Path.Combine(root, "artifacts");
        var bytes = OnePagePdf();
        File.WriteAllBytes(sourcePath, bytes);
        try
        {
            var cache = new SourceSnapshotCache(new SourceSnapshotCacheOptions { RootPath = cacheRoot });
            var snapshot = cache.CaptureAsync(sourcePath).GetAwaiter().GetResult();
            var store = new RenderArtifactStore(artifactRoot);
            var adapter = new PdfPassThroughRendererAdapter(new PdfPassThroughArtifactService(cache, store));
            Assert.True(adapter.CanRenderAsync(new RenderRequest(snapshot.SnapshotId, SourceFormat.Pdf,
                RendererExecutionContext.BackgroundWorker), CancellationToken.None).GetAwaiter().GetResult());
            Assert.False(adapter.CanRenderAsync(new RenderRequest(snapshot.SnapshotId, SourceFormat.Pdf,
                RendererExecutionContext.InteractiveDesktop), CancellationToken.None).GetAwaiter().GetResult());
            var result = adapter.RenderAsync(new RenderRequest(snapshot.SnapshotId, SourceFormat.Pdf,
                RendererExecutionContext.BackgroundWorker), CancellationToken.None).GetAwaiter().GetResult();

            Assert.Equal(RenderStatus.Succeeded, result.Status);
            Assert.Equal(0, result.Artifact?.PageCount);
            var persisted = store.GetReceipt(result.Artifact!.ArtifactId);
            Assert.Equal(snapshot.Sha256, persisted.Artifact.Sha256);
            Assert.Equal(64, persisted.Artifact.Sha256.Length);
            Assert.True(bytes.SequenceEqual(File.ReadAllBytes(sourcePath)));
            using var readback = store.OpenRead(persisted.Artifact.ArtifactId);
            using var output = new MemoryStream();
            readback.CopyTo(output);
            Assert.True(bytes.SequenceEqual(output.ToArray()));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    public static void BuiltInPngRendererCreatesReadableOnePagePdfFromSnapshot()
    {
        var root = Path.Combine(Path.GetTempPath(), $"paperbatch-image-render-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var sourcePath = Path.Combine(root, "source.png");
        var bytes = OnePixelPng();
        File.WriteAllBytes(sourcePath, bytes);
        try
        {
            var cache = new SourceSnapshotCache(new SourceSnapshotCacheOptions { RootPath = Path.Combine(root, "cache") });
            var snapshot = cache.CaptureAsync(sourcePath).GetAwaiter().GetResult();
            var store = new RenderArtifactStore(Path.Combine(root, "artifacts"));
            var adapter = new BuiltInImageRendererAdapter(cache, store);
            var result = adapter.RenderAsync(new RenderRequest(snapshot.SnapshotId, SourceFormat.Png,
                RendererExecutionContext.BackgroundWorker), CancellationToken.None).GetAwaiter().GetResult();

            Assert.Equal(RenderStatus.Succeeded, result.Status);
            Assert.Equal(1, result.Artifact?.PageCount);
            Assert.True(bytes.SequenceEqual(File.ReadAllBytes(sourcePath)));
            using var pdf = store.OpenRead(result.Artifact!.ArtifactId);
            var preflight = SourceFormatPreflight.Inspect(pdf, ".pdf");
            Assert.Equal(SourcePreflightStatus.Ready, preflight.Status);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    public static void BuiltInPngRendererRejectsCorruptCompressedRaster()
    {
        var root = Path.Combine(Path.GetTempPath(), $"paperbatch-image-render-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var sourcePath = Path.Combine(root, "source.png");
        File.WriteAllBytes(sourcePath, OnePixelPng(corruptCompressedRaster: true));
        try
        {
            var cache = new SourceSnapshotCache(new SourceSnapshotCacheOptions { RootPath = Path.Combine(root, "cache") });
            var snapshot = cache.CaptureAsync(sourcePath).GetAwaiter().GetResult();
            var adapter = new BuiltInImageRendererAdapter(cache, new RenderArtifactStore(Path.Combine(root, "artifacts")));
            var result = adapter.RenderAsync(new RenderRequest(snapshot.SnapshotId, SourceFormat.Png,
                RendererExecutionContext.BackgroundWorker), CancellationToken.None).GetAwaiter().GetResult();
            Assert.Equal(RenderStatus.Failed, result.Status);
            Assert.True(result.Artifact is null);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    public static void BuiltInPngRendererRejectsAlphaRatherThanDroppingIt()
    {
        var root = Path.Combine(Path.GetTempPath(), $"paperbatch-image-render-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var sourcePath = Path.Combine(root, "source.png");
        File.WriteAllBytes(sourcePath, OnePixelPng(colorType: 6));
        try
        {
            var cache = new SourceSnapshotCache(new SourceSnapshotCacheOptions { RootPath = Path.Combine(root, "cache") });
            var snapshot = cache.CaptureAsync(sourcePath).GetAwaiter().GetResult();
            var adapter = new BuiltInImageRendererAdapter(cache, new RenderArtifactStore(Path.Combine(root, "artifacts")));
            var result = adapter.RenderAsync(new RenderRequest(snapshot.SnapshotId, SourceFormat.Png,
                RendererExecutionContext.BackgroundWorker), CancellationToken.None).GetAwaiter().GetResult();
            Assert.Equal(RenderStatus.Unsupported, result.Status);
            Assert.True(result.Artifact is null);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static byte[] OnePixelPng(byte colorType = 2, bool corruptCompressedRaster = false)
    {
        using var output = new MemoryStream();
        output.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
        var header = new byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(0, 4), 1);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(4, 4), 1);
        header[8] = 8; header[9] = colorType;
        WritePngChunk(output, "IHDR", header);
        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.SmallestSize, leaveOpen: true))
            zlib.Write(colorType == 6 ? new byte[] { 0, 0x20, 0x40, 0x60, 0xff } : new byte[] { 0, 0x20, 0x40, 0x60 });
        var raster = compressed.ToArray();
        if (corruptCompressedRaster) raster[raster.Length / 2] ^= 0xff;
        WritePngChunk(output, "IDAT", raster);
        WritePngChunk(output, "IEND", []);
        return output.ToArray();
    }

    private static void WritePngChunk(Stream output, string name, byte[] data)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(length, checked((uint)data.Length));
        output.Write(length);
        var type = Encoding.ASCII.GetBytes(name);
        output.Write(type); output.Write(data);
        var crcData = new byte[type.Length + data.Length];
        type.CopyTo(crcData, 0); data.CopyTo(crcData, type.Length);
        Span<byte> crc = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crc, PngCrc32(crcData));
        output.Write(crc);
    }

    private static uint PngCrc32(ReadOnlySpan<byte> bytes)
    {
        var crc = 0xffffffffu;
        foreach (var value in bytes)
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++) crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xedb88320u : crc >> 1;
        }
        return ~crc;
    }

    private static byte[] OnePagePdf()
    {
        var objects = new[]
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Resources << /ProcSet [/PDF] >> /Contents 4 0 R >>",
            "<< /Length 0 >>\nstream\n\nendstream"
        };
        var builder = new StringBuilder("%PDF-1.7\n");
        var offsets = new List<int> { 0 };
        foreach (var item in objects)
        {
            offsets.Add(Encoding.ASCII.GetByteCount(builder.ToString()));
            builder.Append(offsets.Count).Append(" 0 obj\n").Append(item).Append("\nendobj\n");
        }
        var xref = Encoding.ASCII.GetByteCount(builder.ToString());
        builder.Append("xref\n0 5\n0000000000 65535 f \n");
        foreach (var offset in offsets.Skip(1)) builder.Append(offset.ToString("D10", System.Globalization.CultureInfo.InvariantCulture)).Append(" 00000 n \n");
        builder.Append("trailer\n<< /Size 5 /Root 1 0 R >>\nstartxref\n").Append(xref).Append("\n%%EOF\n");
        return Encoding.ASCII.GetBytes(builder.ToString());
    }

    public static void LibreOfficeCapabilityRejectsUserWritableRuntimeTree()
    {
        if (!OperatingSystem.IsLinux()) return;
        var root = Path.Combine(Path.GetTempPath(), $"paperbatch-lo-capability-{Guid.NewGuid():N}");
        var program = Path.Combine(root, "program");
        Directory.CreateDirectory(program);
        var engine = Path.Combine(program, "soffice.bin");
        File.WriteAllText(engine, "pinned executable fixture");
        File.WriteAllText(Path.Combine(program, "libfixture.so"), "runtime library fixture");
        try
        {
            var adapter = CreateLibreOfficeCapabilityProbe(root, program, engine);
            var supported = adapter.CanRenderAsync(new RenderRequest("snapshot", SourceFormat.Docx,
                RendererExecutionContext.BackgroundWorker), CancellationToken.None).GetAwaiter().GetResult();
            Assert.False(supported);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    public static void LibreOfficeCapabilityAcceptsProtectedTreeAndRejectsGroupWritableLibrary()
    {
        if (!OperatingSystem.IsLinux()) return;
        var root = Path.Combine(Path.GetTempPath(), $"paperbatch-lo-capability-{Guid.NewGuid():N}");
        var program = Path.Combine(root, "program");
        Directory.CreateDirectory(program);
        var engine = Path.Combine(program, "soffice.bin");
        var library = Path.Combine(program, "libfixture.so");
        File.WriteAllText(engine, "pinned executable fixture");
        File.WriteAllText(library, "runtime library fixture");
        try
        {
            File.SetUnixFileMode(program, UnixFileMode.UserRead | UnixFileMode.UserExecute);
            File.SetUnixFileMode(engine, UnixFileMode.UserRead | UnixFileMode.UserExecute);
            File.SetUnixFileMode(library, UnixFileMode.UserRead);
            var adapter = CreateLibreOfficeCapabilityProbe(root, program, engine);
            File.SetUnixFileMode(root, UnixFileMode.UserRead | UnixFileMode.UserExecute);
            var request = new RenderRequest("snapshot", SourceFormat.Docx, RendererExecutionContext.BackgroundWorker);
            Assert.True(adapter.CanRenderAsync(request, CancellationToken.None).GetAwaiter().GetResult());

            File.SetUnixFileMode(library, UnixFileMode.UserRead | UnixFileMode.GroupWrite);
            Assert.False(adapter.CanRenderAsync(request, CancellationToken.None).GetAwaiter().GetResult());
        }
        finally
        {
            File.SetUnixFileMode(engine, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            File.SetUnixFileMode(Path.Combine(program, "libfixture.so"), UnixFileMode.UserRead | UnixFileMode.UserWrite);
            File.SetUnixFileMode(program, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            File.SetUnixFileMode(root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            Directory.Delete(root, recursive: true);
        }
    }

    public static void LibreOfficeCapabilityRejectsWritableAncestorOfProtectedRuntime()
    {
        if (!OperatingSystem.IsLinux()) return;
        var root = Path.Combine(Path.GetTempPath(), $"paperbatch-lo-ancestor-{Guid.NewGuid():N}");
        var install = Path.Combine(root, "install");
        var program = Path.Combine(install, "program");
        Directory.CreateDirectory(program);
        var engine = Path.Combine(program, "soffice.bin");
        File.WriteAllText(engine, "pinned executable fixture");
        File.WriteAllText(Path.Combine(program, "libfixture.so"), "runtime library fixture");
        try
        {
            File.SetUnixFileMode(root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupWrite);
            File.SetUnixFileMode(install, UnixFileMode.UserRead | UnixFileMode.UserExecute);
            File.SetUnixFileMode(program, UnixFileMode.UserRead | UnixFileMode.UserExecute);
            File.SetUnixFileMode(engine, UnixFileMode.UserRead | UnixFileMode.UserExecute);
            File.SetUnixFileMode(Path.Combine(program, "libfixture.so"), UnixFileMode.UserRead);

            var adapter = CreateLibreOfficeCapabilityProbe(root, program, engine);
            Assert.False(adapter.CanRenderAsync(new RenderRequest("snapshot", SourceFormat.Docx,
                RendererExecutionContext.BackgroundWorker), CancellationToken.None).GetAwaiter().GetResult());
        }
        finally
        {
            File.SetUnixFileMode(root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            File.SetUnixFileMode(install, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            File.SetUnixFileMode(program, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            File.SetUnixFileMode(engine, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            File.SetUnixFileMode(Path.Combine(program, "libfixture.so"), UnixFileMode.UserRead | UnixFileMode.UserWrite);
            Directory.Delete(root, recursive: true);
        }
    }

    public static void LibreOfficeCapabilityRejectsDependencySymlinkEscape()
    {
        if (!OperatingSystem.IsLinux()) return;
        var root = Path.Combine(Path.GetTempPath(), $"paperbatch-lo-capability-{Guid.NewGuid():N}");
        var program = Path.Combine(root, "program");
        var outside = Path.Combine(root, "outside");
        Directory.CreateDirectory(program);
        Directory.CreateDirectory(outside);
        var engine = Path.Combine(program, "soffice.bin");
        var externalLibrary = Path.Combine(outside, "libfixture.so");
        File.WriteAllText(engine, "pinned executable fixture");
        File.WriteAllText(externalLibrary, "untrusted runtime library fixture");
        var link = Path.Combine(program, "libfixture.so");
        try
        {
            File.SetUnixFileMode(engine, UnixFileMode.UserRead | UnixFileMode.UserExecute);
            File.SetUnixFileMode(externalLibrary, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            File.SetUnixFileMode(outside, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            Directory.CreateSymbolicLink(link, externalLibrary);
            File.SetUnixFileMode(program, UnixFileMode.UserRead | UnixFileMode.UserExecute);
            var adapter = CreateLibreOfficeCapabilityProbe(root, program, engine);
            File.SetUnixFileMode(root, UnixFileMode.UserRead | UnixFileMode.UserExecute);
            var supported = adapter.CanRenderAsync(new RenderRequest("snapshot", SourceFormat.Docx,
                RendererExecutionContext.BackgroundWorker), CancellationToken.None).GetAwaiter().GetResult();
            Assert.False(supported);
        }
        finally
        {
            File.SetUnixFileMode(engine, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            File.SetUnixFileMode(program, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            File.SetUnixFileMode(outside, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            File.SetUnixFileMode(root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            Directory.Delete(root, recursive: true);
        }
    }

    private static LibreOfficeRendererAdapter CreateLibreOfficeCapabilityProbe(string root, string program, string engine)
    {
        var snapshots = new SourceSnapshotCache(new SourceSnapshotCacheOptions { RootPath = Path.Combine(root, "cache") });
        var artifacts = new RenderArtifactStore(Path.Combine(root, "artifacts"));
        var digest = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(engine))).ToLowerInvariant();
        return new LibreOfficeRendererAdapter(snapshots, artifacts, new UnusedPdfInspector(), engine, digest);
    }

    private sealed class UnusedPdfInspector : IPdfArtifactInspector
    {
        public Task<int> GetPageCountAsync(Stream pdf, CancellationToken cancellationToken) =>
            throw new NotSupportedException("The capability probe does not render PDFs.");
    }

    public static void LibreOfficeRejectsSharedOperationDirectory()
    {
        if (OperatingSystem.IsWindows()) return;
        var operationDirectory = Path.Combine(Path.GetTempPath(), $"paperbatch-render-shared-{Guid.NewGuid():N}");
        Directory.CreateDirectory(operationDirectory);
        try
        {
            File.SetUnixFileMode(operationDirectory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute);
            Assert.Throws<IOException>(() => LibreOfficeProfilePolicy.CreatePrivateProfile(operationDirectory));
        }
        finally
        {
            Directory.Delete(operationDirectory, recursive: true);
        }
    }

    public static void LibreOfficeRejectsReplaceableOperationPathAncestors()
    {
        if (OperatingSystem.IsWindows()) return;
        var sharedParent = Path.Combine(Path.GetTempPath(), $"paperbatch-shared-parent-{Guid.NewGuid():N}");
        var operationDirectory = Path.Combine(sharedParent, "operation");
        Directory.CreateDirectory(operationDirectory);
        try
        {
            File.SetUnixFileMode(sharedParent, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute);
            File.SetUnixFileMode(operationDirectory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            Assert.Throws<IOException>(() => LibreOfficeProfilePolicy.CreatePrivateProfile(operationDirectory));
        }
        finally
        {
            Directory.Delete(sharedParent, recursive: true);
        }
    }

    public static void LibreOfficeUsesPrivateMacroRestrictedProfile()
    {
        var operationDirectory = Path.Combine(Path.GetTempPath(), $"paperbatch-render-{Guid.NewGuid():N}");
        Directory.CreateDirectory(operationDirectory);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(operationDirectory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        try
        {
            var profileUri = LibreOfficeProfilePolicy.CreatePrivateProfile(operationDirectory);
            var profilePath = profileUri.LocalPath;
            Assert.True(Path.GetFullPath(profilePath).StartsWith(Path.GetFullPath(operationDirectory) + Path.DirectorySeparatorChar, StringComparison.Ordinal));
            var settings = File.ReadAllText(Path.Combine(profilePath, "registrymodifications.xcu"));
            Assert.Contains("MacroSecurityLevel", settings);
            Assert.Contains("<value>3</value>", settings);
            Assert.Contains("/org.openoffice.Office.Common/Update/Links", settings);
            Assert.Contains("<value>0</value>", settings);
        }
        finally
        {
            Directory.Delete(operationDirectory, recursive: true);
        }
    }

    public static void OwnedProcessWorkerWaitsForSuccessfulChild()
    {
        var result = OwnedProcessWorker.RunAsync(CreateChildProcess("exit", null), TimeSpan.FromSeconds(10))
            .GetAwaiter().GetResult();
        Assert.Equal(OwnedProcessStatus.Exited, result.Status);
        Assert.Equal(0, result.ExitCode);
        Assert.True(result.ProcessId > 0);
    }

    public static void OwnedProcessWorkerKillsOnlyItsChildOnTimeout()
    {
        var marker = Path.Combine(Path.GetTempPath(), $"paperbatch-child-{Guid.NewGuid():N}.pid");
        try
        {
            var task = OwnedProcessWorker.RunAsync(CreateChildProcess("hang", marker), TimeSpan.FromMilliseconds(100));
            var result = task.GetAwaiter().GetResult();
            Assert.Equal(OwnedProcessStatus.TimedOut, result.Status);
            Assert.True(result.ProcessId > 0);
            Assert.True(ChildHasExited(result.ProcessId!.Value));
        }
        finally
        {
            if (File.Exists(marker)) File.Delete(marker);
        }
    }

    public static void OwnedProcessWorkerKillsItsChildOnCancellation()
    {
        var marker = Path.Combine(Path.GetTempPath(), $"paperbatch-child-{Guid.NewGuid():N}.pid");
        try
        {
            using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
            var result = OwnedProcessWorker.RunAsync(CreateChildProcess("hang", marker), TimeSpan.FromSeconds(10), cancellation.Token)
                .GetAwaiter().GetResult();
            Assert.Equal(OwnedProcessStatus.Cancelled, result.Status);
            Assert.True(result.ProcessId > 0);
            Assert.True(ChildHasExited(result.ProcessId!.Value));
        }
        finally
        {
            if (File.Exists(marker)) File.Delete(marker);
        }
    }

    private static System.Diagnostics.ProcessStartInfo CreateChildProcess(string mode, string? marker)
    {
        var executable = Environment.ProcessPath ?? throw new InvalidOperationException("Test host path is unavailable.");
        var startInfo = new System.Diagnostics.ProcessStartInfo(executable) { UseShellExecute = false };
        var entryAssembly = System.Reflection.Assembly.GetEntryAssembly()?.Location;
        if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        {
            startInfo.ArgumentList.Add(entryAssembly ?? throw new InvalidOperationException("Test assembly path is unavailable."));
        }
        startInfo.ArgumentList.Add("--renderer-worker-child");
        startInfo.ArgumentList.Add(mode);
        if (marker is not null) startInfo.ArgumentList.Add(marker);
        return startInfo;
    }

    private static bool ChildHasExited(int processId)
    {
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(processId);
            return process.HasExited;
        }
        catch (ArgumentException)
        {
            return true;
        }
    }

    public static void UnsupportedFormatIsReportedWithoutFallback()
    {
        var adapter = new FakeAdapter(SourceFormat.Docx, RendererExecutionContext.BackgroundWorker,
            (_, _) => Task.FromResult(new RenderResult(RenderStatus.Failed, null, "render failed")));
        var other = new FakeAdapter(SourceFormat.Docx, RendererExecutionContext.BackgroundWorker,
            (_, _) => throw new InvalidOperationException("fallback ran"));
        var coordinator = new RendererCoordinator([adapter, other]);

        var result = coordinator.RenderAsync(new RenderRequest("snapshot", SourceFormat.Docx,
            RendererExecutionContext.BackgroundWorker), TimeSpan.FromSeconds(1)).GetAwaiter().GetResult();

        Assert.Equal(RenderStatus.Failed, result.Status);
        Assert.Equal(1, adapter.CallCount);
        Assert.Equal(0, other.CallCount);
    }

    public static void MissingAdapterIsExplicitlyUnsupported()
    {
        var coordinator = new RendererCoordinator([]);
        var result = coordinator.RenderAsync(new RenderRequest("snapshot", SourceFormat.Pdf,
            RendererExecutionContext.BackgroundWorker), TimeSpan.FromSeconds(1)).GetAwaiter().GetResult();
        Assert.Equal(RenderStatus.Unsupported, result.Status);
    }

    public static void CancelledRequestDoesNotLaunchAnAdapter()
    {
        var adapter = new FakeAdapter(SourceFormat.Pdf, RendererExecutionContext.BackgroundWorker,
            (_, _) => throw new InvalidOperationException("cancelled request launched"));
        var coordinator = new RendererCoordinator([adapter]);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var result = coordinator.RenderAsync(new RenderRequest("snapshot", SourceFormat.Pdf,
            RendererExecutionContext.BackgroundWorker), TimeSpan.FromSeconds(1), cancellation.Token).GetAwaiter().GetResult();

        Assert.Equal(RenderStatus.Cancelled, result.Status);
        Assert.Equal(0, adapter.CallCount);
    }

    public static void CapabilityFailureStopsWithoutTryingAnotherAdapter()
    {
        var broken = new FakeAdapter(SourceFormat.Pdf, RendererExecutionContext.BackgroundWorker,
            (_, _) => Task.FromResult(new RenderResult(RenderStatus.Succeeded, null, null))) { ThrowOnCanRender = true };
        var other = new FakeAdapter(SourceFormat.Pdf, RendererExecutionContext.BackgroundWorker,
            (_, _) => throw new InvalidOperationException("fallback ran"));
        var coordinator = new RendererCoordinator([broken, other]);

        var result = coordinator.RenderAsync(new RenderRequest("snapshot", SourceFormat.Pdf,
            RendererExecutionContext.BackgroundWorker), TimeSpan.FromSeconds(1)).GetAwaiter().GetResult();

        Assert.Equal(RenderStatus.Failed, result.Status);
        Assert.Equal(0, other.CallCount);
    }

    public static void CapabilityCheckIsInsideTheRenderDeadline()
    {
        var capabilityCancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var adapter = new FakeAdapter(SourceFormat.Pdf, RendererExecutionContext.BackgroundWorker,
            (_, _) => throw new InvalidOperationException("render should not start"))
        {
            CapabilityCheck = async cancellationToken =>
            {
                using var registration = cancellationToken.Register(() => capabilityCancelled.TrySetResult());
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return true;
            }
        };
        var coordinator = new RendererCoordinator([adapter]);

        var result = coordinator.RenderAsync(new RenderRequest("snapshot", SourceFormat.Pdf,
            RendererExecutionContext.BackgroundWorker), TimeSpan.FromMilliseconds(25)).GetAwaiter().GetResult();

        Assert.Equal(RenderStatus.TimedOut, result.Status);
        Assert.True(capabilityCancelled.Task.Wait(TimeSpan.FromSeconds(1)));
    }

    public static void ThrowingCancellationCallbacksDoNotHideTimeout()
    {
        var adapter = new FakeAdapter(SourceFormat.Pdf, RendererExecutionContext.BackgroundWorker,
            async (_, cancellationToken) =>
            {
                using var registration = cancellationToken.Register(() => throw new InvalidOperationException("callback failed"));
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return new RenderResult(RenderStatus.Succeeded, null, null);
            });
        var coordinator = new RendererCoordinator([adapter]);

        var result = coordinator.RenderAsync(new RenderRequest("snapshot", SourceFormat.Pdf,
            RendererExecutionContext.BackgroundWorker), TimeSpan.FromMilliseconds(25)).GetAwaiter().GetResult();

        Assert.Equal(RenderStatus.TimedOut, result.Status);
    }

    public static void AdapterTimeoutIsDistinguished()
    {
        var adapter = new FakeAdapter(SourceFormat.Docx, RendererExecutionContext.BackgroundWorker,
            async (_, cancellationToken) =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return new RenderResult(RenderStatus.Succeeded, null, null);
            });
        var coordinator = new RendererCoordinator([adapter]);
        var result = coordinator.RenderAsync(new RenderRequest("snapshot", SourceFormat.Docx,
            RendererExecutionContext.BackgroundWorker), TimeSpan.FromMilliseconds(25)).GetAwaiter().GetResult();
        Assert.Equal(RenderStatus.TimedOut, result.Status);
    }

    private sealed class FakeAdapter(
        SourceFormat format,
        RendererExecutionContext executionContext,
        Func<RenderRequest, CancellationToken, Task<RenderResult>> render) : IRendererAdapter
    {
        public int CallCount { get; private set; }
        public Func<CancellationToken, Task<bool>>? CapabilityCheck { get; init; }
        public bool ThrowOnCanRender { get; init; }
        public async ValueTask<bool> CanRenderAsync(RenderRequest request, CancellationToken cancellationToken)
        {
            if (ThrowOnCanRender) throw new InvalidOperationException("capability check failed");
            if (CapabilityCheck is not null) return await CapabilityCheck(cancellationToken);
            return request.Format == format && request.ExecutionContext == executionContext;
        }
        public async Task<RenderResult> RenderAsync(RenderRequest request, CancellationToken cancellationToken)
        {
            CallCount++;
            return await render(request, cancellationToken);
        }
    }
}
