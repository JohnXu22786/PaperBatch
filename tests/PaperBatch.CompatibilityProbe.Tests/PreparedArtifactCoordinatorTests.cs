using System.Runtime.InteropServices;
using PaperBatch.Core.Domain;
using PaperBatch.Core.Intake;
using PaperBatch.Core.Layout;
using PaperBatch.Core.Persistence;
using PaperBatch.Core.Preparation;
using PaperBatch.Core.Rendering;

internal static class PreparedArtifactCoordinatorTests
{
    private static readonly DateTimeOffset FixedTime = new(2026, 10, 20, 12, 0, 0, TimeSpan.Zero);

    internal static void PreparationRejectsSourceAboveComposerLimitBeforeCopyOrComposition()
    {
        var root = Path.Combine(Path.GetTempPath(), "paperbatch-composer-source-limit-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var supportedPath = Path.Combine(root, "supported.pdf");
            var oversizedPath = Path.Combine(root, "oversized.pdf");
            var supportedPdf = RendererTests.OnePagePdf();
            File.WriteAllBytes(supportedPath, supportedPdf);
            var supportedPdfText = System.Text.Encoding.ASCII.GetString(supportedPdf);
            // Insert one valid whitespace byte before xref and update startxref to keep the fixture parseable.
            var xrefStart = supportedPdfText.LastIndexOf("xref\n0 ", StringComparison.Ordinal);
            var startXrefMarker = supportedPdfText.LastIndexOf("startxref\n", StringComparison.Ordinal);
            var startXrefValueStart = startXrefMarker + "startxref\n".Length;
            var oversizedPdfText = supportedPdfText.Insert(xrefStart, "\n");
            startXrefMarker++;
            startXrefValueStart = startXrefMarker + "startxref\n".Length;
            var startXrefValueEnd = oversizedPdfText.IndexOf('\n', startXrefValueStart);
            oversizedPdfText = oversizedPdfText[..startXrefValueStart] +
                (xrefStart + 1).ToString(System.Globalization.CultureInfo.InvariantCulture) + oversizedPdfText[startXrefValueEnd..];
            Assert.Equal(supportedPdf.Length + 1, System.Text.Encoding.ASCII.GetByteCount(oversizedPdfText));
            File.WriteAllBytes(oversizedPath, System.Text.Encoding.ASCII.GetBytes(oversizedPdfText));
            var cache = new SourceSnapshotCache(new SourceSnapshotCacheOptions { RootPath = Path.Combine(root, "snapshots") });
            var artifacts = new RenderArtifactStore(Path.Combine(root, "artifacts"));
            var renderers = new RendererCoordinator([
                new PdfPassThroughRendererAdapter(new PdfPassThroughArtifactService(cache, artifacts))
            ]);
            var runs = new SqliteRunStore(Path.Combine(root, "runs.db"));
            runs.Initialize();
            var composer = new FixtureCompositionAdapter(pageCount => pageCount == 1
                ? [.. RendererTests.OnePagePdf(), (byte)' ']
                : PdfCompositionTests.TwoPagePdf(), maximumSourceBytes: supportedPdf.Length);
            var staticInspector = new VerifiedStaticFixtureInspector();
            var coordinator = new PreparedArtifactCoordinator(new SourceIntakeService(cache), cache, renderers,
                new FixturePageMetadataInspector(), staticInspector, composer, artifacts, runs);
            var settings = new PrintSettings(1, ColorMode.Color, DuplexMode.Simplex, "Letter",
                Orientation.Auto, ScalingMode.ShrinkToFit);
            var retention = new RetentionMetadata(FixedTime.AddDays(2), RetainWhileUnresolved: true);

            var supported = coordinator.PrepareAsync([new SourceInput(supportedPath)], settings, new LayoutOptions(),
                "composer-source-limit-at-boundary", retention, FixedTime).GetAwaiter().GetResult();
            var supportedBatch = coordinator.PrepareAsync(
                [new SourceInput(supportedPath), new SourceInput(supportedPath, IntentionalCopy: true)],
                settings, new LayoutOptions(), "composer-source-limit-supported-batch", retention, FixedTime.AddMinutes(1))
                .GetAwaiter().GetResult();
            var oversized = coordinator.PrepareAsync([new SourceInput(oversizedPath)], settings, new LayoutOptions(),
                "composer-source-limit-above-boundary", retention, FixedTime.AddMinutes(2)).GetAwaiter().GetResult();

            Assert.True(supported.Succeeded, string.Join("; ", supported.Diagnostics));
            Assert.True(supportedBatch.Succeeded, string.Join("; ", supportedBatch.Diagnostics));
            Assert.Equal(2, supportedBatch.Run!.Items.Length);
            Assert.Equal(2, composer.Calls);
            Assert.Equal(supportedPdf.Length * 2, composer.LastSourceByteCount);
            Assert.Equal(3, staticInspector.Calls);
            Assert.False(oversized.Succeeded);
            Assert.True(oversized.Run is null && oversized.Persistence is null);
            Assert.True(
                oversized.Diagnostics.Any(message => message.Contains(
                    "oversized.pdf: the rendered PDF exceeds the page compositor source-size limit", StringComparison.Ordinal)),
                string.Join("; ", oversized.Diagnostics));
            Assert.Equal(2, composer.Calls);
            Assert.Equal(supportedPdf.Length * 2, composer.LastSourceByteCount);
            Assert.Equal(3, staticInspector.Calls);
            Assert.Equal(2, runs.GetRecentRuns().Length);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    internal static void PreparationRejectsInvalidIdempotencyKeysBeforeReadingInputs()
    {
        var coordinator = new PreparedArtifactCoordinator(null!, null!, null!, null!, null!, null!, null!, null!);
        var inputs = new UnenumeratedInputs();
        var settings = new PrintSettings(1, ColorMode.Color, DuplexMode.Simplex, "Letter",
            Orientation.Auto, ScalingMode.ShrinkToFit);

        foreach (var idempotencyKey in new string?[] { null, string.Empty, " \t " })
        {
            try
            {
                coordinator.PrepareAsync(inputs, settings, new LayoutOptions(), idempotencyKey!,
                    new RetentionMetadata(FixedTime.AddDays(1), RetainWhileUnresolved: true), FixedTime)
                    .GetAwaiter().GetResult();
                throw new InvalidOperationException("Expected invalid idempotency key to be rejected.");
            }
            catch (ArgumentException exception) when (exception.ParamName == "idempotencyKey")
            {
            }
        }
    }

    internal static void PerItemPrintCopiesAreCompiledWhenLayoutCopiesAreAbsent()
    {
        var root = Path.Combine(Path.GetTempPath(), "paperbatch-per-item-copies-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var sourcePath = Path.Combine(root, "source.pdf");
            File.WriteAllBytes(sourcePath, RendererTests.OnePagePdf());
            var cache = new SourceSnapshotCache(new SourceSnapshotCacheOptions { RootPath = Path.Combine(root, "snapshots") });
            var artifacts = new RenderArtifactStore(Path.Combine(root, "artifacts"));
            var renderers = new RendererCoordinator([
                new PdfPassThroughRendererAdapter(new PdfPassThroughArtifactService(cache, artifacts))
            ]);
            var runs = new SqliteRunStore(Path.Combine(root, "runs.db"));
            runs.Initialize();
            var coordinator = new PreparedArtifactCoordinator(new SourceIntakeService(cache), cache, renderers,
                new FixturePageMetadataInspector(), new VerifiedStaticFixtureInspector(), new FixtureCompositionAdapter(pageCount =>
                    pageCount == 1 ? [.. RendererTests.OnePagePdf(), (byte)' '] : PdfCompositionTests.TwoPagePdf()),
                artifacts, runs);
            var settings = new PrintSettings(1, ColorMode.Color, DuplexMode.Simplex, "Letter",
                Orientation.Auto, ScalingMode.ShrinkToFit);

            var result = coordinator.PrepareAsync(
                [new SourceInput(sourcePath)], settings,
                new LayoutOptions(NUp: NUpMode.One), "per-item-copies-layout-default-key",
                new RetentionMetadata(FixedTime.AddDays(2), RetainWhileUnresolved: true), FixedTime,
                [settings with { Copies = 2 }],
                [new LayoutDocumentSettings(NUp: NUpMode.One)])
                .GetAwaiter().GetResult();

            Assert.True(result.Succeeded, string.Join("; ", result.Diagnostics));
            Assert.Equal(2, result.Run!.FinalArtifact!.PageCount);
            Assert.Equal(1, result.Run.Items[0].PrintSettings.Copies);
            Assert.True(result.Run.Segments.SelectMany(segment => segment.FinalPageNumbers).SequenceEqual([1, 2]));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    internal static void ValidPreparationIdempotencyKeyReplaysThePersistedRun()
    {
        var root = Path.Combine(Path.GetTempPath(), "paperbatch-prepare-idempotency-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var sourcePath = Path.Combine(root, "source.pdf");
            File.WriteAllBytes(sourcePath, RendererTests.OnePagePdf());
            var cache = new SourceSnapshotCache(new SourceSnapshotCacheOptions { RootPath = Path.Combine(root, "snapshots") });
            var artifacts = new RenderArtifactStore(Path.Combine(root, "artifacts"));
            var renderers = new RendererCoordinator([
                new PdfPassThroughRendererAdapter(new PdfPassThroughArtifactService(cache, artifacts))
            ]);
            var runs = new SqliteRunStore(Path.Combine(root, "runs.db"));
            runs.Initialize();
            var coordinator = new PreparedArtifactCoordinator(new SourceIntakeService(cache), cache, renderers,
                new FixturePageMetadataInspector(), new VerifiedStaticFixtureInspector(), new FixtureCompositionAdapter(),
                artifacts, runs);
            var settings = new PrintSettings(1, ColorMode.Color, DuplexMode.Simplex, "Letter",
                Orientation.Auto, ScalingMode.ShrinkToFit);
            var retention = new RetentionMetadata(FixedTime.AddDays(2), RetainWhileUnresolved: true);

            var first = coordinator.PrepareAsync([new SourceInput(sourcePath)], settings, new LayoutOptions(),
                "valid-prepare-key", retention, FixedTime).GetAwaiter().GetResult();
            var replay = coordinator.PrepareAsync([new SourceInput(sourcePath)], settings, new LayoutOptions(),
                "valid-prepare-key", retention, FixedTime.AddMinutes(1)).GetAwaiter().GetResult();

            Assert.True(first.Succeeded, string.Join("; ", first.Diagnostics));
            Assert.True(replay.Succeeded, string.Join("; ", replay.Diagnostics));
            Assert.False(first.Persistence!.Replayed);
            Assert.True(replay.Persistence!.Replayed);
            Assert.Equal(first.Run!.RunId, replay.Run!.RunId);
            Assert.True(first.Run.Segments.Single().FinalPageNumbers.SequenceEqual([1]));
            Assert.True(replay.Run!.Segments.Single().FinalPageNumbers.SequenceEqual([1]));
            Assert.True(runs.GetPreparedRun(first.Run.RunId)!.Segments.Single().FinalPageNumbers.SequenceEqual([1]));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    internal static void DirectoryPreparationIdempotencyTracksEveryExpandedSource()
    {
        var root = Path.Combine(Path.GetTempPath(), "paperbatch-directory-prepare-idempotency-" + Guid.NewGuid().ToString("N"));
        var inputDirectory = Path.Combine(root, "inputs");
        Directory.CreateDirectory(inputDirectory);
        try
        {
            var firstPath = Path.Combine(inputDirectory, "01-first.pdf");
            var laterPath = Path.Combine(inputDirectory, "02-later.pdf");
            File.WriteAllBytes(firstPath, RendererTests.OnePagePdf());
            File.WriteAllBytes(laterPath, [.. RendererTests.OnePagePdf(), (byte)'\n']);

            var cache = new SourceSnapshotCache(new SourceSnapshotCacheOptions { RootPath = Path.Combine(root, "snapshots") });
            var artifacts = new RenderArtifactStore(Path.Combine(root, "artifacts"));
            var renderers = new RendererCoordinator([
                new PdfPassThroughRendererAdapter(new PdfPassThroughArtifactService(cache, artifacts))
            ]);
            var runs = new SqliteRunStore(Path.Combine(root, "runs.db"));
            runs.Initialize();
            var coordinator = new PreparedArtifactCoordinator(new SourceIntakeService(cache), cache, renderers,
                new FixturePageMetadataInspector(), new VerifiedStaticFixtureInspector(),
                new FixtureCompositionAdapter(pageCount => pageCount == 2
                    ? PdfCompositionTests.TwoPagePdf() : RendererTests.OnePagePdf()), artifacts, runs);
            var settings = new PrintSettings(1, ColorMode.Color, DuplexMode.Simplex, "Letter",
                Orientation.Auto, ScalingMode.ShrinkToFit);
            var retention = new RetentionMetadata(FixedTime.AddDays(2), RetainWhileUnresolved: true);
            var input = new[] { new SourceInput(inputDirectory) };

            var first = coordinator.PrepareAsync(input, settings, new LayoutOptions(),
                "directory-prepare-idempotency-key", retention, FixedTime).GetAwaiter().GetResult();
            var unchangedRetry = coordinator.PrepareAsync(input, settings, new LayoutOptions(),
                "directory-prepare-idempotency-key", retention, FixedTime.AddMinutes(1)).GetAwaiter().GetResult();

            Assert.True(first.Succeeded, string.Join("; ", first.Diagnostics));
            Assert.Equal(2, first.Run!.Items.Length);
            Assert.False(first.Persistence!.Replayed);
            Assert.True(unchangedRetry.Succeeded, string.Join("; ", unchangedRetry.Diagnostics));
            Assert.True(unchangedRetry.Persistence!.Replayed);
            Assert.Equal(first.Run.RunId, unchangedRetry.Run!.RunId);

            File.WriteAllBytes(laterPath, [.. RendererTests.OnePagePdf(), (byte)'\n', (byte)'\n']);
            var changedRetry = coordinator.PrepareAsync(input, settings, new LayoutOptions(),
                "directory-prepare-idempotency-key", retention, FixedTime.AddMinutes(2)).GetAwaiter().GetResult();

            Assert.True(changedRetry.Succeeded, string.Join("; ", changedRetry.Diagnostics));
            Assert.False(changedRetry.Persistence!.Replayed);
            Assert.False(first.Run.RunId == changedRetry.Run!.RunId);
            Assert.Equal(2, changedRetry.Run.Items.Length);
            var expectedHashes = new[] { firstPath, laterPath }
                .Select(path => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path)))
                    .ToLowerInvariant())
                .Order(StringComparer.Ordinal)
                .ToArray();
            var actualHashes = changedRetry.Run.Items.Select(item => item.Source.Sha256)
                .Order(StringComparer.Ordinal)
                .ToArray();
            Assert.True(expectedHashes.SequenceEqual(actualHashes));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    internal static void ExpectedShaForExplicitFileSurvivesDirectoryExpansion()
    {
        var root = Path.Combine(Path.GetTempPath(), "paperbatch-expanded-expected-sha-" + Guid.NewGuid().ToString("N"));
        var inputDirectory = Path.Combine(root, "inputs");
        Directory.CreateDirectory(inputDirectory);
        try
        {
            var firstPath = Path.Combine(inputDirectory, "01-first.pdf");
            var secondPath = Path.Combine(inputDirectory, "02-second.pdf");
            var explicitPath = Path.Combine(root, "explicit.pdf");
            File.WriteAllBytes(firstPath, RendererTests.OnePagePdf());
            File.WriteAllBytes(secondPath, [.. RendererTests.OnePagePdf(), (byte)'\n']);
            byte[] explicitBytes = [.. RendererTests.OnePagePdf(), (byte)'\n', (byte)'\n'];
            File.WriteAllBytes(explicitPath, explicitBytes);

            var cache = new SourceSnapshotCache(new SourceSnapshotCacheOptions { RootPath = Path.Combine(root, "snapshots") });
            var artifacts = new RenderArtifactStore(Path.Combine(root, "artifacts"));
            var renderers = new RendererCoordinator([
                new PdfPassThroughRendererAdapter(new PdfPassThroughArtifactService(cache, artifacts))
            ]);
            var runs = new SqliteRunStore(Path.Combine(root, "runs.db"));
            runs.Initialize();
            var coordinator = new PreparedArtifactCoordinator(new SourceIntakeService(cache), cache, renderers,
                new FixturePageMetadataInspector(), new VerifiedStaticFixtureInspector(),
                new FixtureCompositionAdapter(pageCount => pageCount == 3 ? ThreePagePdf() : RendererTests.OnePagePdf()),
                artifacts, runs);
            var settings = new PrintSettings(1, ColorMode.Color, DuplexMode.Simplex, "Letter",
                Orientation.Auto, ScalingMode.ShrinkToFit);
            var retention = new RetentionMetadata(FixedTime.AddDays(2), RetainWhileUnresolved: true);
            var explicitSha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(explicitBytes))
                .ToLowerInvariant();
            var inputs = new[]
            {
                new SourceInput(inputDirectory),
                new SourceInput(explicitPath, ExpectedSha256: explicitSha256)
            };

            var unchanged = coordinator.PrepareAsync(inputs, settings, new LayoutOptions(),
                "expanded-expected-sha-unchanged", retention, FixedTime).GetAwaiter().GetResult();

            Assert.True(unchanged.Succeeded, string.Join("; ", unchanged.Diagnostics));
            Assert.Equal(3, unchanged.Run!.Items.Length);
            Assert.Equal(explicitSha256, unchanged.Run.Items[2].Source.Sha256);

            File.WriteAllBytes(explicitPath, [.. explicitBytes, (byte)'\n']);
            var changed = coordinator.PrepareAsync(inputs, settings, new LayoutOptions(),
                "expanded-expected-sha-changed", retention, FixedTime.AddMinutes(1)).GetAwaiter().GetResult();

            Assert.False(changed.Succeeded, "A changed explicit source must fail its own expected SHA check.");
            Assert.True(changed.Run is null && changed.Persistence is null);
            Assert.True(changed.Diagnostics.Any(message => message.Contains(
                "explicit.pdf changed after queue preflight", StringComparison.Ordinal)), string.Join("; ", changed.Diagnostics));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    internal static void MismatchedExpectedShaRejectsExistingIdempotentRunReplay()
    {
        var root = Path.Combine(Path.GetTempPath(), "paperbatch-prepare-expected-sha-replay-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var sourcePath = Path.Combine(root, "source.pdf");
            File.WriteAllBytes(sourcePath, RendererTests.OnePagePdf());
            var cache = new SourceSnapshotCache(new SourceSnapshotCacheOptions { RootPath = Path.Combine(root, "snapshots") });
            var artifacts = new RenderArtifactStore(Path.Combine(root, "artifacts"));
            var renderers = new RendererCoordinator([
                new PdfPassThroughRendererAdapter(new PdfPassThroughArtifactService(cache, artifacts))
            ]);
            var runs = new SqliteRunStore(Path.Combine(root, "runs.db"));
            runs.Initialize();
            var coordinator = new PreparedArtifactCoordinator(new SourceIntakeService(cache), cache, renderers,
                new FixturePageMetadataInspector(), new VerifiedStaticFixtureInspector(), new FixtureCompositionAdapter(),
                artifacts, runs);
            var settings = new PrintSettings(1, ColorMode.Color, DuplexMode.Simplex, "Letter",
                Orientation.Auto, ScalingMode.ShrinkToFit);
            var retention = new RetentionMetadata(FixedTime.AddDays(2), RetainWhileUnresolved: true);

            var first = coordinator.PrepareAsync([new SourceInput(sourcePath)], settings, new LayoutOptions(),
                "expected-sha-replay-key", retention, FixedTime).GetAwaiter().GetResult();
            var retry = coordinator.PrepareAsync([new SourceInput(sourcePath, ExpectedSha256: new string('0', 64))],
                settings, new LayoutOptions(), "expected-sha-replay-key", retention, FixedTime.AddMinutes(1))
                .GetAwaiter().GetResult();

            Assert.True(first.Succeeded, string.Join("; ", first.Diagnostics));
            Assert.False(first.Persistence!.Replayed);
            Assert.False(retry.Succeeded, "A mismatched expected digest must be rejected before an existing run is replayed.");
            Assert.True(retry.Run is null);
            Assert.True(retry.Persistence is null);
            Assert.True(retry.Diagnostics.Any(message => message.Contains("changed after queue preflight", StringComparison.Ordinal)));
            Assert.True(runs.GetPreparedRun(first.Run!.RunId) is not null);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    internal static void PipelineComposesOrderedSnapshotsAndPersistsExactFinalPdf()
    {
        if (!OperatingSystem.IsWindows() || RuntimeInformation.ProcessArchitecture != Architecture.X64)
        {
            Console.WriteLine("SKIP prepared artifact pipeline composition (the PDFium compositor is qualified only on Windows x64).");
            return;
        }
        var runtimePath = Environment.GetEnvironmentVariable("PAPERBATCH_PDFIUM_TEST_PATH");
        var expectedHash = Environment.GetEnvironmentVariable("PAPERBATCH_PDFIUM_TEST_SHA256");
        if (string.IsNullOrWhiteSpace(runtimePath) || string.IsNullOrWhiteSpace(expectedHash)) return;

        var root = Path.Combine(Path.GetTempPath(), "paperbatch-prepared-e2e-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var stage = "initialize fixtures";
        try
        {
            var firstPath = Path.Combine(root, "first.pdf");
            var secondPath = Path.Combine(root, "second.pdf");
            var firstBytes = RendererTests.OnePagePdf();
            var secondBytes = PdfCompositionTests.TwoPagePdf();
            File.WriteAllBytes(firstPath, firstBytes);
            File.WriteAllBytes(secondPath, secondBytes);

            var cache = new SourceSnapshotCache(new SourceSnapshotCacheOptions { RootPath = Path.Combine(root, "snapshots") });
            var artifacts = new RenderArtifactStore(Path.Combine(root, "artifacts"));
            var renderer = new RendererCoordinator([
                new PdfPassThroughRendererAdapter(new PdfPassThroughArtifactService(cache, artifacts))
            ]);
            var inspector = new PdfiumArtifactInspector(runtimePath, expectedHash);
            var composer = new PdfiumPageCompositionAdapter(runtimePath, expectedHash);
            var store = new SqliteRunStore(Path.Combine(root, "runs.db"));
            store.Initialize();
            var pipeline = new PreparedArtifactCoordinator(new SourceIntakeService(cache), cache, renderer,
                inspector, new VerifiedStaticFixtureInspector(), composer, artifacts, store);
            var settings = new PrintSettings(2, ColorMode.Color, DuplexMode.Simplex, "Letter",
                Orientation.Auto, ScalingMode.ShrinkToFit);

            stage = "initial preparation";
            var result = pipeline.PrepareAsync(
                [new SourceInput(firstPath), new SourceInput(secondPath), new SourceInput(firstPath, IntentionalCopy: true)],
                settings,
                new LayoutOptions(NUp: NUpMode.One, SheetSize: new PageSize(200, 200)),
                "prepared-e2e-key",
                new RetentionMetadata(FixedTime.AddDays(2), RetainWhileUnresolved: true),
                FixedTime).GetAwaiter().GetResult();

            Assert.True(result.Succeeded, string.Join("; ", result.Diagnostics));
            var run = result.Run!;
            Assert.Equal(3, run.Items.Length);
            Assert.Equal(run.Items[0].Source.Sha256, run.Items[2].Source.Sha256);
            Assert.True(run.Items[0].Source.SnapshotId != run.Items[2].Source.SnapshotId);
            Assert.Equal(4, run.FinalArtifact!.PageCount);
            Assert.Equal(1, result.Persistence!.Jobs.Length);
            Assert.True(firstBytes.SequenceEqual(File.ReadAllBytes(firstPath)));
            Assert.True(secondBytes.SequenceEqual(File.ReadAllBytes(secondPath)));

            stage = "idempotent replay";
            var retry = pipeline.PrepareAsync(
                [new SourceInput(firstPath), new SourceInput(secondPath), new SourceInput(firstPath, IntentionalCopy: true)],
                settings,
                new LayoutOptions(NUp: NUpMode.One, SheetSize: new PageSize(200, 200)),
                "prepared-e2e-key",
                new RetentionMetadata(FixedTime.AddDays(2), RetainWhileUnresolved: true),
                FixedTime.AddMinutes(1)).GetAwaiter().GetResult();
            Assert.True(retry.Succeeded);
            Assert.True(retry.Persistence!.Replayed);
            Assert.Equal(run.RunId, retry.Run!.RunId);
            stage = "concurrent idempotent replay";
            var concurrentRetries = Task.WhenAll(Enumerable.Range(0, 2).Select(_ => pipeline.PrepareAsync(
                [new SourceInput(firstPath), new SourceInput(secondPath), new SourceInput(firstPath, IntentionalCopy: true)],
                settings,
                new LayoutOptions(NUp: NUpMode.One, SheetSize: new PageSize(200, 200)),
                "prepared-e2e-key",
                new RetentionMetadata(FixedTime.AddDays(2), RetainWhileUnresolved: true),
                FixedTime.AddMinutes(2)))).GetAwaiter().GetResult();
            Assert.True(concurrentRetries.All(result => result.Succeeded && result.Persistence!.Replayed && result.Run!.RunId == run.RunId));

            stage = "persisted artifact inspection";
            var reopenedRun = store.GetPreparedRun(run.RunId)!;
            Assert.Equal(run.FinalArtifact, reopenedRun.FinalArtifact);
            using var finalPdf = artifacts.OpenRead(run.FinalArtifact.ArtifactId);
            var finalPages = inspector.GetPageSizesAsync(finalPdf, CancellationToken.None).GetAwaiter().GetResult();
            Assert.Equal(run.FinalArtifact.PageCount, finalPages.Count);

            stage = "copy layout preparation";
            var copied = pipeline.PrepareAsync([new SourceInput(firstPath)], settings,
                new LayoutOptions(Copies: 2), "copied-layout-key",
                new RetentionMetadata(FixedTime.AddDays(2), RetainWhileUnresolved: true), FixedTime.AddMinutes(1))
                .GetAwaiter().GetResult();
            Assert.True(copied.Succeeded, string.Join("; ", copied.Diagnostics));
            Assert.Equal(2, copied.Run!.FinalArtifact!.PageCount);
            Assert.Equal(1, copied.Run.Items[0].PrintSettings.Copies);
            Assert.Equal(1, copied.Run.Segments[0].PrintSettings.Copies);

            stage = "per-document layout preparation";
            var perDocument = pipeline.PrepareAsync(
                [new SourceInput(firstPath), new SourceInput(secondPath)], settings,
                new LayoutOptions(NUp: NUpMode.One), "per-document-layout-key",
                new RetentionMetadata(FixedTime.AddDays(2), RetainWhileUnresolved: true), FixedTime.AddMinutes(2),
                [settings with { Copies = 2 }, settings with { Copies = 1 }],
                [new LayoutDocumentSettings(Copies: 2), new LayoutDocumentSettings(Copies: 1)])
                .GetAwaiter().GetResult();
            Assert.True(perDocument.Succeeded, string.Join("; ", perDocument.Diagnostics));
            Assert.Equal(4, perDocument.Run!.FinalArtifact!.PageCount);
            Assert.Equal(3, perDocument.Run.Segments.Length);
            Assert.Equal(1, perDocument.Run.Items[0].PrintSettings.Copies);
            Assert.Equal(1, perDocument.Run.Items[1].PrintSettings.Copies);
            Assert.True(perDocument.Run.FinalArtifact.Sha256 != reopenedRun.FinalArtifact!.Sha256);
            Assert.True(perDocument.Run.Segments[0].ItemIds.SequenceEqual([perDocument.Run.Items[0].ItemId]));
            Assert.True(perDocument.Run.Segments[1].ItemIds.SequenceEqual([perDocument.Run.Items[1].ItemId]));
            Assert.True(perDocument.Run.Segments[2].ItemIds.SequenceEqual([perDocument.Run.Items[0].ItemId]));
            Assert.True(perDocument.Run.Segments.Select(segment => segment.FinalPageNumbers)
                .SelectMany(pages => pages).SequenceEqual([1, 2, 3, 4]));
            Assert.True(perDocument.Run.Segments[0].FinalPageNumbers.SequenceEqual([1]));
            Assert.True(perDocument.Run.Segments[1].FinalPageNumbers.SequenceEqual([2, 3]));
            Assert.True(perDocument.Run.Segments[2].FinalPageNumbers.SequenceEqual([4]));

            stage = "orientation identity preparation";
            var changedLaterProfile = pipeline.PrepareAsync(
                [new SourceInput(firstPath), new SourceInput(secondPath)], settings,
                new LayoutOptions(NUp: NUpMode.One), "per-document-orientation-key",
                new RetentionMetadata(FixedTime.AddDays(2), RetainWhileUnresolved: true), FixedTime.AddMinutes(2),
                [settings, settings with { Orientation = Orientation.Portrait }],
                [new LayoutDocumentSettings(Copies: 2), new LayoutDocumentSettings(Copies: 1)])
                .GetAwaiter().GetResult();
            Assert.True(changedLaterProfile.Succeeded, string.Join("; ", changedLaterProfile.Diagnostics));
            Assert.True(perDocument.Run.SnapshotFingerprint != changedLaterProfile.Run!.SnapshotFingerprint,
                "A later file's requested orientation is part of the prepared-run identity.");

            stage = "caller-cancelled preparation";
            using var cancelledToken = new CancellationTokenSource();
            cancelledToken.Cancel();
            var cancelled = pipeline.PrepareAsync([new SourceInput(firstPath)], settings, new LayoutOptions(),
                "cancelled-layout-key", new RetentionMetadata(FixedTime.AddDays(2), RetainWhileUnresolved: true),
                FixedTime.AddMinutes(3), cancelledToken.Token).GetAwaiter().GetResult();
            Assert.True(cancelled.IsCancelled);
            Assert.True(cancelled.Run is null);
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException(
                $"Prepared artifact test failed during stage '{stage}': {exception.GetType().Name}: {exception.Message}", exception);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private sealed class VerifiedStaticFixtureInspector : IStaticPdfContentInspector
    {
        public int Calls { get; private set; }

        public Task<bool> HasStaticPageContentAsync(Stream pdf, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            return Task.FromResult(true);
        }
    }

    private sealed class FixturePageMetadataInspector : IPdfPageMetadataInspector
    {
        public Task<int> GetPageCountAsync(Stream pdf, CancellationToken cancellationToken) => Task.FromResult(1);

        public Task<IReadOnlyList<PageSize>> GetPageSizesAsync(Stream pdf, CancellationToken cancellationToken)
        {
            using var reader = new StreamReader(pdf, System.Text.Encoding.ASCII, leaveOpen: true);
            var pdfText = reader.ReadToEnd();
            var pageCount = pdfText.Contains("/Kids [3 0 R 5 0 R 7 0 R] /Count 3", StringComparison.Ordinal) ? 3 :
                pdfText.Contains("/Kids [3 0 R 5 0 R] /Count 2", StringComparison.Ordinal) ? 2 : 1;
            pdf.Position = 0;
            return Task.FromResult<IReadOnlyList<PageSize>>(Enumerable.Repeat(new PageSize(612, 792), pageCount).ToArray());
        }
    }

    private sealed class FixtureCompositionAdapter(
        Func<int, byte[]>? outputPdfForPageCount = null,
        long maximumSourceBytes = long.MaxValue) : IPageCompositionAdapter
    {
        public long MaximumSourceBytes => maximumSourceBytes;
        public int Calls { get; private set; }
        public int LastSourceByteCount { get; private set; }

        public Task ComposePdfAsync(LayoutCompilation compilation, IEnumerable<PdfCompositionSource> sources,
            Stream destination, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            var sourceArray = sources.ToArray();
            LastSourceByteCount = sourceArray.Sum(source => source.PdfBytes.Length);
            if (outputPdfForPageCount is not null) destination.Write(outputPdfForPageCount(compilation.Pages.Length));
            else
            {
                destination.Write(sourceArray.First().PdfBytes.Span);
                destination.WriteByte((byte)' ');
            }
            return Task.CompletedTask;
        }
    }

    private sealed class UnenumeratedInputs : IEnumerable<SourceInput>
    {
        public IEnumerator<SourceInput> GetEnumerator() =>
            throw new InvalidOperationException("Preparation enumerated its inputs before validating the idempotency key.");

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private static byte[] ThreePagePdf()
    {
        var pageContent = new[] { "1 0 0 rg 10 20 20 20 re f", "0 1 0 rg 10 20 20 20 re f", "0 0 1 rg 10 20 20 20 re f" };
        var objects = new[]
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R 5 0 R 7 0 R] /Count 3 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Resources << /ProcSet [/PDF] >> /Contents 4 0 R >>",
            $"<< /Length {pageContent[0].Length} >>\nstream\n{pageContent[0]}\nendstream",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Resources << /ProcSet [/PDF] >> /Contents 6 0 R >>",
            $"<< /Length {pageContent[1].Length} >>\nstream\n{pageContent[1]}\nendstream",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Resources << /ProcSet [/PDF] >> /Contents 8 0 R >>",
            $"<< /Length {pageContent[2].Length} >>\nstream\n{pageContent[2]}\nendstream"
        };
        var builder = new System.Text.StringBuilder("%PDF-1.7\n");
        var offsets = new List<int> { 0 };
        for (var index = 0; index < objects.Length; index++)
        {
            offsets.Add(System.Text.Encoding.ASCII.GetByteCount(builder.ToString()));
            builder.Append(index + 1).Append(" 0 obj\n").Append(objects[index]).Append("\nendobj\n");
        }
        var xref = System.Text.Encoding.ASCII.GetByteCount(builder.ToString());
        builder.Append("xref\n0 ").Append(objects.Length + 1).Append("\n0000000000 65535 f \n");
        foreach (var offset in offsets.Skip(1))
            builder.Append(offset.ToString("D10", System.Globalization.CultureInfo.InvariantCulture)).Append(" 00000 n \n");
        builder.Append("trailer\n<< /Size ").Append(objects.Length + 1).Append(" /Root 1 0 R >>\nstartxref\n")
            .Append(xref).Append("\n%%EOF\n");
        return System.Text.Encoding.ASCII.GetBytes(builder.ToString());
    }
}
