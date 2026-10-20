using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using PaperBatch.Desktop;
using PaperBatch.Desktop.Presentation;

var tests = new (string Name, Action Run)[]
{
    ("selection refreshes preview, settings, and preflight from presentation sources", SelectionRefreshesPresentationSnapshots),
    ("navigation changes the active section", NavigationChangesTheActiveSection),
    ("headless shell identifies demo data and has no print action", HeadlessShellIsExplicitlyDemoOnly),
    ("presentation assembly does not depend on native backends", PresentationAssemblyHasNoNativeBackendCoupling)
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
    var window = new MainWindow(DesktopShellViewModel.CreateDemo());
    try
    {
        window.Show();

        var banner = window.FindControl<Border>("SampleDataBanner");
        Assert.NotNull(banner);
        var bannerText = string.Join(" ", ((StackPanel)banner!.Child!).Children.OfType<TextBlock>().Select(textBlock => textBlock.Text));
        Assert.Contains("SAMPLE DATA", bannerText);
        Assert.Contains("No files are opened", bannerText);
        Assert.Null(window.FindControl<Button>("PrintButton"));
    }
    finally
    {
        window.Close();
    }
}

static void PresentationAssemblyHasNoNativeBackendCoupling()
{
    var assembly = typeof(DesktopShellViewModel).Assembly;
    string[] forbiddenReferenceTokens =
    [
        "System.Printing",
        "System.Management",
        "Microsoft.Data.Sqlite",
        "Microsoft.Office",
        "Pdfium",
        "PaperBatch.Core",
        "PaperBatch.Storage"
    ];
    var references = assembly.GetReferencedAssemblies().Select(reference => reference.Name ?? string.Empty).ToArray();
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
