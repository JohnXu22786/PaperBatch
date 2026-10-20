using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using System.Text.Json;
using PaperBatch.Desktop;
using PaperBatch.Core.Intake;
using PaperBatch.Core.Domain;
using PaperBatch.Core.Persistence;
using PaperBatch.Core.Rendering;
using PaperBatch.Desktop.Presentation;

var tests = new (string Name, Action Run)[]
{
    ("selection refreshes preview, settings, and preflight from presentation sources", SelectionRefreshesPresentationSnapshots),
    ("navigation changes the active section", NavigationChangesTheActiveSection),
    ("queue ordering and removal are actionable and clear prepared previews", QueueEditsPreserveSelectionAndInvalidateRemovedPreview),
    ("language selection localizes desktop section labels", LanguageSelectionLocalizesSectionLabels),
    ("source intake errors remain visible and repeated imports append", SourceIntakeErrorsRemainVisibleAndAppend),
    ("cancelled renderer batch releases every captured snapshot", CancelledRendererBatchReleasesEverySnapshot),
    ("file picker reports all skipped and mixed local selections", FilePickerClassifiesSkippedSelections),
    ("headless shell identifies demo data and has no print action", HeadlessShellIsExplicitlyDemoOnly),
    ("production presentation exposes unavailable production capabilities without demo rows", ProductionPresentationIsExplicitlyUnavailable),
    ("production preview raster comes from the persisted prepared artifact", ProductionPreviewReadsPreparedArtifact),
    ("presentation assembly keeps native backends behind shared core contracts", PresentationAssemblyReferencesCoreWithoutNativeImports)
};

AppBuilder.Configure<App>()
    .UseHeadless(new AvaloniaHeadlessPlatformOptions())
    .SetupWithoutStarting();

var failures = 0;
foreach (var (name, run) in tests)
{
    try
    {
        run();
        Console.WriteLine($"PASS {name}");
    }
    catch (Exception exception)
    {
        failures++;
        Console.Error.WriteLine($"FAIL {name}: {exception.Message}");
    }
}

return failures == 0 ? 0 : 1;

static void SelectionRefreshesPresentationSnapshots()
{
    var previewSource = new FakePreviewSource();
    var settingsSource = new FakeSettingsSource();
    var preflightSource = new FakePreflightSource();
    var queue = new FakeQueueSource(
    [
        new QueueItemSnapshot("sample-a", "Sample A.pdf", "4 sample pages", "Ready · sample"),
        new QueueItemSnapshot("sample-b", "Sample B.xlsx", "2 sample pages", "Review · sample")
    ]);
    var viewModel = CreateViewModel(queue, previewSource, settingsSource, preflightSource);

    viewModel.SelectedQueueItem = viewModel.QueueItems[1];

    Assert.Equal("sample-b", previewSource.LastRequestedId);
    Assert.Equal("sample-b", settingsSource.LastRequestedId);
    Assert.Equal("sample-b", preflightSource.LastRequestedId);
    Assert.Equal("sample-b", viewModel.CurrentPreview!.ItemId);
    Assert.Equal("sample-b", viewModel.EffectiveSettings.Single().Value);
    Assert.Equal("Selected sample-b.", viewModel.PreflightFindings.Single().Detail);

    var demoViewModel = DesktopShellViewModel.CreateDemo();
    demoViewModel.SelectedQueueItem = demoViewModel.QueueItems.Single(item => item.Id == "sample-roster");
    Assert.Contains("Sample roster", demoViewModel.CurrentPreview!.Description);
    Assert.Equal(2, demoViewModel.CurrentPreview.PageCount);

    demoViewModel.SelectedQueueItem = demoViewModel.QueueItems.Single(item => item.Id == "sample-brief");
    Assert.Contains("Sample brief", demoViewModel.CurrentPreview!.Description);
    Assert.Equal(3, demoViewModel.CurrentPreview.PageCount);
}

static void QueueEditsPreserveSelectionAndInvalidateRemovedPreview()
{
    var queue = new FakeQueueSource([
        new QueueItemSnapshot("a", "A.pdf", "ready", "Ready"),
        new QueueItemSnapshot("b", "B.pdf", "ready", "Ready"),
        new QueueItemSnapshot("c", "C.pdf", "ready", "Ready")]);
    var preview = new FakePreparedPreviewSource();
    var viewModel = CreateViewModel(queue, preview, new FakeSettingsSource(), new FakePreflightSource());
    viewModel.SelectedQueueItem = viewModel.QueueItems[1];
    viewModel.MoveSelectedQueueItem(-1);
    Assert.True(preview.InvalidatedIds.Contains("a") && preview.InvalidatedIds.Contains("b") && preview.InvalidatedIds.Contains("c"));
    Assert.Equal("b", viewModel.QueueItems[0].Id);
    Assert.Equal("b", viewModel.SelectedQueueItem!.Id);
    viewModel.RemoveSelectedQueueItem();
    Assert.Equal(2, viewModel.QueueItems.Count);
    Assert.True(preview.InvalidatedIds.Contains("b"));
    Assert.Equal("a", viewModel.SelectedQueueItem!.Id);
}

static void LanguageSelectionLocalizesSectionLabels()
{
    var viewModel = DesktopShellViewModel.CreateProduction();
    viewModel.SetLanguage("zh-CN");
    Assert.Equal("队列与审核", viewModel.SectionQueueLabel);
    Assert.Equal("历史记录", viewModel.SectionHistoryLabel);
    Assert.Contains("不可用", viewModel.HistoryTitle);

    var demo = DesktopShellViewModel.CreateDemo();
    demo.SetLanguage("zh-CN");
    Assert.Equal("示例运行 A", demo.HistoryEntries[0].Title);
    Assert.Equal("示例课堂资料包", demo.Templates[0].Name);
}

static void SourceIntakeErrorsRemainVisibleAndAppend()
{
    var cacheRoot = Path.Combine(Path.GetTempPath(), "paperbatch-desktop-tests", Guid.NewGuid().ToString("N"));
    var cache = new SourceSnapshotCache(new SourceSnapshotCacheOptions { RootPath = cacheRoot });
    var sources = new ProductionPresentationSources(new SourceIntakeService(cache), new RendererCoordinator([]), cache);
    var viewModel = new DesktopShellViewModel(sources, sources, sources, sources, sources, sources,
        new WorkspaceStatusSnapshot("PRODUCTION MODE", "Test", false));
    try
    {
        viewModel.ImportFilesAsync([Path.Combine(cacheRoot, "missing-a.pdf")]).GetAwaiter().GetResult();
        viewModel.ImportFilesAsync([Path.Combine(cacheRoot, "missing-b.pdf")]).GetAwaiter().GetResult();

        Assert.Equal(2, viewModel.QueueItems.Count);
        Assert.Equal("Needs attention", viewModel.QueueItems[0].Status);
        Assert.Equal("InputVanished", viewModel.PreflightFindings.Single().Title);
        Assert.Equal(2, viewModel.QueueItems.Count);
        Assert.Equal("2 ITEMS IN QUEUE", viewModel.QueueStateLabel);
        Assert.True(viewModel.CanImportFiles);
    }
    finally
    {
        if (Directory.Exists(cacheRoot)) Directory.Delete(cacheRoot, recursive: true);
    }
}

static void CancelledRendererBatchReleasesEverySnapshot()
{
    var root = Path.Combine(Path.GetTempPath(), "paperbatch-desktop-tests", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root);
    var cacheRoot = Path.Combine(root, "cache");
    var firstPath = Path.Combine(root, "first.pdf");
    var secondPath = Path.Combine(root, "second.pdf");
    File.WriteAllBytes(firstPath, MinimalPdf());
    File.WriteAllBytes(secondPath, MinimalPdf());
    var cache = new SourceSnapshotCache(new SourceSnapshotCacheOptions { RootPath = cacheRoot });
    using var cancellation = new CancellationTokenSource();
    var renderer = new RendererCoordinator([new CancellingRendererAdapter(cancellation)]);
    var sources = new ProductionPresentationSources(new SourceIntakeService(cache), renderer, cache, rendererAvailable: true);
    try
    {
        Assert.Throws<OperationCanceledException>(() => sources.ImportAsync([firstPath, secondPath], cancellation.Token)
            .GetAwaiter().GetResult());
        var metadata = Directory.EnumerateDirectories(cacheRoot, "snap_*", SearchOption.TopDirectoryOnly)
            .Select(directory => JsonDocument.Parse(File.ReadAllBytes(Path.Combine(directory, "metadata.json"))))
            .ToArray();
        Assert.Equal(2, metadata.Length);
        Assert.True(metadata.All(document => !document.RootElement.GetProperty("Retained").GetBoolean()));
        foreach (var document in metadata) document.Dispose();
    }
    finally
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }
}

static byte[] MinimalPdf()
{
    var objects = new[]
    {
        "<< /Type /Catalog /Pages 2 0 R >>",
        "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Resources << /ProcSet [/PDF] >> /Contents 4 0 R >>",
        "<< /Length 0 >>\nstream\n\nendstream"
    };
    using var output = new MemoryStream();
    var header = System.Text.Encoding.ASCII.GetBytes("%PDF-1.7\n");
    output.Write(header);
    var offsets = new List<long> { 0 };
    foreach (var item in objects)
    {
        var objectNumber = offsets.Count;
        offsets.Add(output.Position);
        var bytes = System.Text.Encoding.ASCII.GetBytes($"{objectNumber} 0 obj\n{item}\nendobj\n");
        output.Write(bytes);
    }
    var xref = output.Position;
    using var writer = new StreamWriter(output, System.Text.Encoding.ASCII, 1024, leaveOpen: true);
    writer.Write("xref\n0 5\n0000000000 65535 f \n");
    foreach (var offset in offsets.Skip(1)) writer.Write(offset.ToString("D10", System.Globalization.CultureInfo.InvariantCulture) + " 00000 n \n");
    writer.Write($"trailer\n<< /Size 5 /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
    writer.Flush();
    return output.ToArray();
}

static void FilePickerClassifiesSkippedSelections()
{
    var allSkipped = MainWindow.ClassifySelectedPaths([null, "", "   "]);
    Assert.Empty(allSkipped.Paths);
    Assert.Equal(3, allSkipped.SkippedCount);
    Assert.Contains("3 selected item(s)", allSkipped.SkippedMessage(noItemsAdded: true));
    Assert.Contains("No items were added", allSkipped.SkippedMessage(noItemsAdded: true));

    var mixed = MainWindow.ClassifySelectedPaths(["/tmp/accepted.pdf", null]);
    Assert.Equal("/tmp/accepted.pdf", mixed.Paths.Single());
    Assert.Equal(1, mixed.SkippedCount);
    Assert.Contains("1 selected item(s)", mixed.SkippedMessage(noItemsAdded: false));
    Assert.True(!mixed.SkippedMessage(noItemsAdded: false).Contains("No items were added", StringComparison.Ordinal));
}

static void NavigationChangesTheActiveSection()
{
    var viewModel = CreateViewModel(new FakeQueueSource([]), new FakePreviewSource(), new FakeSettingsSource(), new FakePreflightSource());

    viewModel.NavigateCommand.Execute("History");
    Assert.Equal(ShellSection.History, viewModel.ActiveSection);
    Assert.True(viewModel.IsHistorySection);

    viewModel.NavigateCommand.Execute("Templates");
    Assert.Equal(ShellSection.Templates, viewModel.ActiveSection);
    Assert.True(viewModel.IsTemplatesSection);

    viewModel.NavigateCommand.Execute("Queue");
    Assert.Equal(ShellSection.Queue, viewModel.ActiveSection);
}

static void HeadlessShellIsExplicitlyDemoOnly()
{
    var viewModel = DesktopShellViewModel.CreateDemo();
    var window = new MainWindow(viewModel);
    try
    {
        window.Show();

        var banner = window.FindControl<Border>("SampleDataBanner");
        Assert.NotNull(banner);
        var bannerText = string.Join(" ", ((StackPanel)banner!.Child!).Children.OfType<TextBlock>().Select(textBlock => textBlock.Text));
        Assert.Contains("DEMO MODE", bannerText);
        Assert.Contains("Synthetic sample data only", bannerText);
        Assert.Equal("DEMO PREVIEW", window.FindControl<TextBlock>("PreviewHeading")!.Text);
        Assert.Equal("Illustrative sheet face", window.FindControl<TextBlock>("PreviewDescription")!.Text);
        Assert.Equal("3 ITEMS IN QUEUE", viewModel.QueueStateLabel);
        Assert.Contains("sample preview", window.FindControl<TextBlock>("QueueDescription")!.Text);
        Assert.Null(window.FindControl<Button>("PrintButton"));
        viewModel.NavigateCommand.Execute("History");
        Assert.Equal("Example activity", window.FindControl<TextBlock>("HistoryTitle")!.Text);
        viewModel.NavigateCommand.Execute("Templates");
        Assert.Equal("Example recipes", window.FindControl<TextBlock>("TemplateTitle")!.Text);
    }
    finally
    {
        window.Close();
    }
}

static void ProductionPresentationIsExplicitlyUnavailable()
{
    var sources = new ProductionPresentationSources();
    Assert.Empty(sources.GetQueueItems());
            Assert.Contains("unavailable", sources.GetPreviewAsync("missing").GetAwaiter().GetResult().Description);
    Assert.Contains("No renderer", sources.GetFindings("missing").Single().Detail);
    Assert.Empty(sources.GetHistoryEntries());
    Assert.Empty(sources.GetTemplates());

    var viewModel = new DesktopShellViewModel(sources, sources, sources, sources, sources, sources,
        new WorkspaceStatusSnapshot("PRODUCTION MODE", "Test", false));
    var window = new MainWindow(viewModel);
    try
    {
        window.Show();
        Assert.Equal("QUEUE EMPTY", viewModel.QueueStateLabel);
        Assert.Contains("Source files are captured", window.FindControl<TextBlock>("QueueDescription")!.Text);
        Assert.Equal("FINAL PREVIEW UNAVAILABLE", window.FindControl<TextBlock>("PreviewHeading")!.Text);
        Assert.Equal("No prepared artifact preview is available.", window.FindControl<TextBlock>("PreviewDescription")!.Text);
        viewModel.NavigateCommand.Execute("History");
        Assert.Equal("Print history unavailable", window.FindControl<TextBlock>("HistoryTitle")!.Text);
        viewModel.NavigateCommand.Execute("Templates");
        Assert.Equal("Saved recipes unavailable", window.FindControl<TextBlock>("TemplateTitle")!.Text);
    }
    finally
    {
        window.Close();
    }
}

static void ProductionPreviewReadsPreparedArtifact()
{
    var root = Path.Combine(Path.GetTempPath(), "paperbatch-desktop-preview-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root);
    try
    {
        var artifacts = new RenderArtifactStore(Path.Combine(root, "artifacts"));
        var pdfBytes = MinimalPdf();
        var stored = artifacts.PersistPdfAsync(new MemoryStream(pdfBytes), "test-renderer", "1",
            new Dictionary<string, string>()).GetAwaiter().GetResult();
        var final = new FinalPdfArtifact(stored.Artifact.ArtifactId, stored.Artifact.Sha256, stored.Artifact.LengthBytes, 1);
        var settings = new PrintSettings(1, ColorMode.Color, DuplexMode.Simplex, "Letter", Orientation.Auto, ScalingMode.ShrinkToFit);
        var item = new PreparedItem("item", new SourceSnapshot("snapshot", new string('a', 64), 1, DateTimeOffset.UtcNow),
            new RendererResult("renderer", "1", "source-artifact", new string('b', 64), 1, 1,
                new RendererCapabilities(1, ["application/pdf"])),
            [new PageMapping(1, 1)], settings);
        var run = PreparedRun.Create("run-preview", DateTimeOffset.UtcNow, new string('c', 64), [item],
            [new CompatibleSegment("segment", 0, [item.ItemId], "compat", settings)], final);
        var preview = new FakeArtifactPreviewRenderer(pdfBytes);
        var sources = new ProductionPresentationSources(artifacts: artifacts, previewRenderer: preview);
        var viewModel = new DesktopShellViewModel(new FakeQueueSource([new QueueItemSnapshot("queue-item", "prepared.pdf", "Ready", "Prepared")]),
            sources, sources, sources, sources, sources, new WorkspaceStatusSnapshot("PRODUCTION MODE", "Test", false));
        viewModel.RegisterPreparedRun("queue-item", run);
        viewModel.RefreshPreviewAsync().GetAwaiter().GetResult();
        var window = new MainWindow(viewModel);
        try
        {
            window.Show();
            Assert.True(viewModel.HasPreviewImage);
            Assert.Equal(final.ArtifactId, viewModel.CurrentPreview!.ArtifactId);
            Assert.Equal(final.Sha256, viewModel.CurrentPreview.ArtifactSha256);
            Assert.True(pdfBytes.SequenceEqual(preview.ObservedPdfBytes!));
            Assert.True(ReferenceEquals(viewModel.CurrentPreview.PageImage,
                window.FindControl<Image>("PreparedArtifactPageImage")!.Source));
            Assert.Contains("immutable final PDF", viewModel.ProductionPreviewStatus);
            viewModel.InvalidatePreparedRun("queue-item");
            Assert.True(!viewModel.HasPreviewImage);
            Assert.Equal("FINAL PREVIEW UNAVAILABLE", viewModel.PreviewHeading);
        }
        finally { window.Close(); }
    }
    finally
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }
}

static void PresentationAssemblyReferencesCoreWithoutNativeImports()
{
    var assembly = typeof(DesktopShellViewModel).Assembly;
    string[] forbiddenReferenceTokens =
    [
        "System.Printing",
        "System.Management",
        "Microsoft.Data.Sqlite",
        "Microsoft.Office",
        "Pdfium",
        "PaperBatch.Storage"
    ];
    var references = assembly.GetReferencedAssemblies().Select(reference => reference.Name ?? string.Empty).ToArray();
    Assert.True(references.Any(reference => reference == "PaperBatch.Core"));
    var forbiddenReferences = references.Where(reference => forbiddenReferenceTokens.Any(token => reference.Contains(token, StringComparison.OrdinalIgnoreCase))).ToArray();
    Assert.Empty(forbiddenReferences);

    var nativeImports = assembly.GetTypes()
        .SelectMany(type => type.GetMethods(System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic))
        .Where(method => method.GetCustomAttributesData().Any(attribute =>
            attribute.AttributeType.FullName is "System.Runtime.InteropServices.DllImportAttribute" or "System.Runtime.InteropServices.LibraryImportAttribute"))
        .ToArray();
    Assert.Empty(nativeImports);
}

static DesktopShellViewModel CreateViewModel(
    IQueuePresentationSource queueSource,
    IFinalPreviewPresentationSource previewSource,
    IEffectiveSettingsPresentationSource settingsSource,
    IPreflightPresentationSource preflightSource) =>
    new(
        queueSource,
        settingsSource,
        previewSource,
        preflightSource,
        new FakeHistorySource(),
        new FakeTemplateSource());

sealed class FakeQueueSource(IReadOnlyList<QueueItemSnapshot> items) : IQueuePresentationSource
{
    public IReadOnlyList<QueueItemSnapshot> GetQueueItems() => items;
}

sealed class FakePreparedPreviewSource : IFinalPreviewPresentationSource, IPreparedArtifactPresentationSource
{
    public event Action<string>? PreparedArtifactChanged { add { } remove { } }
    public List<string> InvalidatedIds { get; } = [];
    public void RegisterPreparedRun(string queueItemId, PaperBatch.Core.Domain.PreparedRun run) { }
    public void InvalidatePreparedRun(string queueItemId) => InvalidatedIds.Add(queueItemId);
    public Task<PreviewSnapshot> GetPreviewAsync(string queueItemId, CancellationToken cancellationToken = default) =>
        Task.FromResult(new PreviewSnapshot(queueItemId, "Preview", 1, 1));
}

sealed class FakePreviewSource : IFinalPreviewPresentationSource
{
    public string? LastRequestedId { get; private set; }

    public Task<PreviewSnapshot> GetPreviewAsync(string queueItemId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        LastRequestedId = queueItemId;
        return Task.FromResult(new PreviewSnapshot(queueItemId, "Synthetic page-face illustration", 1, 4));
    }
}

sealed class FakeSettingsSource : IEffectiveSettingsPresentationSource
{
    public string? LastRequestedId { get; private set; }

    public IReadOnlyList<EffectiveSettingSnapshot> GetEffectiveSettings(string queueItemId)
    {
        LastRequestedId = queueItemId;
        return [new EffectiveSettingSnapshot("Sample selection", queueItemId, "Injected test snapshot")];
    }
}

sealed class FakePreflightSource : IPreflightPresentationSource
{
    public string? LastRequestedId { get; private set; }

    public IReadOnlyList<PreflightFindingSnapshot> GetFindings(string queueItemId)
    {
        LastRequestedId = queueItemId;
        return [new PreflightFindingSnapshot("Source files", "Demo only", $"Selected {queueItemId}.")];
    }
}

sealed class FakeHistorySource : IHistoryPresentationSource
{
    public IReadOnlyList<HistoryEntrySnapshot> GetHistoryEntries() => [];
}

sealed class FakeTemplateSource : ITemplatePresentationSource
{
    public IReadOnlyList<TemplateSnapshot> GetTemplates() => [];
}

static class Assert
{
    public static void True(bool condition)
    {
        if (!condition)
        {
            throw new InvalidOperationException("Expected true.");
        }
    }

    public static void Null(object? value)
    {
        if (value is not null)
        {
            throw new InvalidOperationException("Expected null.");
        }
    }

    public static void NotNull(object? value)
    {
        if (value is null)
        {
            throw new InvalidOperationException("Expected a non-null value.");
        }
    }

    public static void Contains(string expected, string? actual)
    {
        if (actual?.Contains(expected, StringComparison.Ordinal) != true)
        {
            throw new InvalidOperationException($"Expected '{actual}' to contain '{expected}'.");
        }
    }

    public static void Empty<T>(IReadOnlyCollection<T> values)
    {
        if (values.Count != 0)
        {
            throw new InvalidOperationException($"Expected empty collection, got {values.Count} items.");
        }
    }

    public static void Throws<TException>(Action action) where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException)
        {
            return;
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException($"Expected {typeof(TException).Name}, got {exception.GetType().Name}.", exception);
        }
        throw new InvalidOperationException($"Expected {typeof(TException).Name}, but no exception was thrown.");
    }

    public static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException($"Expected '{expected}', got '{actual}'.");
        }
    }
}


sealed class CancellingRendererAdapter(CancellationTokenSource cancellation) : IRendererAdapter
{
    public ValueTask<bool> CanRenderAsync(RenderRequest request, CancellationToken cancellationToken) =>
        ValueTask.FromResult(request.Format == SourceFormat.Pdf && request.ExecutionContext == RendererExecutionContext.BackgroundWorker);

    public Task<RenderResult> RenderAsync(RenderRequest request, CancellationToken cancellationToken)
    {
        cancellation.Cancel();
        return Task.FromResult(RenderResult.Unsupported("Cancellation cleanup fixture."));
    }
}

sealed class FakeArtifactPreviewRenderer(byte[] expectedBytes) : IPdfArtifactPreviewRenderer
{
    public byte[]? ObservedPdfBytes { get; private set; }

    public Task<PdfRasterPage> RenderPageAsync(Stream pdf, int pageNumber, int maximumDimension,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var content = new MemoryStream();
        pdf.CopyTo(content);
        ObservedPdfBytes = content.ToArray();
        if (!ObservedPdfBytes.SequenceEqual(expectedBytes)) throw new InvalidDataException("Preview received different PDF bytes.");
        Assert.Equal(1, pageNumber);
        Assert.Equal(1600, maximumDimension);
        return Task.FromResult(new PdfRasterPage(1, 1, 4, [0, 0, 0, 255]));
    }
}
