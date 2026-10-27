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
        public Task<bool> HasStaticPageContentAsync(Stream pdf, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(true);
        }
    }

    private sealed class FixturePageMetadataInspector : IPdfPageMetadataInspector
    {
        public Task<int> GetPageCountAsync(Stream pdf, CancellationToken cancellationToken) => Task.FromResult(1);

        public Task<IReadOnlyList<PageSize>> GetPageSizesAsync(Stream pdf, CancellationToken cancellationToken)
        {
            using var reader = new StreamReader(pdf, System.Text.Encoding.ASCII, leaveOpen: true);
            var pageCount = reader.ReadToEnd().Contains("/Kids [3 0 R 5 0 R] /Count 2", StringComparison.Ordinal) ? 2 : 1;
            pdf.Position = 0;
            return Task.FromResult<IReadOnlyList<PageSize>>(Enumerable.Repeat(new PageSize(612, 792), pageCount).ToArray());
        }
    }

    private sealed class FixtureCompositionAdapter(Func<int, byte[]>? outputPdfForPageCount = null) : IPageCompositionAdapter
    {
        public Task ComposePdfAsync(LayoutCompilation compilation, IEnumerable<PdfCompositionSource> sources,
            Stream destination, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (outputPdfForPageCount is not null) destination.Write(outputPdfForPageCount(compilation.Pages.Length));
            else
            {
                destination.Write(sources.First().PdfBytes.Span);
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
}
