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
            var concurrentRetries = Task.WhenAll(Enumerable.Range(0, 2).Select(_ => pipeline.PrepareAsync(
                [new SourceInput(firstPath), new SourceInput(secondPath), new SourceInput(firstPath, IntentionalCopy: true)],
                settings,
                new LayoutOptions(NUp: NUpMode.One, SheetSize: new PageSize(200, 200)),
                "prepared-e2e-key",
                new RetentionMetadata(FixedTime.AddDays(2), RetainWhileUnresolved: true),
                FixedTime.AddMinutes(2)))).GetAwaiter().GetResult();
            Assert.True(concurrentRetries.All(result => result.Succeeded && result.Persistence!.Replayed && result.Run!.RunId == run.RunId));

            var reopenedRun = store.GetPreparedRun(run.RunId)!;
            Assert.Equal(run.FinalArtifact, reopenedRun.FinalArtifact);
            using var finalPdf = artifacts.OpenRead(run.FinalArtifact.ArtifactId);
            var finalPages = inspector.GetPageSizesAsync(finalPdf, CancellationToken.None).GetAwaiter().GetResult();
            Assert.Equal(run.FinalArtifact.PageCount, finalPages.Count);

            var copied = pipeline.PrepareAsync([new SourceInput(firstPath)], settings,
                new LayoutOptions(Copies: 2), "copied-layout-key",
                new RetentionMetadata(FixedTime.AddDays(2), RetainWhileUnresolved: true), FixedTime.AddMinutes(1))
                .GetAwaiter().GetResult();
            Assert.True(copied.Succeeded, string.Join("; ", copied.Diagnostics));
            Assert.Equal(2, copied.Run!.FinalArtifact!.PageCount);
            Assert.Equal(1, copied.Run.Items[0].PrintSettings.Copies);
            Assert.Equal(1, copied.Run.Segments[0].PrintSettings.Copies);

            var perDocument = pipeline.PrepareAsync(
                [new SourceInput(firstPath), new SourceInput(secondPath)], settings,
                new LayoutOptions(NUp: NUpMode.One), "per-document-layout-key",
                new RetentionMetadata(FixedTime.AddDays(2), RetainWhileUnresolved: true), FixedTime.AddMinutes(2),
                [settings with { Copies = 2 }, settings with { Copies = 1 }],
                [new LayoutDocumentSettings(Copies: 2), new LayoutDocumentSettings(Copies: 1)])
                .GetAwaiter().GetResult();
            Assert.True(perDocument.Succeeded, string.Join("; ", perDocument.Diagnostics));
            Assert.Equal(4, perDocument.Run!.FinalArtifact!.PageCount);
            Assert.Equal(2, perDocument.Run.Segments.Length);
            Assert.Equal(1, perDocument.Run.Items[0].PrintSettings.Copies);
            Assert.Equal(1, perDocument.Run.Items[1].PrintSettings.Copies);
            Assert.True(perDocument.Run.FinalArtifact.Sha256 != reopenedRun.FinalArtifact!.Sha256);
            Assert.True(perDocument.Run.Segments[0].ItemIds.SequenceEqual([perDocument.Run.Items[0].ItemId]));
            Assert.True(perDocument.Run.Segments[1].ItemIds.SequenceEqual([perDocument.Run.Items[1].ItemId]));

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

            using var cancelledToken = new CancellationTokenSource();
            cancelledToken.Cancel();
            var cancelled = pipeline.PrepareAsync([new SourceInput(firstPath)], settings, new LayoutOptions(),
                "cancelled-layout-key", new RetentionMetadata(FixedTime.AddDays(2), RetainWhileUnresolved: true),
                FixedTime.AddMinutes(3), cancelledToken.Token).GetAwaiter().GetResult();
            Assert.True(cancelled.IsCancelled);
            Assert.True(cancelled.Run is null);
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
}
