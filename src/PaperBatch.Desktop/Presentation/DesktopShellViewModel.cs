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

    public DesktopShellViewModel(
        IQueuePresentationSource queueSource,
        IEffectiveSettingsPresentationSource settingsSource,
        IFinalPreviewPresentationSource previewSource,
        IPreflightPresentationSource preflightSource,
        IHistoryPresentationSource historySource,
        ITemplatePresentationSource templateSource)
    {
        ArgumentNullException.ThrowIfNull(queueSource);
        ArgumentNullException.ThrowIfNull(settingsSource);
        ArgumentNullException.ThrowIfNull(previewSource);
        ArgumentNullException.ThrowIfNull(preflightSource);
        ArgumentNullException.ThrowIfNull(historySource);
        ArgumentNullException.ThrowIfNull(templateSource);

        _settingsSource = settingsSource;
        _previewSource = previewSource;
        _preflightSource = preflightSource;
        QueueItems = new ObservableCollection<QueueItemSnapshot>(queueSource.GetQueueItems());
        HistoryEntries = historySource.GetHistoryEntries();
        Templates = templateSource.GetTemplates();
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
        ShellSection.History => "History · sample view",
        ShellSection.Templates => "Templates · sample view",
        _ => "PaperBatch"
    };

    public static DesktopShellViewModel CreateDemo()
    {
        var sources = new DemoPresentationSources();
        return new DesktopShellViewModel(sources, sources, sources, sources, sources, sources);
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
