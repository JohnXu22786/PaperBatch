using System.Collections.ObjectModel;
using System.Collections.Immutable;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using Avalonia.Threading;
using PaperBatch.Core.Domain;
using PaperBatch.Core.Layout;

namespace PaperBatch.Desktop.Presentation;

internal sealed class DesktopShellViewModel : INotifyPropertyChanged
{
    private readonly IEffectiveSettingsPresentationSource _settingsSource;
    private readonly IFinalPreviewPresentationSource _previewSource;
    private readonly IPreflightPresentationSource _preflightSource;
    private readonly IHistoryPresentationSource _historySource;
    private readonly ITemplatePresentationSource _templateSource;
    private readonly IPrinterCapabilityPresentationSource? _printerCapabilitySource;
    private QueueItemSnapshot? _selectedQueueItem;
    private PreviewSnapshot? _currentPreview;
    private IReadOnlyList<EffectiveSettingSnapshot> _effectiveSettings = [];
    private IReadOnlyList<PreflightFindingSnapshot> _preflightFindings = [];
    private ShellSection _activeSection = ShellSection.Queue;
    private readonly WorkspaceStatusSnapshot _workspaceStatus;
    private string? _operationError;
    private readonly IQueuePresentationSource _queueSource;
    private CancellationTokenSource? _previewRefreshCancellation;
    private Task _previewRefreshTask = Task.CompletedTask;
    private long _previewRequestNumber;
    private string _language = "en";
    private TemplateSnapshot? _selectedTemplate;
    private int _requestedCopies = 1;
    private int _requestedNUp = 1;
    private int _orientationIndex;
    private int _scalingIndex;
    private int _mediaIndex;
    private int _duplexIndex;
    private string _pageRange = "";
    private ColorMode _requestedColorMode = ColorMode.Color;
    private PaperBatch.Core.Recipes.JobRecipe? _activeRecipe;

    public DesktopShellViewModel(
        IQueuePresentationSource queueSource,
        IEffectiveSettingsPresentationSource settingsSource,
        IFinalPreviewPresentationSource previewSource,
        IPreflightPresentationSource preflightSource,
        IHistoryPresentationSource historySource,
        ITemplatePresentationSource templateSource,
        WorkspaceStatusSnapshot? workspaceStatus = null)
    {
        ArgumentNullException.ThrowIfNull(queueSource);
        ArgumentNullException.ThrowIfNull(settingsSource);
        ArgumentNullException.ThrowIfNull(previewSource);
        ArgumentNullException.ThrowIfNull(preflightSource);
        ArgumentNullException.ThrowIfNull(historySource);
        ArgumentNullException.ThrowIfNull(templateSource);

        _queueSource = queueSource;
        _settingsSource = settingsSource;
        _previewSource = previewSource;
        if (_previewSource is IPreparedArtifactPresentationSource preparedArtifacts)
            preparedArtifacts.PreparedArtifactChanged += OnPreparedArtifactChanged;
        _preflightSource = preflightSource;
        _historySource = historySource;
        _templateSource = templateSource;
        _printerCapabilitySource = queueSource as IPrinterCapabilityPresentationSource;
        QueueItems = new ObservableCollection<QueueItemSnapshot>(queueSource.GetQueueItems());
        PrinterQueues = new ObservableCollection<PrinterQueueCapabilitySnapshot>();
        HistoryEntries = historySource.GetHistoryEntries();
        Templates = templateSource.GetTemplates(chinese: false);
        _workspaceStatus = workspaceStatus ?? new WorkspaceStatusSnapshot("Demo mode", "Synthetic sample data only.", true);
        NavigateCommand = new RelayCommand(Navigate, CanNavigate);
        MoveQueueItemCommand = new RelayCommand(MoveQueueItem, CanMoveQueueItem);
        RemoveQueueItemCommand = new RelayCommand(_ => RemoveSelectedQueueItem(), _ => SelectedQueueItem is not null);
        RefreshPrinterCapabilitiesCommand = new RelayCommand(_ => _ = RefreshPrinterCapabilitiesAsync(), _ => CanRefreshPrinterCapabilities);

        if (QueueItems.Count > 0)
        {
            SelectedQueueItem = QueueItems[0];
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<QueueItemSnapshot> QueueItems { get; }

    public IReadOnlyList<HistoryEntrySnapshot> HistoryEntries { get; private set; }

    public IReadOnlyList<TemplateSnapshot> Templates { get; private set; }

    public TemplateSnapshot? SelectedTemplate
    {
        get => _selectedTemplate;
        set
        {
            if (!SetProperty(ref _selectedTemplate, value)) return;
            OnPropertyChanged(nameof(HasSelectedTemplate));
            OnPropertyChanged(nameof(CanLoadSelectedRecipe));
        }
    }

    public bool CanManageRecipes => IsProductionMode &&
        _templateSource is IRecipeManagementPresentationSource { IsRecipeStoreAvailable: true };
    public bool HasSelectedTemplate => SelectedTemplate is not null;
    public bool CanLoadSelectedRecipe => CanManageRecipes && SelectedTemplate is not null && CanImportFiles;

    public ICommand NavigateCommand { get; }

    public ICommand MoveQueueItemCommand { get; }

    public ICommand RemoveQueueItemCommand { get; }

    public ICommand RefreshPrinterCapabilitiesCommand { get; }

    public ObservableCollection<PrinterQueueCapabilitySnapshot> PrinterQueues { get; }

    private string _printerCapabilityStatusEnglish = "Select Refresh to read local Windows queue capabilities. No print jobs are submitted.";
    private string _printerCapabilityStatusChinese = "选择“刷新”以读取本地 Windows 队列能力。不会提交打印作业。";
    public string PrinterCapabilityStatus => IsChinese ? _printerCapabilityStatusChinese : _printerCapabilityStatusEnglish;
    private int _printerCapabilityRefreshGate;
    public bool IsPrinterCapabilityRefreshInProgress => Volatile.Read(ref _printerCapabilityRefreshGate) != 0;

    private void SetPrinterCapabilityStatus(string english, string chinese)
    {
        _printerCapabilityStatusEnglish = english;
        _printerCapabilityStatusChinese = chinese;
        OnPropertyChanged(nameof(PrinterCapabilityStatus));
    }

    public bool CanInspectPrinterCapabilities => IsProductionMode && _printerCapabilitySource?.CanInspectPrinterCapabilities == true;
    public bool CanRefreshPrinterCapabilities => CanInspectPrinterCapabilities && !IsPrinterCapabilityRefreshInProgress;

    public async Task RefreshPrinterCapabilitiesAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.CompareExchange(ref _printerCapabilityRefreshGate, 1, 0) != 0) return;
        OnPrinterRefreshStateChanged();
        PrinterQueues.Clear();
        try
        {
            if (!CanInspectPrinterCapabilities || _printerCapabilitySource is null)
            {
                SetPrinterCapabilityStatus("Read-only queue capability inspection is available only in the production Windows app.",
                    "只读队列能力检查仅在 Windows 生产应用中可用。");
                return;
            }
            SetPrinterCapabilityStatus("Reading local queues and driver-reported capabilities. This does not submit a job or change defaults.",
                "正在读取本地打印队列和驱动报告的能力；不会提交作业或更改默认设置。");
            var entries = await Task.Run(
                async () => await _printerCapabilitySource.ReadPrinterCapabilitiesAsync(cancellationToken).ConfigureAwait(false),
                cancellationToken).ConfigureAwait(true);
            foreach (var entry in entries) PrinterQueues.Add(entry with { Chinese = IsChinese });
            if (entries.Count == 0)
                SetPrinterCapabilityStatus("No local print queues were reported.", "没有发现本地打印队列。");
            else
                SetPrinterCapabilityStatus($"Read {entries.Count} local queue(s). These values are inventory only; route and paper output are unverified.",
                    $"已读取 {entries.Count} 个本地队列。这里只是能力清单；实际路由和纸张输出尚未验证。");
        }
        catch (OperationCanceledException)
        {
            SetPrinterCapabilityStatus("Queue capability inspection was cancelled.", "队列能力检查已取消。");
        }
        catch (Exception error)
        {
            SetPrinterCapabilityStatus($"Queue capability inspection failed ({error.GetType().Name}). No job was submitted.",
                $"队列能力检查失败（{error.GetType().Name}）。没有提交作业。");
        }
        finally
        {
            Interlocked.Exchange(ref _printerCapabilityRefreshGate, 0);
            OnPrinterRefreshStateChanged();
        }
    }

    private void OnPrinterRefreshStateChanged()
    {
        OnPropertyChanged(nameof(IsPrinterCapabilityRefreshInProgress));
        OnPropertyChanged(nameof(CanRefreshPrinterCapabilities));
        (RefreshPrinterCapabilitiesCommand as RelayCommand)?.NotifyCanExecuteChanged();
    }

    public int RequestedCopies { get => _requestedCopies; set { if (SetProperty(ref _requestedCopies, Math.Clamp(value, 1, 999))) InvalidatePreparedQueue(); } }
    public int RequestedNUp { get => _requestedNUp; set { if (SetProperty(ref _requestedNUp, value is 2 or 4 ? value : 1)) InvalidatePreparedQueue(); } }
    public int RequestedNUpIndex { get => RequestedNUp switch { 2 => 1, 4 => 2, _ => 0 }; set => RequestedNUp = value switch { 1 => 2, 2 => 4, _ => 1 }; }
    public int OrientationIndex { get => _orientationIndex; set { if (SetProperty(ref _orientationIndex, Math.Clamp(value, 0, 2))) InvalidatePreparedQueue(); } }
    public int ScalingIndex { get => _scalingIndex; set { if (SetProperty(ref _scalingIndex, Math.Clamp(value, 0, 1))) InvalidatePreparedQueue(); } }
    public int MediaIndex { get => _mediaIndex; set { if (SetProperty(ref _mediaIndex, Math.Clamp(value, 0, 1))) InvalidatePreparedQueue(); } }
    public int DuplexIndex { get => _duplexIndex; set { if (SetProperty(ref _duplexIndex, Math.Clamp(value, 0, 2))) InvalidatePreparedQueue(); } }
    public string PageRange { get => _pageRange; set { if (SetProperty(ref _pageRange, value ?? "")) InvalidatePreparedQueue(); } }
    public string PrepareLabel => Localize("Prepare and preview PDF", "准备并预览 PDF");
    public string PreparationSettingsHeading => Localize("Requested preparation settings", "请求的准备设置");
    public string CopiesLabel => Localize("Copies", "份数");
    public string OrientationLabel => Localize("Orientation", "方向");
    public string ScalingLabel => Localize("Scaling", "缩放");
    public string NUpLabel => Localize("Pages per sheet", "每张页数");
    public string MediaLabel => Localize("Sheet size", "纸张尺寸");
    public string DuplexLabel => Localize("Duplex request", "双面请求");
    public string PageRangeLabel => Localize("Page range (blank = all)", "页码范围（留空为全部）");
    public IReadOnlyList<string> OrientationOptions => IsChinese ? ["自动", "纵向", "横向"] : ["Auto", "Portrait", "Landscape"];
    public IReadOnlyList<string> ScalingOptions => IsChinese ? ["缩小以适合", "实际大小"] : ["Shrink to fit", "Actual size"];
    public IReadOnlyList<string> NUpOptions => ["1", "2", "4"];
    public IReadOnlyList<string> MediaOptions => ["Letter", "A4"];
    public IReadOnlyList<string> DuplexOptions => IsChinese ? ["单面", "双面长边", "双面短边"] : ["Simplex", "Long edge", "Short edge"];
    public string PreparationSettingsNote => Localize("Values above are requested for this batch or explicitly loaded recipe. Recipe defaults apply only after you load that recipe. Printer and tray capabilities are not connected; media, color and duplex requests are not validated against a device. Nothing will be printed.",
        "以上是此批次或明确加载的配方请求值。仅在加载配方后应用其默认值。打印机和纸盒能力尚未连接；纸张、颜色和双面请求未经设备验证。不会提交打印。");
    public string PreparationResolutionNote
    {
        get
        {
            var summaries = _queueSource is IQueuePreparationPresentationSource source ? source.ResolutionSummaries : [];
            if (_activeRecipe is null) return Localize("No recipe is loaded; batch controls supply these requested values.", "未加载配方；当前请求值来自批次控件。");
            return summaries.Count == 0
                ? Localize("Resolved values and their source will appear here after preparation.", "准备后将在此处显示解析值及其来源。")
                : Localize("Per-file requested values and provenance: " + string.Join("\n", summaries),
                    "每个文件的请求值及来源：" + string.Join("\n", summaries));
        }
    }
    public bool CanPrepareQueue => IsProductionMode && _queueSource is IQueuePreparationPresentationSource { CanPrepare: true };

    public async Task PrepareQueueAsync(CancellationToken cancellationToken = default)
    {
        if (_queueSource is not IQueuePreparationPresentationSource source || !source.CanPrepare)
            throw new InvalidOperationException("Preparation requires a configured PDFium runtime and a nonempty supported queue.");
        var media = MediaIndex == 1 ? "A4" : "Letter";
        var orientation = OrientationIndex switch { 1 => Orientation.Portrait, 2 => Orientation.Landscape, _ => Orientation.Auto };
        var scaling = ScalingIndex == 1 ? ScalingMode.ActualSize : ScalingMode.ShrinkToFit;
        var duplex = DuplexIndex switch { 1 => DuplexMode.LongEdge, 2 => DuplexMode.ShortEdge, _ => DuplexMode.Simplex };
        PageSize? size = orientation == Orientation.Auto ? null : media == "A4"
            ? (orientation == Orientation.Landscape ? new PageSize(841.89, 595.28) : new PageSize(595.28, 841.89))
            : (orientation == Orientation.Landscape ? new PageSize(792, 612) : new PageSize(612, 792));
        ImmutableArray<PageRange> ranges;
        try { ranges = ParsePageRange(PageRange); }
        catch (InvalidOperationException exception)
        {
            OperationError = LocalizePreparationError(exception.Message);
            return;
        }
        var settings = new PrintSettings(RequestedCopies, _requestedColorMode, duplex, media, orientation, scaling);
        var options = new LayoutOptions(Copies: RequestedCopies, NUp: RequestedNUp switch { 2 => NUpMode.Two, 4 => NUpMode.Four, _ => NUpMode.One },
            Duplex: duplex != DuplexMode.Simplex, Fit: scaling == ScalingMode.ActualSize ? FitMode.ActualSize : FitMode.ShrinkToFit,
            SheetSize: size, Ranges: ranges);
        var result = await source.PrepareQueueAsync(settings, options, _activeRecipe, cancellationToken).ConfigureAwait(true);
        OperationError = result.Succeeded ? null : LocalizePreparationError(string.Join(" ", result.Diagnostics));
        if (result.Succeeded) await RefreshSelectedItemAsync().ConfigureAwait(true);
        OnPropertyChanged(nameof(PrepareLabel));
        OnPropertyChanged(nameof(PreparationResolutionNote));
    }

    private static System.Collections.Immutable.ImmutableArray<PageRange> ParsePageRange(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return [];
        var parts = value.Split('-', StringSplitOptions.TrimEntries);
        if (parts.Length is < 1 or > 2 || !int.TryParse(parts[0], out var first) || first < 1 ||
            (parts.Length == 2 && (!int.TryParse(parts[1], out var last) || last < first)) ||
            (parts.Length == 2 && string.IsNullOrEmpty(parts[1])))
            throw new InvalidOperationException("Enter a page range such as 1-5, or leave it blank to include every page.");
        var end = parts.Length == 2 ? int.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture) : first;
        return [new PageRange(first, end)];
    }

    private string LocalizePreparationError(string message)
    {
        if (!IsChinese) return message;
        if (message.Contains("changed after queue preflight", StringComparison.Ordinal))
            return "源文件在队列预检后发生变化。请重新导入后再准备；未创建最终文件。";
        if (message.Contains("Per-file device requests resolve differently", StringComparison.Ordinal))
            return "不同文件的设备请求不一致。当前准备运行模型要求每个兼容分段使用单一设备配置，且此界面无法验证逐文件设备设置；未创建文件。";
        if (message.Contains("Enter a page range such as", StringComparison.Ordinal))
            return "请输入页码范围，例如 1-5；留空则包含所有页面。";
        var pageRangeError = message.IndexOf(" is invalid; valid pages are 1-", StringComparison.Ordinal);
        if (pageRangeError >= 0)
        {
            var context = message[..pageRangeError];
            var validPages = message[(pageRangeError + " is invalid; valid pages are 1-".Length)..].TrimEnd('.');
            var fileMarker = context.LastIndexOf(" for ", StringComparison.Ordinal);
            return fileMarker >= 0
                ? $"文件 {context[(fileMarker + 5)..]} 的页码范围 {context["Page range ".Length..fileMarker]} 无效；有效页码为 1-{validPages}。"
                : $"文件 {context["The selected page range for ".Length..]} 的页码范围无效；有效页码为 1-{validPages}。";
        }
        if (message.Contains("requested media is not supported", StringComparison.Ordinal))
            return "请求的纸张尺寸不受桌面布局控件支持；未创建文件。";
        if (message.Contains("recipe rules support only PDF, JPEG, and PNG", StringComparison.Ordinal))
            return "配方规则仅支持 PDF、JPEG 和 PNG 文件。";
        return "准备失败。请检查来源、预检状态和请求设置后重试。详细信息：" + message;
    }

    private void InvalidatePreparedQueue()
    {
        if (_queueSource is not IPreparedArtifactPresentationSource prepared) return;
        foreach (var item in QueueItems) prepared.InvalidatePreparedRun(item.Id);
        CurrentPreview = null;
        OnPropertyChanged(nameof(CanPrepareQueue));
    }

    public int LanguageIndex
    {
        get => _language == "zh-CN" ? 1 : 0;
        set => SetLanguage(value == 1 ? "zh-CN" : "en");
    }

    public void SetLanguage(string? language)
    {
        var next = string.Equals(language, "zh-CN", StringComparison.OrdinalIgnoreCase) ? "zh-CN" : "en";
        if (!SetProperty(ref _language, next, nameof(LanguageIndex))) return;
        OnPropertyChanged(nameof(LanguageIndex));
        OnPropertyChanged(nameof(IsChinese));
        for (var index = 0; index < PrinterQueues.Count; index++)
            PrinterQueues[index] = PrinterQueues[index] with { Chinese = IsChinese };
        Templates = _templateSource.GetTemplates(IsChinese);
        if (IsDemoMode)
        {
            HistoryEntries = _historySource.GetHistoryEntries().Select(entry => entry with
            {
                Title = Localize(entry.Title, entry.Title switch
                {
                    "Sample run A" => "示例运行 A",
                    "Sample run B" => "示例运行 B",
                    _ => entry.Title
                }),
                Summary = Localize(entry.Summary, entry.Summary switch
                {
                    "Example history row · no pages submitted" => "示例历史记录 · 未提交页面",
                    "Example history row · no output created" => "示例历史记录 · 未生成输出",
                    _ => entry.Summary
                }),
                Status = Localize(entry.Status, entry.Status == "Example only" ? "仅供示例" : entry.Status)
            }).ToArray();
            Templates = _templateSource.GetTemplates().Select(template => template with
            {
                Name = Localize(template.Name, template.Name switch
                {
                    "Sample classroom packet" => "示例课堂资料包",
                    "Sample office handout" => "示例办公讲义",
                    _ => template.Name
                }),
                Summary = Localize(template.Summary, template.Summary == "Example recipe · not saved" ? "示例配方 · 未保存" : template.Summary),
                SettingsSummary = Localize(template.SettingsSummary, template.SettingsSummary switch
                {
                    "A4 · one-sided · sample values" => "A4 · 单面 · 示例值",
                    "Letter · two-sided · sample values" => "Letter · 双面 · 示例值",
                    _ => template.SettingsSummary
                })
            }).ToArray();
        }
        foreach (var property in new[] { nameof(SectionQueueLabel), nameof(SectionHistoryLabel), nameof(SectionTemplatesLabel), nameof(SectionPrintersLabel), nameof(PrinterRefreshLabel), nameof(PrinterCapabilityStatus), nameof(PrinterCapabilityHelp),
                     nameof(ActiveSectionTitle), nameof(HistoryEyebrow), nameof(HistoryTitle), nameof(HistoryDescription), nameof(HistoryEntries), nameof(TemplateEyebrow), nameof(TemplateTitle), nameof(TemplateDescription), nameof(Templates), nameof(CreateRecipeLabel), nameof(RenameRecipeLabel), nameof(DeleteRecipeLabel), nameof(ImportRecipeLabel), nameof(ExportRecipeLabel), nameof(LoadRecipeLabel), nameof(RefreshRecipesLabel), nameof(PreparationSettingsHeading), nameof(SettingsHeading), nameof(CopiesLabel), nameof(OrientationLabel), nameof(ScalingLabel), nameof(NUpLabel), nameof(MediaLabel), nameof(DuplexLabel), nameof(PageRangeLabel), nameof(OrientationOptions), nameof(ScalingOptions), nameof(DuplexOptions), nameof(PreparationSettingsNote), nameof(PreparationResolutionNote), nameof(PrepareLabel) })
            OnPropertyChanged(property);
    }

    public string SectionQueueLabel => Localize("Queue and review", "队列与审核");
    public string SectionHistoryLabel => Localize("History", "历史记录");
    public string SectionTemplatesLabel => Localize("Templates", "模板");
    public string SectionPrintersLabel => Localize("Printers", "打印机");
    public string PrinterRefreshLabel => Localize("Refresh local queue capabilities", "刷新本地队列能力");
    public string PrinterCapabilityHelp => Localize("Read-only local queue and driver capability inventory. Paper output and real job behavior are not verified here.",
        "只读读取本地队列和驱动能力清单；这里不会验证实际纸张输出或真实作业行为。");
    public string CreateRecipeLabel => Localize("Create from queue…", "从队列创建…");
    public string RenameRecipeLabel => Localize("Rename…", "重命名…");
    public string DeleteRecipeLabel => Localize("Delete…", "删除…");
    public string ImportRecipeLabel => Localize("Import recipe…", "导入配方…");
    public string ExportRecipeLabel => Localize("Export recipe…", "导出配方…");
    public string LoadRecipeLabel => Localize("Load sources into queue…", "将来源加载到队列…");
    public string RefreshRecipesLabel => Localize("Refresh", "刷新");


    public async Task ImportFilesAsync(IEnumerable<string> paths, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(paths);
        if (_queueSource is not IQueueImportPresentationSource importer)
            throw new InvalidOperationException("File intake is unavailable in this presentation mode.");
        var imported = await importer.ImportAsync(paths, cancellationToken).ConfigureAwait(true);
        foreach (var item in imported)
        {
            if (!QueueItems.Any(existing => string.Equals(existing.Id, item.Id, StringComparison.Ordinal)))
                QueueItems.Add(item);
        }
        if (SelectedQueueItem is null || !QueueItems.Contains(SelectedQueueItem))
            SelectedQueueItem = imported.FirstOrDefault(item => QueueItems.Contains(item)) ?? QueueItems.FirstOrDefault();
        OnPropertyChanged(nameof(QueueStateLabel));
        OnPropertyChanged(nameof(CanPrepareQueue));
    }

    public void RefreshTemplates()
    {
        Templates = _templateSource.GetTemplates(IsChinese);
        SelectedTemplate = null;
        OnPropertyChanged(nameof(Templates));
        OnPropertyChanged(nameof(TemplateTitle));
        OnPropertyChanged(nameof(TemplateDescription));
        OnPropertyChanged(nameof(CanManageRecipes));
        OnPropertyChanged(nameof(CanLoadSelectedRecipe));
    }

    public void SaveRecipe(string name, IReadOnlyList<string>? queueItemIds = null,
        RecipeDefaultsSnapshot? defaults = null, TemplateSnapshot? edit = null)
    {
        if (!CanManageRecipes || _templateSource is not IRecipeManagementPresentationSource manager)
            throw new InvalidOperationException("Recipe management is available only in production mode with local storage.");
        JobRecipeSnapshot recipe;
        if (edit is null)
        {
            recipe = manager.CreateRecipe(name, queueItemIds ?? [], defaults ?? new RecipeDefaultsSnapshot(1,
                PaperBatch.Core.Domain.ColorMode.Color, PaperBatch.Core.Domain.DuplexMode.Simplex,
                PaperBatch.Core.Domain.Orientation.Auto, PaperBatch.Core.Domain.ScalingMode.ShrinkToFit, "Letter"));
        }
        else
        {
            var stored = manager.GetRecipe(edit.RecipeId);
            recipe = stored with { Name = name };
        }
        manager.SaveRecipe(recipe);
        RefreshTemplates();
        SelectedTemplate = Templates.FirstOrDefault(item => item.RecipeId == recipe.RecipeId);
    }

    public void DeleteRecipe(TemplateSnapshot recipe)
    {
        if (_templateSource is not IRecipeManagementPresentationSource manager || !CanManageRecipes)
            throw new InvalidOperationException("Recipe management is unavailable.");
        manager.DeleteRecipe(recipe.RecipeId);
        RefreshTemplates();
    }

    public void ImportRecipe(string path)
    {
        if (_templateSource is not IRecipeManagementPresentationSource manager || !CanManageRecipes)
            throw new InvalidOperationException("Recipe management is unavailable.");
        manager.ImportRecipe(path);
        RefreshTemplates();
    }

    public void ExportRecipe(TemplateSnapshot recipe, string path)
    {
        if (_templateSource is not IRecipeManagementPresentationSource manager || !CanManageRecipes)
            throw new InvalidOperationException("Recipe management is unavailable.");
        manager.ExportRecipe(recipe.RecipeId, path);
    }

    public async Task LoadRecipeSourcesAsync(TemplateSnapshot recipe, bool replaceQueue, CancellationToken cancellationToken = default)
    {
        if (_templateSource is not IRecipeManagementPresentationSource manager || !CanManageRecipes ||
            _queueSource is not IQueueEditingPresentationSource editable || _queueSource is not IQueueImportPresentationSource importer)
            throw new InvalidOperationException("Recipe sources cannot be loaded in this presentation mode.");
        var savedRecipe = manager.GetRecipe(recipe.RecipeId);
        if (savedRecipe.Settings.Media is not ("Letter" or "A4"))
            throw new InvalidOperationException("This recipe requests a sheet size not supported by the desktop layout controls. Its sources were not loaded.");
        var paths = manager.GetRecipeSourceLocations(recipe.RecipeId);
        _activeRecipe = new PaperBatch.Core.Recipes.JobRecipe(savedRecipe.SchemaVersion, savedRecipe.RecipeId,
            savedRecipe.Name, savedRecipe.Sources, savedRecipe.Settings) { Rules = savedRecipe.Rules };
        OnPropertyChanged(nameof(PreparationResolutionNote));
        var imported = await importer.ImportAsync(paths, cancellationToken).ConfigureAwait(true);
        RequestedCopies = savedRecipe.Settings.Copies;
        OrientationIndex = savedRecipe.Settings.Orientation switch { Orientation.Portrait => 1, Orientation.Landscape => 2, _ => 0 };
        ScalingIndex = savedRecipe.Settings.ScalingMode == ScalingMode.ActualSize ? 1 : 0;
        MediaIndex = savedRecipe.Settings.Media == "A4" ? 1 : 0;
        DuplexIndex = savedRecipe.Settings.DuplexMode switch { DuplexMode.LongEdge => 1, DuplexMode.ShortEdge => 2, _ => 0 };
        _requestedColorMode = savedRecipe.Settings.ColorMode;
        if (replaceQueue)
        {
            var importedIds = imported.Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
            foreach (var item in QueueItems.Where(item => !importedIds.Contains(item.Id)).ToArray())
            {
                editable.Remove(item.Id);
                if (_previewSource is IPreparedArtifactPresentationSource preparedArtifacts)
                    preparedArtifacts.InvalidatePreparedRun(item.Id);
                QueueItems.Remove(item);
            }
        }
        foreach (var item in imported)
            if (!QueueItems.Any(existing => existing.Id == item.Id)) QueueItems.Add(item);
        if (SelectedQueueItem is null || !QueueItems.Contains(SelectedQueueItem))
            SelectedQueueItem = imported.FirstOrDefault(item => QueueItems.Contains(item)) ?? QueueItems.FirstOrDefault();
        OnPropertyChanged(nameof(QueueStateLabel));
        OnPropertyChanged(nameof(CanPrepareQueue));
    }

    public WorkspaceStatusSnapshot WorkspaceStatus => _workspaceStatus;

    public bool IsDemoMode => _workspaceStatus.IsDemo;

    public bool IsProductionMode => !_workspaceStatus.IsDemo;
    public bool IsChinese => _language == "zh-CN";

    public bool CanImportFiles => _queueSource is IQueueImportPresentationSource { IsAvailable: true };

    public string QueueStateLabel => QueueItems.Count == 0 ? "QUEUE EMPTY" : $"{QueueItems.Count} ITEMS IN QUEUE";

    public string PreflightHeading => _workspaceStatus.IsDemo ? "Demo checks only" : "Source preflight";

    public string SettingsHeading => _workspaceStatus.IsDemo ? "Example values and reasons" : Localize("Resolved status and provenance", "已解析状态与来源");

    public string PreviewHeading => _workspaceStatus.IsDemo ? "DEMO PREVIEW" : HasPreviewImage ? "FINAL PREVIEW · PREPARED ARTIFACT" : "FINAL PREVIEW UNAVAILABLE";

    public string PreviewDescription => _workspaceStatus.IsDemo ? "Illustrative sheet face" : HasPreviewImage
        ? $"Artifact {CurrentPreview!.ArtifactId} · SHA-256 {CurrentPreview.ArtifactSha256}"
        : "No prepared artifact preview is available.";

    public string ProductionPreviewStatus => HasPreviewImage
        ? "Rendered from the immutable final PDF saved for this prepared run."
        : "A configured PDFium preview runtime and prepared final artifact are required.";

    public bool HasPreviewImage => CurrentPreview?.PageImage is not null;

    public Task RefreshPreviewAsync() => _previewRefreshTask = RefreshSelectedItemAsync();

    /// <summary>Publishes the immutable run returned by preparation to this item's preview.</summary>
    public void RegisterPreparedRun(string queueItemId, PaperBatch.Core.Domain.PreparedRun run)
    {
        if (_previewSource is not IPreparedArtifactPresentationSource preparedArtifacts)
            throw new InvalidOperationException("Prepared-artifact preview is unavailable in this presentation mode.");
        preparedArtifacts.RegisterPreparedRun(queueItemId, run);
        RefreshHistoryEntries();
    }

    /// <summary>Removes the prior run from preview when an item is re-prepared or removed.</summary>
    public void InvalidatePreparedRun(string queueItemId)
    {
        if (_previewSource is not IPreparedArtifactPresentationSource preparedArtifacts)
            throw new InvalidOperationException("Prepared-artifact preview is unavailable in this presentation mode.");
        preparedArtifacts.InvalidatePreparedRun(queueItemId);
    }

    private void RefreshHistoryEntries()
    {
        HistoryEntries = _historySource.GetHistoryEntries();
        OnPropertyChanged(nameof(HistoryEntries));
        OnPropertyChanged(nameof(HistoryTitle));
        OnPropertyChanged(nameof(HistoryDescription));
    }

    public string HistoryEyebrow => _workspaceStatus.IsDemo ? Localize("HISTORY · SAMPLE VIEW", "历史记录 · 示例") : Localize("HISTORY", "历史记录");

    public string HistoryTitle => _workspaceStatus.IsDemo ? Localize("Example activity", "示例活动") :
        _workspaceStatus.InitializationFailed || _historySource.HistoryReadError is not null ? Localize("History unavailable", "历史记录不可用") :
        Localize(HistoryEntries.Count == 0 ? "No saved runs yet" : "Saved run history", HistoryEntries.Count == 0 ? "暂无已保存的运行记录" : "已保存的运行记录");

    public string HistoryDescription => _workspaceStatus.IsDemo
        ? Localize("These rows are illustrative. This shell does not save completed runs or print records.", "这些示例行仅供说明。此界面不会保存已完成的运行或打印记录。")
        : _workspaceStatus.InitializationFailed ? _workspaceStatus.Detail
        : _historySource.HistoryReadError is not null ? _historySource.HistoryReadError
        : Localize(HistoryEntries.Count == 0
            ? "Prepared runs will appear here when the desktop coordinator records them. No print completion is implied."
            : "Local prepared run records are shown below. Their status does not claim physical paper output.",
            HistoryEntries.Count == 0 ? "桌面协调器记录准备的运行后，这里会显示记录。此处不代表打印已完成。" : "以下为本地准备记录；其状态不表示纸张已实际输出。");

    public string TemplateEyebrow => _workspaceStatus.IsDemo ? Localize("TEMPLATES · SAMPLE VIEW", "模板 · 示例") : Localize("TEMPLATES", "模板");

    public string TemplateTitle => _workspaceStatus.IsDemo ? Localize("Example recipes", "示例配方") :
        _workspaceStatus.InitializationFailed || _templateSource.TemplateReadError is not null ? Localize("Recipes unavailable", "配方不可用") :
        Localize(Templates.Count == 0 ? "No recipes saved" : "Saved recipes", Templates.Count == 0 ? "尚未保存配方" : "已保存的配方");

    public string TemplateDescription => _workspaceStatus.IsDemo
        ? Localize("These sample cards are not saved recipes and do not change application settings.", "这些示例卡片不是已保存配方，也不会更改应用设置。")
        : _workspaceStatus.InitializationFailed ? _workspaceStatus.Detail
        : _templateSource.TemplateReadError is not null ? _templateSource.TemplateReadError
        : Localize(Templates.Count == 0
            ? "Saved recipes will appear here. Their defaults are not effective printer settings."
            : "Saved recipe defaults are shown for reference and are not effective printer settings.",
            Templates.Count == 0 ? "这里会显示已保存的配方；其默认值不是有效打印机设置。" : "已保存配方默认值仅供参考，不是有效打印机设置。");

    public string QueueDescription => _workspaceStatus.IsDemo
        ? "Selecting a row updates only the sample preview and sample settings."
        : Localize("Sources are captured and checked before preparation. PDF/JPEG/PNG preparation requires the configured PDFium runtime. This flow creates and previews a PDF only; it does not submit a print job.",
            "来源会在准备前进行捕获和检查。准备 PDF/JPEG/PNG 需要配置 PDFium 运行时。此流程仅生成并预览 PDF，不会提交打印任务。");

    public string? OperationError
    {
        get => _operationError;
        set
        {
            if (SetProperty(ref _operationError, value)) OnPropertyChanged(nameof(HasOperationError));
        }
    }

    public bool HasOperationError => !string.IsNullOrWhiteSpace(OperationError);

    public string SettingsDescription => _workspaceStatus.IsDemo
        ? "These examples do not describe a connected printer or a completed rules calculation."
        : "Effective settings are unavailable until a prepared run and printer capability snapshot exist.";

    public QueueItemSnapshot? SelectedQueueItem
    {
        get => _selectedQueueItem;
        set
        {
            if (!SetProperty(ref _selectedQueueItem, value))
            {
                return;
            }

            RefreshSelectedItem();
            (MoveQueueItemCommand as RelayCommand)?.NotifyCanExecuteChanged();
            (RemoveQueueItemCommand as RelayCommand)?.NotifyCanExecuteChanged();
        }
    }

    public PreviewSnapshot? CurrentPreview
    {
        get => _currentPreview;
        private set
        {
            if (ReferenceEquals(_currentPreview, value)) return;
            var previous = _currentPreview;
            if (!SetProperty(ref _currentPreview, value)) return;
            if (previous?.PageImage is { } oldImage && !ReferenceEquals(oldImage, value?.PageImage)) oldImage.Dispose();
            OnPropertyChanged(nameof(HasPreviewImage));
            OnPropertyChanged(nameof(PreviewHeading));
            OnPropertyChanged(nameof(PreviewDescription));
            OnPropertyChanged(nameof(ProductionPreviewStatus));
        }
    }

    public IReadOnlyList<EffectiveSettingSnapshot> EffectiveSettings
    {
        get => _effectiveSettings;
        private set => SetProperty(ref _effectiveSettings, value);
    }

    public IReadOnlyList<PreflightFindingSnapshot> PreflightFindings
    {
        get => _preflightFindings;
        private set => SetProperty(ref _preflightFindings, value);
    }

    public ShellSection ActiveSection
    {
        get => _activeSection;
        private set
        {
            if (!SetProperty(ref _activeSection, value))
            {
                return;
            }

            OnPropertyChanged(nameof(IsQueueSection));
            OnPropertyChanged(nameof(IsHistorySection));
            OnPropertyChanged(nameof(IsTemplatesSection));
            OnPropertyChanged(nameof(IsPrintersSection));
            OnPropertyChanged(nameof(ActiveSectionTitle));
        }
    }

    public bool IsQueueSection => ActiveSection == ShellSection.Queue;

    public bool IsHistorySection => ActiveSection == ShellSection.History;

    public bool IsTemplatesSection => ActiveSection == ShellSection.Templates;

    public bool IsPrintersSection => ActiveSection == ShellSection.Printers;

    public bool IsPrinterInspectionSectionVisible => CanInspectPrinterCapabilities;

    public string ActiveSectionTitle => ActiveSection switch
    {
        ShellSection.Queue => SectionQueueLabel,
        ShellSection.History => SectionHistoryLabel,
        ShellSection.Templates => SectionTemplatesLabel,
        ShellSection.Printers => SectionPrintersLabel,
        _ => "PaperBatch"
    };

    public static DesktopShellViewModel CreateProduction() => DesktopComposition.CreateProduction();

    public static DesktopShellViewModel CreateDemo()
    {
        var sources = new DemoPresentationSources();
        return new DesktopShellViewModel(sources, sources, sources, sources, sources, sources,
            new WorkspaceStatusSnapshot("DEMO MODE", "Synthetic sample data only. No files, renderers, printers, or jobs are used.", true));
    }

    private string Localize(string english, string chinese) => _language == "zh-CN" ? chinese : english;

    private bool CanMoveQueueItem(object? parameter) => SelectedQueueItem is not null &&
        int.TryParse(parameter?.ToString(), out var delta) &&
        QueueItems.IndexOf(SelectedQueueItem) + delta >= 0 && QueueItems.IndexOf(SelectedQueueItem) + delta < QueueItems.Count;

    private void MoveQueueItem(object? parameter)
    {
        if (!CanMoveQueueItem(parameter) || !int.TryParse(parameter?.ToString(), out var delta)) return;
        var index = QueueItems.IndexOf(SelectedQueueItem!);
        var selected = SelectedQueueItem!;
        QueueItems.Move(index, index + delta);
        (MoveQueueItemCommand as RelayCommand)?.NotifyCanExecuteChanged();
        if (_queueSource is IQueueEditingPresentationSource editable) editable.Move(selected.Id, delta);
        if (_previewSource is IPreparedArtifactPresentationSource preparedArtifacts)
            foreach (var item in QueueItems) preparedArtifacts.InvalidatePreparedRun(item.Id);
        OnPropertyChanged(nameof(QueueStateLabel));
        OnPropertyChanged(nameof(CanPrepareQueue));
    }

    public void MoveSelectedQueueItem(int delta) => MoveQueueItemCommand.Execute(delta);

    public void RemoveSelectedQueueItem()
    {
        if (SelectedQueueItem is not { } selected) return;
        var index = QueueItems.IndexOf(selected);
        if (_previewSource is IPreparedArtifactPresentationSource preparedArtifacts)
            preparedArtifacts.InvalidatePreparedRun(selected.Id);
        if (_queueSource is IQueueEditingPresentationSource editable) editable.Remove(selected.Id);
        QueueItems.Remove(selected);
        SelectedQueueItem = QueueItems.Count == 0 ? null : QueueItems[Math.Min(index, QueueItems.Count - 1)];
        OnPropertyChanged(nameof(QueueStateLabel));
        OnPropertyChanged(nameof(CanPrepareQueue));
    }

    private bool CanNavigate(object? parameter) => TryGetSection(parameter, out var section) &&
        (section != ShellSection.Printers || IsPrinterInspectionSectionVisible);

    private void Navigate(object? parameter)
    {
        if (CanNavigate(parameter) && TryGetSection(parameter, out var section))
        {
            ActiveSection = section;
        }
    }

    private static bool TryGetSection(object? parameter, out ShellSection section)
    {
        var value = parameter switch
        {
            ShellSection enumValue => enumValue.ToString(),
            string text => text,
            _ => string.Empty
        };

        return Enum.TryParse(value, ignoreCase: true, out section) && Enum.IsDefined(section);
    }

    private void RefreshSelectedItem()
    {
        _previewRefreshTask = RefreshSelectedItemAsync();
    }

    private async Task RefreshSelectedItemAsync()
    {
        _previewRefreshCancellation?.Cancel();
        _previewRefreshCancellation?.Dispose();
        var cancellation = new CancellationTokenSource();
        _previewRefreshCancellation = cancellation;
        var requestNumber = ++_previewRequestNumber;
        if (SelectedQueueItem is not { } item)
        {
            CurrentPreview = null;
            EffectiveSettings = [];
            PreflightFindings = [];
            return;
        }

        CurrentPreview = null;
        EffectiveSettings = _settingsSource.GetEffectiveSettings(item.Id);
        PreflightFindings = _preflightSource.GetFindings(item.Id);
        try
        {
            var preview = await _previewSource.GetPreviewAsync(item.Id, cancellation.Token);
            if (cancellation.IsCancellationRequested || requestNumber != _previewRequestNumber)
            {
                preview.PageImage?.Dispose();
                return;
            }
            CurrentPreview = preview;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            if (!cancellation.IsCancellationRequested && requestNumber == _previewRequestNumber)
                CurrentPreview = new PreviewSnapshot(item.Id, $"Prepared PDF preview unavailable ({exception.GetType().Name}).", 0, 0);
        }
    }

    private void OnPreparedArtifactChanged(string queueItemId)
    {
        if (string.Equals(SelectedQueueItem?.Id, queueItemId, StringComparison.Ordinal))
        {
            if (Dispatcher.UIThread.CheckAccess()) RefreshSelectedItem();
            else Dispatcher.UIThread.Post(RefreshSelectedItem);
        }
    }

    private bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void OnPropertyChanged(string? propertyName) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    private sealed class RelayCommand(Action<object?> execute, Predicate<object?> canExecute) : ICommand
    {
        public event EventHandler? CanExecuteChanged;

        public void NotifyCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);

        public bool CanExecute(object? parameter) => canExecute(parameter);

        public void Execute(object? parameter) => execute(parameter);
    }
}
