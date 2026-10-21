using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using System.Text.Json;
using System.Text;
using PaperBatch.Desktop;
using PaperBatch.Core.Intake;
using PaperBatch.Core.Domain;
using PaperBatch.Core.Persistence;
using PaperBatch.Core.Rendering;
using PaperBatch.Core.Preparation;
using PaperBatch.Core.Recipes;
using PaperBatch.Core.Layout;
using PaperBatch.Desktop.Presentation;

var tests = new (string Name, Action Run)[]
{
    ("selection refreshes preview, settings, and preflight from presentation sources", SelectionRefreshesPresentationSnapshots),
    ("navigation changes the active section", NavigationChangesTheActiveSection),
    ("printer inspection is explicit, read-only, and keeps demo mode disabled", PrinterInspectionIsReadOnly),
    ("print status and cancellation failures remain visible and block unsafe submission", PrintStatusFailuresAreVisibleAndBlockSubmission),
    ("queue ordering and removal are actionable and clear prepared previews", QueueEditsPreserveSelectionAndInvalidateRemovedPreview),
    ("language selection localizes desktop section labels", LanguageSelectionLocalizesSectionLabels),
    ("source intake errors remain visible and repeated imports append", SourceIntakeErrorsRemainVisibleAndAppend),
    ("intentional duplicate sources have independent queue row identities", IntentionalDuplicateQueueRowsRemainIndependent),
    ("cancelled renderer batch releases every captured snapshot", CancelledRendererBatchReleasesEverySnapshot),
    ("file picker reports all skipped and mixed local selections", FilePickerClassifiesSkippedSelections),
    ("headless shell identifies demo data and has no print action", HeadlessShellIsExplicitlyDemoOnly),
    ("production presentation exposes unavailable production capabilities without demo rows", ProductionPresentationIsExplicitlyUnavailable),
    ("production preview raster comes from the persisted prepared artifact", ProductionPreviewReadsPreparedArtifact),
    ("source changes after queue preflight stop before final rendering or persistence", ChangedSourceAfterPreflightCreatesNoPreparedArtifact),
    ("preparation controls forward requested layout settings and invalidate previews", PreparationControlsForwardRequestedSettingsAndInvalidate),
    ("preparation errors follow the selected language", PreparationErrorsFollowSelectedLanguage),
    ("recipe device-profile conflicts do not persist or compose an artifact", RecipeDeviceConflictCreatesNoRunOrFinalArtifact),
    ("recipe duplex requests change compiled physical-face layout while remaining unvalidated", RecipeDuplexRequestChangesCompiledLayout),
    ("corrupt saved recipes leave production shell available with an error state", CorruptSavedRecipesRemainVisibleAsAnError),
    ("recipe lifecycle strictly imports, renames, exports without overwrite, and deletes", RecipeLifecycleIsExplicitAndBounded),
    ("corrupt saved history leaves production shell available with an error state", CorruptSavedHistoryRemainsVisibleAsAnError),
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
    Assert.True(!viewModel.MoveQueueItemCommand.CanExecute(-1));
    Assert.True(viewModel.MoveQueueItemCommand.CanExecute(1));
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
    Assert.Contains("已保存的运行记录", viewModel.HistoryTitle);

    var demo = DesktopShellViewModel.CreateDemo();
    demo.SetLanguage("zh-CN");
    Assert.Equal("示例运行 A", demo.HistoryEntries[0].Title);
    Assert.Equal("示例课堂资料包", demo.Templates[0].Name);
}

static void PrinterInspectionIsReadOnly()
{
    Assert.Equal("驱动未报告双面能力。", ProductionPresentationSources.FormatCapabilityDetail(
        "The driver did not report duplex capability.", chinese: true));
    Assert.Equal("驱动报告支持双面，但未报告具体翻转方式。", ProductionPresentationSources.FormatCapabilityDetail(
        "The driver reports duplex support but does not report exact binding modes.", chinese: true));
    Assert.Equal("驱动文本：Vendor-specific explanation", ProductionPresentationSources.FormatCapabilityDetail(
        "Vendor-specific explanation", chinese: true));
    Assert.Equal("驱动返回的纸盒过多，无法安全检查。", ProductionPresentationSources.FormatCapabilityDetail(
        "The driver returned too many trays to inspect safely.", chinese: true));
    Assert.Equal("驱动报告不支持纸盒。", ProductionPresentationSources.FormatCapabilityDetail(
        "The driver reports no trays.", chinese: true));
    Assert.Equal("驱动未返回纸张类型。", ProductionPresentationSources.FormatCapabilityDetail(
        "The driver did not return media types.", chinese: true));
    Assert.Equal("驱动返回的分辨率过多，无法安全检查。", ProductionPresentationSources.FormatCapabilityDetail(
        "The driver returned too many resolutions to inspect safely.", chinese: true));
    Assert.Equal("驱动报告了纸张名称，但无法将其与原生标识符准确对应。", ProductionPresentationSources.FormatCapabilityDetail(
        "The driver reported Media names but its native identifiers could not be mapped exactly.", chinese: true));
    Assert.Equal(" (Driver-reported values: Vendor A, Vendor B)", ProductionPresentationSources.FormatCapabilityValues(
        ["Vendor A", "Vendor B"], chinese: false));
    Assert.Equal("（驱动报告值：厂商纸张）", ProductionPresentationSources.FormatCapabilityValues(
        ["厂商纸张"], chinese: true));
    var source = new FakePrinterCapabilitySource();
    var production = new DesktopShellViewModel(new FakeQueueSource([]), new FakeSettingsSource(),
        new FakePreviewSource(), new FakePreflightSource(), new FakeHistorySource(), new FakeTemplateSource(),
        new WorkspaceStatusSnapshot("PRODUCTION MODE", "test", false));
    var attached = new DesktopShellViewModel(source, new FakeSettingsSource(), new FakePreviewSource(),
        new FakePreflightSource(), new FakeHistorySource(), new FakeTemplateSource(),
        new WorkspaceStatusSnapshot("PRODUCTION MODE", "test", false));
    Assert.True(!production.CanInspectPrinterCapabilities);
    Assert.True(!production.IsPrinterInspectionSectionVisible);
    Assert.True(!production.NavigateCommand.CanExecute("Printers"));
    Assert.True(attached.CanInspectPrinterCapabilities);
    attached.NavigateCommand.Execute("Printers");
    Assert.True(attached.IsPrintersSection);
    var callerThreadId = Environment.CurrentManagedThreadId;
    Task.Run(async () => await attached.RefreshPrinterCapabilitiesAsync()).GetAwaiter().GetResult();
    Assert.Equal(1, source.ReadCalls);
    Assert.True(source.ReadThreadId != callerThreadId);
    Assert.Equal("Sample Queue", attached.PrinterQueues.Single().DisplayName);
    Assert.Equal("Queue: Sample Queue", attached.PrinterQueues.Single().QueueDisplay);
    Assert.Equal("Driver: Sample Driver", attached.PrinterQueues.Single().DriverDisplay);
    Assert.Contains("Media: Supported", attached.PrinterQueues.Single().Details);
    Assert.Contains("inventory only", attached.PrinterCapabilityStatus);
    attached.SetLanguage("zh-CN");
    Assert.Equal("打印机", attached.SectionPrintersLabel);
    Assert.Equal("队列：Sample Queue", attached.PrinterQueues.Single().QueueDisplay);
    Assert.Equal("驱动：Sample Driver", attached.PrinterQueues.Single().DriverDisplay);
    Assert.Contains("纸张：支持", attached.PrinterQueues.Single().Details);
    Assert.Contains("端口：", attached.PrinterQueues.Single().Details);
    Assert.Contains("驱动未报告双面能力。", attached.PrinterQueues.Single().Details);
    Assert.True(!attached.PrinterQueues.Single().Details.Contains("The driver did not report duplex capability.", StringComparison.Ordinal));
    Assert.Contains("已读取 1 个本地队列", attached.PrinterCapabilityStatus);
    var demo = DesktopShellViewModel.CreateDemo();
    Assert.True(!demo.CanInspectPrinterCapabilities);
    Assert.True(!demo.IsPrinterInspectionSectionVisible);
    Assert.True(!demo.RefreshPrinterCapabilitiesCommand.CanExecute(null));
    Assert.True(!demo.NavigateCommand.CanExecute("Printers"));
    demo.NavigateCommand.Execute("Printers");
    Assert.True(!demo.IsPrintersSection);

    var delayedSource = new FakePrinterCapabilitySource { DelayRead = true };
    var pending = new DesktopShellViewModel(delayedSource, new FakeSettingsSource(), new FakePreviewSource(),
        new FakePreflightSource(), new FakeHistorySource(), new FakeTemplateSource(),
        new WorkspaceStatusSnapshot("PRODUCTION MODE", "test", false));
    var firstRead = Task.Run(async () =>
    {
        await pending.RefreshPrinterCapabilitiesAsync();
    });
    Assert.True(SpinWait.SpinUntil(() => delayedSource.ReadCalls == 1, TimeSpan.FromSeconds(5)));
    Assert.True(pending.IsPrinterCapabilityRefreshInProgress);
    Assert.True(!pending.RefreshPrinterCapabilitiesCommand.CanExecute(null));
    pending.RefreshPrinterCapabilitiesAsync().GetAwaiter().GetResult();
    var readCallsBeforeCompletion = delayedSource.ReadCalls;
    delayedSource.CompleteRead();
    firstRead.GetAwaiter().GetResult();
    Assert.Equal(1, readCallsBeforeCompletion);
    Assert.Equal(1, delayedSource.ReadCalls);
    Assert.Equal(1, pending.PrinterQueues.Count);
    Assert.True(!pending.IsPrinterCapabilityRefreshInProgress);
}

static void PrintStatusFailuresAreVisibleAndBlockSubmission()
{
    var source = new FakeUnavailablePrintSource([new QueueItemSnapshot("queue-item", "Prepared.pdf", "Ready", "Prepared")]);
    var viewModel = new DesktopShellViewModel(source, new FakeSettingsSource(), new FakePreviewSource(),
        new FakePreflightSource(), new FakeHistorySource(), new FakeTemplateSource(),
        new WorkspaceStatusSnapshot("PRODUCTION MODE", "Test", false));

    Assert.Contains("Print status unavailable", viewModel.OperationError);
    Assert.True(!viewModel.CanSubmitPreparedRun);
    viewModel.CancelPrintJobAsync("owned-job").GetAwaiter().GetResult();
    Assert.Contains("Print cancellation could not be confirmed", viewModel.OperationError);
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
        var mixedImport = viewModel.ImportFilesAsync(["bad\0path.pdf", Path.Combine(cacheRoot, "missing-b.pdf")]);
        mixedImport.GetAwaiter().GetResult();

        Assert.Equal(3, viewModel.QueueItems.Count);
        Assert.Equal("Needs attention", viewModel.QueueItems[0].Status);
        var invalidRow = viewModel.QueueItems.Single(item => item.Title == "bad\0path.pdf");
        var missingRow = viewModel.QueueItems.Single(item => item.Title == "missing-b.pdf");
        Assert.Equal("InvalidPath", sources.GetFindings(invalidRow.Id).Single().Title);
        Assert.Equal("InputVanished", sources.GetFindings(missingRow.Id).Single().Title);
        Assert.Equal(3, viewModel.QueueItems.Count);
        Assert.Equal("3 ITEMS IN QUEUE", viewModel.QueueStateLabel);
        Assert.True(viewModel.CanImportFiles);
    }
    finally
    {
        if (Directory.Exists(cacheRoot)) Directory.Delete(cacheRoot, recursive: true);
    }
}

static void IntentionalDuplicateQueueRowsRemainIndependent()
{
    var root = Path.Combine(Path.GetTempPath(), "paperbatch-duplicate-queue-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root);
    try
    {
        var path = Path.Combine(root, "same.pdf");
        File.WriteAllBytes(path, TestOnePagePdf());
        var cache = new SourceSnapshotCache(new SourceSnapshotCacheOptions { RootPath = Path.Combine(root, "snapshots") });
        var artifacts = new RenderArtifactStore(Path.Combine(root, "artifacts"));
        var renderer = new RendererCoordinator([
            new PdfPassThroughRendererAdapter(new PdfPassThroughArtifactService(cache, artifacts))
        ]);
        var sources = new ProductionPresentationSources(new SourceIntakeService(cache), renderer, cache,
            rendererAvailable: true, artifacts: artifacts);

        var imported = sources.ImportAsync([path, path]).GetAwaiter().GetResult();

        Assert.Equal(2, imported.Count);
        Assert.True(imported[0].Id != imported[1].Id);
        sources.Remove(imported[0].Id);
        Assert.Equal(1, sources.GetQueueItems().Count);
        Assert.Equal(imported[1].Id, sources.GetQueueItems()[0].Id);
    }
    finally
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
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

    var failedViewModel = new DesktopShellViewModel(sources, sources, sources, sources, sources, sources,
        new WorkspaceStatusSnapshot("PRODUCTION MODE · INITIALIZATION FAILED", "Local storage could not initialize.", false,
            InitializationFailed: true));
    Assert.Equal("History unavailable", failedViewModel.HistoryTitle);
    Assert.Equal("Recipes unavailable", failedViewModel.TemplateTitle);
    Assert.True(!failedViewModel.CanManageRecipes);
    Assert.Contains("could not initialize", failedViewModel.HistoryDescription);

    var viewModel = new DesktopShellViewModel(sources, sources, sources, sources, sources, sources,
        new WorkspaceStatusSnapshot("PRODUCTION MODE", "Test", false));
    var window = new MainWindow(viewModel);
    try
    {
        window.Show();
        Assert.Equal("QUEUE EMPTY", viewModel.QueueStateLabel);
        Assert.Contains("Sources are captured", window.FindControl<TextBlock>("QueueDescription")!.Text);
        Assert.Equal("FINAL PREVIEW UNAVAILABLE", window.FindControl<TextBlock>("PreviewHeading")!.Text);
        Assert.Equal("No prepared artifact preview is available.", window.FindControl<TextBlock>("PreviewDescription")!.Text);
        viewModel.NavigateCommand.Execute("History");
        Assert.Equal("No saved runs yet", window.FindControl<TextBlock>("HistoryTitle")!.Text);
        viewModel.NavigateCommand.Execute("Templates");
        Assert.Equal("No recipes saved", window.FindControl<TextBlock>("TemplateTitle")!.Text);
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
        var store = new SqliteRunStore(Path.Combine(root, "runs.db"));
        store.Initialize();
        store.CreateRun(run, "coordinator-request-key", new RetentionMetadata(run.CreatedAtUtc.AddDays(30), true));
        store.SaveRecipe(new JobRecipe(1, "recipe-1", "Saved packet", [new RecipeSource("source-1", "/tmp/a.pdf")],
            new RecipePrintSettings(1, ColorMode.Color, DuplexMode.Simplex, Orientation.Auto, ScalingMode.ShrinkToFit, "Letter")));
        var sources = new ProductionPresentationSources(artifacts: artifacts, previewRenderer: preview, runStore: store);
        var viewModel = new DesktopShellViewModel(new FakeQueueSource([new QueueItemSnapshot("queue-item", "prepared.pdf", "Ready", "Prepared")]),
            sources, sources, sources, sources, sources, new WorkspaceStatusSnapshot("PRODUCTION MODE", "Test", false));
        viewModel.RegisterPreparedRun("queue-item", run);
        Assert.Equal("run-preview", store.GetRecentRuns().Single().RunId);
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
            viewModel.NavigateCommand.Execute("History");
            Assert.Equal("Saved run history", viewModel.HistoryTitle);
            Assert.Equal("Ready", viewModel.HistoryEntries.Single().Status);
            viewModel.NavigateCommand.Execute("Templates");
            Assert.Equal("Saved recipes", viewModel.TemplateTitle);
            Assert.Contains("not applied", viewModel.Templates.Single().SettingsSummary);
            viewModel.SetLanguage("zh-CN");
            Assert.Contains("个来源", viewModel.Templates.Single().Summary);
            Assert.Contains("未应用", viewModel.Templates.Single().SettingsSummary);
            Assert.Equal("从队列创建…", viewModel.CreateRecipeLabel);
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

static void ChangedSourceAfterPreflightCreatesNoPreparedArtifact()
{
    var root = Path.Combine(Path.GetTempPath(), "paperbatch-changed-source-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root);
    try
    {
        var path = Path.Combine(root, "source.pdf");
        File.WriteAllBytes(path, TestOnePagePdf());
        var cache = new SourceSnapshotCache(new SourceSnapshotCacheOptions { RootPath = Path.Combine(root, "snapshots") });
        var artifacts = new RenderArtifactStore(Path.Combine(root, "artifacts"));
        var renderer = new RendererCoordinator([new PdfPassThroughRendererAdapter(new PdfPassThroughArtifactService(cache, artifacts))]);
        var intake = new SourceIntakeService(cache);
        var store = new SqliteRunStore(Path.Combine(root, "runs.db"));
        store.Initialize();
        var composer = new ConflictTestComposer();
        var coordinator = new PreparedArtifactCoordinator(intake, cache, renderer, new ConflictTestInspector(),
            new ConflictTestInspector(), composer, artifacts, store);
        var sources = new ProductionPresentationSources(intake, renderer, cache, rendererAvailable: true,
            artifacts: artifacts, runStore: store, preparation: coordinator);
        var queueItem = sources.ImportAsync([path]).GetAwaiter().GetResult().Single();
        File.AppendAllText(path, "\n% changed after preflight\n");

        var result = sources.PrepareQueueAsync(
            new PrintSettings(1, ColorMode.Color, DuplexMode.Simplex, "Letter", Orientation.Auto, ScalingMode.ShrinkToFit),
            new LayoutOptions()).GetAwaiter().GetResult();

        Assert.True(!result.Succeeded);
        Assert.True(result.Diagnostics.Any(message => message.Contains("changed after queue preflight", StringComparison.Ordinal)));
        Assert.Null(result.Run);
        Assert.Null(result.Persistence);
        Assert.Equal(0, composer.Calls);
        Assert.Equal(0, store.GetRecentRuns().Length);
        Assert.True(sources.GetQueueItems().Any(item => item.Id == queueItem.Id));
    }
    finally
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }
}

static void PreparationControlsForwardRequestedSettingsAndInvalidate()
{
    var source = new FakePreparationSource([new QueueItemSnapshot("a", "A.pdf", "ready", "Ready")]);
    var viewModel = new DesktopShellViewModel(source, new FakeSettingsSource(), source,
        new FakePreflightSource(), new FakeHistorySource(), new FakeTemplateSource());
    SelectSupportedPrinter(viewModel);
    viewModel.RequestedCopies = 3;
    viewModel.RequestedNUpIndex = 2;
    viewModel.OrientationIndex = 2;
    viewModel.ScalingIndex = 1;
    viewModel.DuplexIndex = 1;
    viewModel.PageRange = "2-4";
    viewModel.PrepareQueueAsync().GetAwaiter().GetResult();
    Assert.Equal(1, source.Settings!.Copies);
    Assert.Equal(3, source.Options!.Copies);
    Assert.Equal(PaperBatch.Core.Layout.NUpMode.Four, source.Options.NUp);
    Assert.True(source.Options.SheetSize!.WidthPoints > source.Options.SheetSize.HeightPoints);
    Assert.Equal(PaperBatch.Core.Layout.FitMode.ActualSize, source.Options.Fit);
    Assert.True(source.Options.Duplex);
    Assert.Equal(new PaperBatch.Core.Layout.PageRange(2, 4), source.Options.Ranges.Single());
    Assert.True(source.InvalidatedIds.Contains("a"));
    viewModel.OrientationIndex = 0;
    viewModel.PrepareQueueAsync().GetAwaiter().GetResult();
    Assert.Null(source.Options!.SheetSize);
    Assert.Equal(2, source.PrepareCalls);
    viewModel.PageRange = "0-3";
    viewModel.PrepareQueueAsync().GetAwaiter().GetResult();
    Assert.Equal(2, source.PrepareCalls);
    Assert.Contains("1-5", viewModel.OperationError);
}

static void RecipeDeviceConflictCreatesNoRunOrFinalArtifact()
{
    var root = Path.Combine(Path.GetTempPath(), "paperbatch-device-conflict-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root);
    try
    {
        var firstPath = Path.Combine(root, "first.pdf");
        var secondPath = Path.Combine(root, "second.pdf");
        File.WriteAllBytes(firstPath, TestOnePagePdf());
        File.WriteAllBytes(secondPath, TestOnePagePdf());
        var cache = new SourceSnapshotCache(new SourceSnapshotCacheOptions { RootPath = Path.Combine(root, "snapshots") });
        var artifacts = new RenderArtifactStore(Path.Combine(root, "artifacts"));
        var renderer = new RendererCoordinator([new PdfPassThroughRendererAdapter(new PdfPassThroughArtifactService(cache, artifacts))]);
        var intake = new SourceIntakeService(cache);
        var store = new SqliteRunStore(Path.Combine(root, "runs.db"));
        store.Initialize();
        var composer = new ConflictTestComposer();
        var coordinator = new PreparedArtifactCoordinator(intake, cache, renderer, new ConflictTestInspector(),
            new ConflictTestInspector(), composer, artifacts, store);
        var sources = new ProductionPresentationSources(intake, renderer, cache, rendererAvailable: true,
            artifacts: artifacts, runStore: store, preparation: coordinator);
        sources.ImportAsync([firstPath, secondPath]).GetAwaiter().GetResult();
        var recipe = new JobRecipe(JobRecipeJson.CurrentSchemaVersion, "r", "conflict",
            [new RecipeSource("first", firstPath), new RecipeSource("second", secondPath)],
            new RecipePrintSettings(1, ColorMode.Color, DuplexMode.Simplex, Orientation.Auto, ScalingMode.ShrinkToFit, "Letter"))
        {
            Rules = [
                new RecipeSettingRule("mono", "first.pdf", "pdf", new RecipeSettingsOverride(ColorMode: ColorMode.Monochrome)),
                new RecipeSettingRule("color", "second.pdf", "pdf", new RecipeSettingsOverride(ColorMode: ColorMode.Color))
            ]
        };

        var result = sources.PrepareQueueAsync(new PrintSettings(1, ColorMode.Color, DuplexMode.Simplex,
            "Letter", Orientation.Auto, ScalingMode.ShrinkToFit), new LayoutOptions(), recipe).GetAwaiter().GetResult();

        Assert.True(!result.Succeeded);
        Assert.True(result.Run is null && result.Persistence is null);
        Assert.True(result.Diagnostics.Any(message => message.Contains("device requests resolve differently", StringComparison.Ordinal)));
        Assert.Equal(0, composer.Calls);
        Assert.Equal(0, store.GetRecentRuns().Length);
    }
    finally
    {
        try { Directory.Delete(root, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}

static void PreparationErrorsFollowSelectedLanguage()
{
    var source = new FakePreparationSource([new QueueItemSnapshot("a", "A.pdf", "ready", "Ready")]);
    source.Diagnostics = ["A.pdf changed after queue preflight. Reimport the source before preparing; no final artifact was created."];
    var viewModel = new DesktopShellViewModel(source, new FakeSettingsSource(), source,
        new FakePreflightSource(), new FakeHistorySource(), new FakeTemplateSource());
    SelectSupportedPrinter(viewModel);
    viewModel.SetLanguage("zh-CN");
    viewModel.PrepareQueueAsync().GetAwaiter().GetResult();
    Assert.Contains("源文件在队列预检后发生变化", viewModel.OperationError);
    Assert.Contains("未创建最终文件", viewModel.OperationError);

    source.Diagnostics = ["Page range 3-4 for A.pdf is invalid; valid pages are 1-2."];
    viewModel.PageRange = string.Empty;
    viewModel.PrepareQueueAsync().GetAwaiter().GetResult();
    Assert.Contains("A.pdf 的页码范围 3-4 无效", viewModel.OperationError);
    Assert.Contains("有效页码为 1-2", viewModel.OperationError);

    viewModel.PageRange = "bad";
    viewModel.PrepareQueueAsync().GetAwaiter().GetResult();
    Assert.Contains("请输入页码范围", viewModel.OperationError);
}

static void RecipeDuplexRequestChangesCompiledLayout()
{
    var root = Path.Combine(Path.GetTempPath(), "paperbatch-recipe-duplex-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root);
    try
    {
        var firstPath = Path.Combine(root, "first.pdf");
        File.WriteAllBytes(firstPath, TestOnePagePdf());
        var cache = new SourceSnapshotCache(new SourceSnapshotCacheOptions { RootPath = Path.Combine(root, "snapshots") });
        var artifacts = new RenderArtifactStore(Path.Combine(root, "artifacts"));
        var renderer = new RendererCoordinator([new PdfPassThroughRendererAdapter(new PdfPassThroughArtifactService(cache, artifacts))]);
        var intake = new SourceIntakeService(cache);
        var store = new SqliteRunStore(Path.Combine(root, "runs.db"));
        store.Initialize();
        var composer = new LayoutCaptureComposer();
        var coordinator = new PreparedArtifactCoordinator(intake, cache, renderer, new ConflictTestInspector(),
            new ConflictTestInspector(), composer, artifacts, store);
        var sources = new ProductionPresentationSources(intake, renderer, cache, rendererAvailable: true,
            artifacts: artifacts, runStore: store, preparation: coordinator);
        sources.ImportAsync([firstPath, firstPath]).GetAwaiter().GetResult();
        var recipe = new JobRecipe(JobRecipeJson.CurrentSchemaVersion, "r", "duplex",
            [new RecipeSource("first", firstPath) { Override = new RecipeSettingsOverride(DuplexMode: DuplexMode.LongEdge) },
             new RecipeSource("second", firstPath) { Override = new RecipeSettingsOverride(Copies: 2, NUp: 1) }],
            new RecipePrintSettings(1, ColorMode.Color, DuplexMode.Simplex, Orientation.Auto, ScalingMode.ShrinkToFit, "Letter"))
        {
            Rules = [new RecipeSettingRule("duplex-rule", "*.pdf", "pdf", new RecipeSettingsOverride(DuplexMode: DuplexMode.LongEdge))]
        };

        try
        {
            sources.PrepareQueueAsync(
                new PrintSettings(1, ColorMode.Color, DuplexMode.Simplex, "Letter", Orientation.Auto, ScalingMode.ShrinkToFit),
                new LayoutOptions(NUp: NUpMode.Four), recipe).GetAwaiter().GetResult();
            throw new InvalidOperationException("The recording composer should stop after capturing the layout.");
        }
        catch (LayoutCapturedException) { }

        Assert.Equal(3, composer.CompiledPageCount);
        Assert.True(composer.CompiledSides.SequenceEqual([PageSide.Front, PageSide.Back, PageSide.Front]));
        Assert.Equal(0, store.GetRecentRuns().Length);
        Assert.True(sources.ResolutionSummaries[0].Contains("duplex mode/双面模式=LongEdge (file override/单文件覆盖)", StringComparison.Ordinal));
        Assert.True(sources.ResolutionSummaries[1].Contains("copies/份数=2 (file override/单文件覆盖)", StringComparison.Ordinal));
        Assert.True(sources.ResolutionSummaries[1].Contains("pages per sheet/每张页数=1 (file override/单文件覆盖)", StringComparison.Ordinal));
        Assert.True(sources.ResolutionSummaries[1].Contains("duplex mode/双面模式=LongEdge (rule/规则 duplex-rule)", StringComparison.Ordinal));
        Assert.True(sources.ResolutionSummaries[0].Contains("orientation/方向=Auto (batch/recipe defaults/批次或配方默认值)", StringComparison.Ordinal));
        Assert.Equal(50d, composer.CompiledPlacements[0].Bounds!.WidthPoints);
        Assert.True(composer.CompiledPlacements.Skip(1).All(placement => placement.Bounds!.WidthPoints == 100d));
        var viewModel = new DesktopShellViewModel(sources, sources, sources, sources, sources, sources,
            new WorkspaceStatusSnapshot("PRODUCTION MODE", "Test", false));
        Assert.True(viewModel.PreparationSettingsNote.Contains("explicit queue", StringComparison.OrdinalIgnoreCase));
    }
    finally
    {
        try { Directory.Delete(root, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}

static byte[] TestOnePagePdf()
{
    var objects = new[] { "<< /Type /Catalog /Pages 2 0 R >>", "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Resources << /ProcSet [/PDF] >> /Contents 4 0 R >>",
        "<< /Length 0 >>\nstream\n\nendstream" };
    var builder = new StringBuilder("%PDF-1.7\n");
    var offsets = new List<int> { 0 };
    for (var index = 0; index < objects.Length; index++)
    {
        offsets.Add(Encoding.ASCII.GetByteCount(builder.ToString()));
        builder.Append(index + 1).Append(" 0 obj\n").Append(objects[index]).Append("\nendobj\n");
    }
    var xref = Encoding.ASCII.GetByteCount(builder.ToString());
    builder.Append("xref\n0 5\n0000000000 65535 f \n");
    foreach (var offset in offsets.Skip(1)) builder.Append(offset.ToString("D10", System.Globalization.CultureInfo.InvariantCulture)).Append(" 00000 n \n");
    builder.Append("trailer\n<< /Size 5 /Root 1 0 R >>\nstartxref\n").Append(xref).Append("\n%%EOF\n");
    return Encoding.ASCII.GetBytes(builder.ToString());
}

static void CorruptSavedRecipesRemainVisibleAsAnError()
{
    var root = Path.Combine(Path.GetTempPath(), "paperbatch-desktop-corrupt-recipe-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root);
    try
    {
        var store = new SqliteRunStore(Path.Combine(root, "runs.db"));
        store.Initialize();
        store.SaveRecipe(new JobRecipe(1, "recipe-corrupt", "Saved packet", [new RecipeSource("source-1", "/tmp/a.pdf")],
            new RecipePrintSettings(1, ColorMode.Color, DuplexMode.Simplex, Orientation.Auto, ScalingMode.ShrinkToFit, "Letter")));
        using (var connection = CreateUnpooledSqliteConnection(Path.Combine(root, "runs.db")))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE recipes SET recipe_json = '{broken' WHERE recipe_id = 'recipe-corrupt';";
            command.ExecuteNonQuery();
        }
        var sources = new ProductionPresentationSources(runStore: store);
        var viewModel = new DesktopShellViewModel(sources, sources, sources, sources, sources, sources,
            new WorkspaceStatusSnapshot("PRODUCTION MODE", "Test", false));
        Assert.Empty(viewModel.Templates);
        Assert.Equal("Recipes unavailable", viewModel.TemplateTitle);
        Assert.True(!viewModel.CanManageRecipes);
        Assert.Contains("damaged", viewModel.TemplateDescription);

        store.DeleteRecipe("recipe-corrupt");
        store.SaveRecipe(new JobRecipe(1, "recipe-bad-time", "Bad timestamp", [new RecipeSource("source-2", "/tmp/b.pdf")],
            new RecipePrintSettings(1, ColorMode.Color, DuplexMode.Simplex, Orientation.Auto, ScalingMode.ShrinkToFit, "Letter")));
        using (var connection = CreateUnpooledSqliteConnection(Path.Combine(root, "runs.db")))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE recipes SET saved_utc = 'not-a-timestamp' WHERE recipe_id = 'recipe-bad-time';";
            command.ExecuteNonQuery();
        }
        var timestampErrorViewModel = new DesktopShellViewModel(sources, sources, sources, sources, sources, sources,
            new WorkspaceStatusSnapshot("PRODUCTION MODE", "Test", false));
        Assert.Empty(timestampErrorViewModel.Templates);
        Assert.Equal("Recipes unavailable", timestampErrorViewModel.TemplateTitle);
    }
    finally
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }
}

static void RecipeLifecycleIsExplicitAndBounded()
{
    var root = Path.Combine(Path.GetTempPath(), "paperbatch-desktop-recipe-lifecycle-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root);
    try
    {
        var store = new SqliteRunStore(Path.Combine(root, "runs.db"));
        store.Initialize();
        var source = new ProductionPresentationSources(runStore: store);
        var recipe = new JobRecipe(JobRecipeJson.CurrentSchemaVersion, "recipe-lifecycle", "Packet",
            [new RecipeSource("source-a", "/tmp/a.pdf")],
            new RecipePrintSettings(2, ColorMode.Color, DuplexMode.Simplex, Orientation.Auto, ScalingMode.ShrinkToFit, "Letter"));
        source.SaveRecipe(new JobRecipeSnapshot(recipe.SchemaVersion, recipe.RecipeId, recipe.Name, recipe.Sources, recipe.Settings));
        var renamed = source.GetRecipe(recipe.RecipeId) with { Name = "Renamed packet" };
        source.SaveRecipe(renamed);
        Assert.Equal("Renamed packet", source.GetTemplates().Single().Name);

        var exportedPath = Path.Combine(root, "export.json");
        source.ExportRecipe(recipe.RecipeId, exportedPath);
        Assert.Equal("Renamed packet", JobRecipeJson.Parse(File.ReadAllBytes(exportedPath)).Name);
        Assert.Throws<IOException>(() => source.ExportRecipe(recipe.RecipeId, exportedPath));

        var importPath = Path.Combine(root, "import.json");
        File.WriteAllText(importPath, JobRecipeJson.Serialize(recipe with { RecipeId = "imported", Name = "Imported packet" }));
        source.ImportRecipe(importPath);
        Assert.Equal(2, source.GetTemplates().Count);
        Assert.Throws<InvalidOperationException>(() => source.ImportRecipe(importPath));
        Assert.Throws<InvalidDataException>(() => source.ImportRecipe(WriteOversizedRecipe(root)));
        Assert.True(source.DeleteRecipe("imported"));
        Assert.True(source.DeleteRecipe(recipe.RecipeId));
        Assert.Empty(source.GetTemplates());

        var cache = new SourceSnapshotCache(new SourceSnapshotCacheOptions { RootPath = Path.Combine(root, "cache") });
        var intake = new SourceIntakeService(cache);
        source = new ProductionPresentationSources(intake, runStore: store);
        var initiallyLoaded = source.ImportAsync([Path.Combine(root, "missing.pdf")]).GetAwaiter().GetResult();
        var created = source.CreateRecipe("Missing source recipe", initiallyLoaded.Select(item => item.Id).ToArray(),
            new RecipeDefaultsSnapshot(1, ColorMode.Color, DuplexMode.Simplex, Orientation.Auto, ScalingMode.ShrinkToFit, "Letter"));
        source.SaveRecipe(created);
        var viewModel = new DesktopShellViewModel(source, source, source, source, source, source,
            new WorkspaceStatusSnapshot("PRODUCTION MODE", "Test", false));
        var saved = viewModel.Templates.Single();
        viewModel.LoadRecipeSourcesAsync(saved, replaceQueue: false).GetAwaiter().GetResult();
        Assert.Equal(2, viewModel.QueueItems.Count);
        Assert.True(viewModel.QueueItems.All(item => item.Status == "Needs attention"));
    }
    finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
}

static string WriteOversizedRecipe(string root)
{
    var path = Path.Combine(root, "oversized.json");
    File.WriteAllBytes(path, new byte[JobRecipeJson.MaximumRecipeBytes + 1]);
    return path;
}

static void CorruptSavedHistoryRemainsVisibleAsAnError()
{
    var root = Path.Combine(Path.GetTempPath(), "paperbatch-desktop-corrupt-history-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root);
    try
    {
        var store = new SqliteRunStore(Path.Combine(root, "runs.db"));
        store.Initialize();
        var now = DateTimeOffset.UtcNow;
        var settings = new PrintSettings(1, ColorMode.Color, DuplexMode.Simplex, "Letter", Orientation.Auto, ScalingMode.ShrinkToFit);
        var item = new PreparedItem("item", new SourceSnapshot("snapshot", new string('a', 64), 1, now),
            new RendererResult("renderer", "1", "source-artifact", new string('b', 64), 1, 1, new RendererCapabilities(1, ["application/pdf"])),
            [new PageMapping(1, 1)], settings);
        var run = PreparedRun.Create("run-corrupt", now, new string('c', 64), [item],
            [new CompatibleSegment("segment", 0, [item.ItemId], "compat", settings)]);
        store.CreateRun(run, "request-corrupt", new RetentionMetadata(now.AddDays(30), true));
        store.SaveRecipe(new JobRecipe(1, "recipe-survives", "Valid recipe", [new RecipeSource("source", "/tmp/a.pdf")],
            new RecipePrintSettings(1, ColorMode.Color, DuplexMode.Simplex, Orientation.Auto, ScalingMode.ShrinkToFit, "Letter")));
        using (var connection = CreateUnpooledSqliteConnection(Path.Combine(root, "runs.db")))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE runs SET created_utc = 'not-a-timestamp' WHERE run_id = 'run-corrupt';";
            command.ExecuteNonQuery();
        }
        var sources = new ProductionPresentationSources(runStore: store);
        var viewModel = new DesktopShellViewModel(sources, sources, sources, sources, sources, sources,
            new WorkspaceStatusSnapshot("PRODUCTION MODE", "Test", false));
        Assert.Empty(viewModel.HistoryEntries);
        Assert.Equal("History unavailable", viewModel.HistoryTitle);
        Assert.Contains("damaged", viewModel.HistoryDescription);
        Assert.Equal("Valid recipe", viewModel.Templates.Single().Name);
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

static Microsoft.Data.Sqlite.SqliteConnection CreateUnpooledSqliteConnection(string databasePath) =>
    new(new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
    {
        DataSource = databasePath,
        Pooling = false
    }.ToString());

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

static void SelectSupportedPrinter(DesktopShellViewModel viewModel)
{
    var capabilities = new PaperBatch.Core.Printing.PrinterCapabilitySnapshot("test-queue", "Test Queue", "Test Driver", "test-port", "test-fingerprint",
        new Dictionary<PaperBatch.Core.Printing.PrintOption, PaperBatch.Core.Printing.PrintOptionCapability>
        {
            [PaperBatch.Core.Printing.PrintOption.Media] = new(PaperBatch.Core.Printing.CapabilityState.Supported, ["Letter", "A4"], null),
            [PaperBatch.Core.Printing.PrintOption.Color] = new(PaperBatch.Core.Printing.CapabilityState.Supported, ["Color", "Monochrome"], null),
            [PaperBatch.Core.Printing.PrintOption.Duplex] = new(PaperBatch.Core.Printing.CapabilityState.Supported, ["Simplex", "LongEdge", "ShortEdge"], null),
            [PaperBatch.Core.Printing.PrintOption.Copies] = new(PaperBatch.Core.Printing.CapabilityState.Supported, ["1..999"], null)
        });
    var selection = new PrinterQueueCapabilitySnapshot("test-queue", "Test Queue", "Test Driver", capabilities, "test details", "测试详情");
    viewModel.PrinterQueues.Add(selection);
    viewModel.SelectedPrinter = selection;
}

sealed class FakeQueueSource(IReadOnlyList<QueueItemSnapshot> items) : IQueuePresentationSource
{
    public IReadOnlyList<QueueItemSnapshot> GetQueueItems() => items;
}

sealed class FakeUnavailablePrintSource(IReadOnlyList<QueueItemSnapshot> items) : IQueuePresentationSource, IPrintSubmissionPresentationSource
{
    public bool CanSubmitPrintJobs => true;
    public IReadOnlyList<QueueItemSnapshot> GetQueueItems() => items;
    public Task<IReadOnlyList<PrintJobSnapshot>> SubmitPreparedRunAsync(string queueItemId, CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException("unused");
    public Task<IReadOnlyList<PrintJobSnapshot>> RefreshPrintJobsAsync(string queueItemId, CancellationToken cancellationToken = default) =>
        Task.FromException<IReadOnlyList<PrintJobSnapshot>>(new UnauthorizedAccessException());
    public Task<bool> CancelPrintJobAsync(string jobId, CancellationToken cancellationToken = default) =>
        Task.FromException<bool>(new UnauthorizedAccessException());
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

sealed class FakePreparationSource(IReadOnlyList<QueueItemSnapshot> items) : IQueuePresentationSource,
    IQueuePreparationPresentationSource, IFinalPreviewPresentationSource, IPreparedArtifactPresentationSource
{
    public event Action<string>? PreparedArtifactChanged { add { } remove { } }
    public bool CanPrepare => true;
    public IReadOnlyList<string> ResolutionSummaries => [];
    public PrintSettings? Settings { get; private set; }
    public PaperBatch.Core.Layout.LayoutOptions? Options { get; private set; }
    public PaperBatch.Core.Recipes.JobRecipe? Recipe { get; private set; }
    public IReadOnlyList<string> Diagnostics { get; set; } = ["test capture"];
    public int PrepareCalls { get; private set; }
    public List<string> InvalidatedIds { get; } = [];
    public IReadOnlyList<QueueItemSnapshot> GetQueueItems() => items;
    public Task<PreparedArtifactResult> PrepareQueueAsync(PrintSettings requestedSettings,
        PaperBatch.Core.Layout.LayoutOptions layoutOptions, PaperBatch.Core.Recipes.JobRecipe? recipe = null,
        CancellationToken cancellationToken = default)
    {
        PrepareCalls++;
        Settings = requestedSettings;
        Options = layoutOptions;
        Recipe = recipe;
        return Task.FromResult(new PreparedArtifactResult(null, null, Diagnostics, false));
    }
    public void RegisterPreparedRun(string queueItemId, PreparedRun run) { }
    public void InvalidatePreparedRun(string queueItemId) => InvalidatedIds.Add(queueItemId);
    public Task<PreviewSnapshot> GetPreviewAsync(string queueItemId, CancellationToken cancellationToken = default) =>
        Task.FromResult(new PreviewSnapshot(queueItemId, "Preview", 1, 1));
}

sealed class ConflictTestInspector : IPdfArtifactInspector, IPdfPageMetadataInspector, IStaticPdfContentInspector
{
    public Task<int> GetPageCountAsync(Stream pdf, CancellationToken cancellationToken) => Task.FromResult(1);
    public Task<IReadOnlyList<PageSize>> GetPageSizesAsync(Stream pdf, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<PageSize>>([new PageSize(100, 100)]);
    public Task<bool> HasStaticPageContentAsync(Stream pdf, CancellationToken cancellationToken) => Task.FromResult(true);
}

sealed class ConflictTestComposer : IPageCompositionAdapter
{
    public int Calls { get; private set; }
    public Task ComposePdfAsync(LayoutCompilation compilation, IEnumerable<PdfCompositionSource> sources,
        Stream destination, CancellationToken cancellationToken = default)
    {
        Calls++;
        throw new InvalidOperationException("Device conflict should stop before composition.");
    }
}

sealed class LayoutCaptureComposer : IPageCompositionAdapter
{
    public int CompiledPageCount { get; private set; }
    public IReadOnlyList<PageSide> CompiledSides { get; private set; } = [];
    public IReadOnlyList<(string ItemId, PageRectangle? Bounds)> CompiledPlacements { get; private set; } = [];
    public Task ComposePdfAsync(LayoutCompilation compilation, IEnumerable<PdfCompositionSource> sources,
        Stream destination, CancellationToken cancellationToken = default)
    {
        CompiledPageCount = compilation.Pages.Length;
        CompiledSides = compilation.Pages.Select(page => page.Side).ToArray();
        CompiledPlacements = compilation.Pages.SelectMany(page => page.Placements)
            .Select(placement => (placement.ItemId, placement.TargetBounds)).ToArray();
        throw new LayoutCapturedException();
    }
}

sealed class LayoutCapturedException : Exception { }

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
    public string? HistoryReadError => null;
}

sealed class FakeTemplateSource : ITemplatePresentationSource
{
    public IReadOnlyList<TemplateSnapshot> GetTemplates(bool chinese = false) => [];
    public string? TemplateReadError => null;
}

sealed class FakePrinterCapabilitySource : IQueuePresentationSource, IPrinterCapabilityPresentationSource
{
    public bool CanInspectPrinterCapabilities => true;
    public int ReadCalls { get; private set; }
    public int ReadThreadId { get; private set; }
    public bool DelayRead { get; init; }
    private readonly TaskCompletionSource<IReadOnlyList<PrinterQueueCapabilitySnapshot>> _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    public void CompleteRead() => _completion.TrySetResult(Entries());
    public IReadOnlyList<QueueItemSnapshot> GetQueueItems() => [];
    public Task<IReadOnlyList<PrinterQueueCapabilitySnapshot>> ReadPrinterCapabilitiesAsync(CancellationToken cancellationToken = default)
    {
        ReadCalls++;
        ReadThreadId = Environment.CurrentManagedThreadId;
        return DelayRead ? _completion.Task : Task.FromResult(Entries());
    }

    private static IReadOnlyList<PrinterQueueCapabilitySnapshot> Entries() => [
            new("sample-queue-id", "Sample Queue", "Sample Driver", null,
                "Port: LPT1\nMedia: Supported (Letter)\nDuplex: Unavailable — The driver did not report duplex capability.",
                "端口：LPT1\n纸张：支持（Letter）\n双面：不可用 — 驱动未报告双面能力。")
        ];
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
