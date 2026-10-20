using Avalonia.Controls;
using Avalonia.Platform.Storage;
using PaperBatch.Desktop.Presentation;

namespace PaperBatch.Desktop;

internal partial class MainWindow : Window
{
    private readonly DesktopShellViewModel _viewModel;

    public MainWindow(DesktopShellViewModel viewModel)
    {
        _viewModel = viewModel;
        InitializeComponent();
        DataContext = viewModel;
    }

    private async void AddDocumentsClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        try
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                AllowMultiple = true,
                Title = "Add documents to the queue"
            });
            if (files.Count == 0) return;
            var selection = ClassifySelectedPaths(files.Select(file => file.TryGetLocalPath()));
            if (selection.Paths.Count == 0)
            {
                _viewModel.OperationError = selection.SkippedMessage(noItemsAdded: true);
                return;
            }
            await _viewModel.ImportFilesAsync(selection.Paths);
            _viewModel.OperationError = selection.SkippedCount == 0 ? null : selection.SkippedMessage(noItemsAdded: false);
        }
        catch (Exception exception)
        {
            _viewModel.OperationError = $"File intake is unavailable ({exception.GetType().Name}). No print job was submitted.";
        }
    }

    internal static LocalFileSelection ClassifySelectedPaths(IEnumerable<string?> paths)
    {
        var candidates = paths.ToArray();
        var localPaths = candidates.Where(path => !string.IsNullOrWhiteSpace(path)).Cast<string>().ToArray();
        return new LocalFileSelection(localPaths, candidates.Length - localPaths.Length);
    }
}

internal sealed record LocalFileSelection(IReadOnlyList<string> Paths, int SkippedCount)
{
    public string SkippedMessage(bool noItemsAdded) =>
        $"{SkippedCount} selected item(s) could not be opened because they do not expose a local file path.{(noItemsAdded ? " No items were added." : string.Empty)}";
}
