using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;

namespace PaperBatch.Desktop.Presentation;

internal sealed class DesktopShellViewModel : INotifyPropertyChanged
{
    private readonly IEffectiveSettingsPresentationSource _settingsSource;
    private readonly IFinalPreviewPresentationSource _previewSource;
    private readonly IPreflightPresentationSource _preflightSource;
    private QueueItemSnapshot? _selectedQueueItem;
    private PreviewSnapshot? _currentPreview;
    private IReadOnlyList<EffectiveSettingSnapshot> _effectiveSettings = [];
    private IReadOnlyList<PreflightFindingSnapshot> _preflightFindings = [];
    private ShellSection _activeSection = ShellSection.Queue;
    private readonly WorkspaceStatusSnapshot _workspaceStatus;
    private string? _operationError;
    private readonly IQueuePresentationSource _queueSource;

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
        _preflightSource = preflightSource;
        QueueItems = new ObservableCollection<QueueItemSnapshot>(queueSource.GetQueueItems());
        HistoryEntries = historySource.GetHistoryEntries();
        Templates = templateSource.GetTemplates();
        _workspaceStatus = workspaceStatus ?? new WorkspaceStatusSnapshot("Demo mode", "Synthetic sample data only.", true);
        NavigateCommand = new RelayCommand(Navigate, CanNavigate);

        if (QueueItems.Count > 0)
        {
            SelectedQueueItem = QueueItems[0];
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<QueueItemSnapshot> QueueItems { get; }

    public IReadOnlyList<HistoryEntrySnapshot> HistoryEntries { get; }

    public IReadOnlyList<TemplateSnapshot> Templates { get; }

    public ICommand NavigateCommand { get; }

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

    public string PreviewHeading => _workspaceStatus.IsDemo ? "DEMO PREVIEW" : "FINAL PREVIEW UNAVAILABLE";

    public string PreviewDescription => _workspaceStatus.IsDemo ? "Illustrative sheet face" : "No prepared artifact is available.";

    public string HistoryEyebrow => _workspaceStatus.IsDemo ? "HISTORY · SAMPLE VIEW" : "HISTORY";

    public string HistoryTitle => _workspaceStatus.IsDemo ? "Example activity" : "Print history unavailable";

    public string HistoryDescription => _workspaceStatus.IsDemo
        ? "These rows are illustrative. This shell does not save completed runs or print records."
        : "No persisted runs are available in the current core read API.";

    public string TemplateEyebrow => _workspaceStatus.IsDemo ? "TEMPLATES · SAMPLE VIEW" : "TEMPLATES";

    public string TemplateTitle => _workspaceStatus.IsDemo ? "Example recipes" : "Saved recipes unavailable";

    public string TemplateDescription => _workspaceStatus.IsDemo
        ? "These sample cards are not saved recipes and do not change application settings."
        : "Recipe persistence and application are unavailable in the current core API.";

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
        }
    }

    public PreviewSnapshot? CurrentPreview
    {
        get => _currentPreview;
        private set => SetProperty(ref _currentPreview, value);
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
        ShellSection.Queue => "Queue and review",
        ShellSection.History => "History",
        ShellSection.Templates => "Templates",
        _ => "PaperBatch"
    };

    public static DesktopShellViewModel CreateProduction() => DesktopComposition.CreateProduction();

    public static DesktopShellViewModel CreateDemo()
    {
        var sources = new DemoPresentationSources();
        return new DesktopShellViewModel(sources, sources, sources, sources, sources, sources,
            new WorkspaceStatusSnapshot("DEMO MODE", "Synthetic sample data only. No files, renderers, printers, or jobs are used.", true));
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
        if (SelectedQueueItem is not { } item)
        {
            CurrentPreview = null;
            EffectiveSettings = [];
            PreflightFindings = [];
            return;
        }

        CurrentPreview = _previewSource.GetPreview(item.Id);
        EffectiveSettings = _settingsSource.GetEffectiveSettings(item.Id);
        PreflightFindings = _preflightSource.GetFindings(item.Id);
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
        public event EventHandler? CanExecuteChanged
        {
            add { }
            remove { }
        }

        public bool CanExecute(object? parameter) => canExecute(parameter);

        public void Execute(object? parameter) => execute(parameter);
    }
}
