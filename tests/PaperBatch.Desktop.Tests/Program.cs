using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using PaperBatch.Desktop;
using PaperBatch.Core.Intake;
using PaperBatch.Core.Rendering;
using PaperBatch.Desktop.Presentation;

var tests = new (string Name, Action Run)[]
{
    ("selection refreshes preview, settings, and preflight from presentation sources", SelectionRefreshesPresentationSnapshots),
    ("navigation changes the active section", NavigationChangesTheActiveSection),
    ("source intake errors remain visible and repeated imports append", SourceIntakeErrorsRemainVisibleAndAppend),
    ("file picker reports all skipped and mixed local selections", FilePickerClassifiesSkippedSelections),
    ("headless shell identifies demo data and has no print action", HeadlessShellIsExplicitlyDemoOnly),
    ("production presentation exposes unavailable production capabilities without demo rows", ProductionPresentationIsExplicitlyUnavailable),
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
    Assert.Contains("unavailable", sources.GetPreview("missing").Description);
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
        Assert.Equal("No prepared artifact is available.", window.FindControl<TextBlock>("PreviewDescription")!.Text);
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

sealed class FakePreviewSource : IFinalPreviewPresentationSource
{
    public string? LastRequestedId { get; private set; }

    public PreviewSnapshot GetPreview(string queueItemId)
    {
        LastRequestedId = queueItemId;
        return new PreviewSnapshot(queueItemId, "Synthetic page-face illustration", 1, 4);
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

    public static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException($"Expected '{expected}', got '{actual}'.");
        }
    }
}
