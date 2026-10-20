using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using Avalonia.Threading;

namespace PaperBatch.Desktop.Presentation;

internal sealed class DesktopShellViewModel : INotifyPropertyChanged
{
    private readonly IEffectiveSettingsPresentationSource _settingsSource;
    private readonly IFinalPreviewPresentationSource _previewSource;
    private readonly IPreflightPresentationSource _preflightSource;
    private readonly IHistoryPresentationSource _historySource;
    private readonly ITemplatePresentationSource _templateSource;
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
        QueueItems = new ObservableCollection<QueueItemSnapshot>(queueSource.GetQueueItems());
        HistoryEntries = historySource.GetHistoryEntries();
        Templates = templateSource.GetTemplates();
        _workspaceStatus = workspaceStatus ?? new WorkspaceStatusSnapshot("Demo mode", "Synthetic sample data only.", true);
        NavigateCommand = new RelayCommand(Navigate, CanNavigate);
        MoveQueueItemCommand = new RelayCommand(MoveQueueItem, CanMoveQueueItem);
        RemoveQueueItemCommand = new RelayCommand(_ => RemoveSelectedQueueItem(), _ => SelectedQueueItem is not null);

        if (QueueItems.Count > 0)
        {
            SelectedQueueItem = QueueItems[0];
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<QueueItemSnapshot> QueueItems { get; }

    public IReadOnlyList<HistoryEntrySnapshot> HistoryEntries { get; private set; }

    public IReadOnlyList<TemplateSnapshot> Templates { get; private set; }

    public ICommand NavigateCommand { get; }

    public ICommand MoveQueueItemCommand { get; }

    public ICommand RemoveQueueItemCommand { get; }

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
        foreach (var property in new[] { nameof(SectionQueueLabel), nameof(SectionHistoryLabel), nameof(SectionTemplatesLabel),
                     nameof(ActiveSectionTitle), nameof(HistoryEyebrow), nameof(HistoryTitle), nameof(HistoryDescription), nameof(HistoryEntries), nameof(TemplateEyebrow), nameof(TemplateTitle), nameof(TemplateDescription), nameof(Templates) })
            OnPropertyChanged(property);
    }

    public string SectionQueueLabel => Localize("Queue and review", "队列与审核");
    public string SectionHistoryLabel => Localize("History", "历史记录");
    public string SectionTemplatesLabel => Localize("Templates", "模板");


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
        SelectedQueueItem ??= QueueItems.FirstOrDefault();
        OnPropertyChanged(nameof(QueueStateLabel));
    }

    public WorkspaceStatusSnapshot WorkspaceStatus => _workspaceStatus;

    public bool IsDemoMode => _workspaceStatus.IsDemo;

    public bool IsProductionMode => !_workspaceStatus.IsDemo;

    public bool CanImportFiles => _queueSource is IQueueImportPresentationSource { IsAvailable: true };

    public string QueueStateLabel => QueueItems.Count == 0 ? "QUEUE EMPTY" : $"{QueueItems.Count} ITEMS IN QUEUE";

    public string PreflightHeading => _workspaceStatus.IsDemo ? "Demo checks only" : "Source preflight";

    public string SettingsHeading => _workspaceStatus.IsDemo ? "Example values and reasons" : "Effective values and provenance";

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
    }

    /// <summary>Removes the prior run from preview when an item is re-prepared or removed.</summary>
    public void InvalidatePreparedRun(string queueItemId)
    {
        if (_previewSource is not IPreparedArtifactPresentationSource preparedArtifacts)
            throw new InvalidOperationException("Prepared-artifact preview is unavailable in this presentation mode.");
        preparedArtifacts.InvalidatePreparedRun(queueItemId);
    }

    public string HistoryEyebrow => _workspaceStatus.IsDemo ? Localize("HISTORY · SAMPLE VIEW", "历史记录 · 示例") : Localize("HISTORY", "历史记录");

    public string HistoryTitle => _workspaceStatus.IsDemo ? Localize("Example activity", "示例活动") : Localize("Print history unavailable", "打印历史记录不可用");

    public string HistoryDescription => _workspaceStatus.IsDemo
        ? Localize("These rows are illustrative. This shell does not save completed runs or print records.", "这些示例行仅供说明。此界面不会保存已完成的运行或打印记录。")
        : Localize("No persisted runs are available in the current core read API.", "当前核心读取接口无法提供已保存的运行记录。");

    public string TemplateEyebrow => _workspaceStatus.IsDemo ? Localize("TEMPLATES · SAMPLE VIEW", "模板 · 示例") : Localize("TEMPLATES", "模板");

    public string TemplateTitle => _workspaceStatus.IsDemo ? Localize("Example recipes", "示例配方") : Localize("Saved recipes unavailable", "已保存的配方不可用");

    public string TemplateDescription => _workspaceStatus.IsDemo
        ? Localize("These sample cards are not saved recipes and do not change application settings.", "这些示例卡片不是已保存配方，也不会更改应用设置。")
        : Localize("Recipe persistence and application are unavailable in the current core API.", "当前核心接口不支持配方持久化和应用。");

    public string QueueDescription => _workspaceStatus.IsDemo
        ? "Selecting a row updates only the sample preview and sample settings."
        : "Source files are captured and checked by the shared core. Rendering and print submission are unavailable.";

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
            OnPropertyChanged(nameof(ActiveSectionTitle));
        }
    }

    public bool IsQueueSection => ActiveSection == ShellSection.Queue;

    public bool IsHistorySection => ActiveSection == ShellSection.History;

    public bool IsTemplatesSection => ActiveSection == ShellSection.Templates;

    public string ActiveSectionTitle => ActiveSection switch
    {
        ShellSection.Queue => SectionQueueLabel,
        ShellSection.History => SectionHistoryLabel,
        ShellSection.Templates => SectionTemplatesLabel,
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
        if (_queueSource is IQueueEditingPresentationSource editable) editable.Move(selected.Id, delta);
        if (_previewSource is IPreparedArtifactPresentationSource preparedArtifacts)
            foreach (var item in QueueItems) preparedArtifacts.InvalidatePreparedRun(item.Id);
        OnPropertyChanged(nameof(QueueStateLabel));
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
    }

    private static bool CanNavigate(object? parameter) => TryGetSection(parameter, out _);

    private void Navigate(object? parameter)
    {
        if (TryGetSection(parameter, out var section))
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
