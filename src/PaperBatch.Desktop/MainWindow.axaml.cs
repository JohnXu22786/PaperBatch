using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Avalonia;
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

    private async void CreateRecipeClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        try
        {
            var result = await RecipeEditorWindow.ShowAsync(this, null, _viewModel.IsChinese);
            if (result is null) return;
            _viewModel.SaveRecipe(result.Name, _viewModel.QueueItems.Select(item => item.Id).ToArray(), result.Defaults);
            _viewModel.OperationError = null;
        }
        catch (Exception exception) { _viewModel.OperationError = _viewModel.IsChinese ? $"配方无法保存（{exception.GetType().Name}）。" : $"Recipe could not be saved ({exception.GetType().Name})."; }
    }

    private async void RenameRecipeClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_viewModel.SelectedTemplate is not { } selected) return;
        try
        {
            var result = await RecipeEditorWindow.ShowAsync(this, selected.Name, _viewModel.IsChinese, renameOnly: true);
            if (result is null) return;
            _viewModel.SaveRecipe(result.Name, edit: selected);
            _viewModel.OperationError = null;
        }
        catch (Exception exception) { _viewModel.OperationError = _viewModel.IsChinese ? $"配方无法重命名（{exception.GetType().Name}）。" : $"Recipe could not be renamed ({exception.GetType().Name})."; }
    }

    private async void DeleteRecipeClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_viewModel.SelectedTemplate is not { } selected) return;
        if (await RecipeConfirmWindow.ShowAsync(this, selected.Name, _viewModel.IsChinese) != true) return;
        try { _viewModel.DeleteRecipe(selected); _viewModel.OperationError = null; }
        catch (Exception exception) { _viewModel.OperationError = _viewModel.IsChinese ? $"配方无法删除（{exception.GetType().Name}）。" : $"Recipe could not be deleted ({exception.GetType().Name})."; }
    }

    private async void ImportRecipeClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        try
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { AllowMultiple = false,
                Title = _viewModel.IsChinese ? "导入配方" : "Import recipe", FileTypeFilter = [new FilePickerFileType("PaperBatch recipe") { Patterns = ["*.json"] }] });
            var path = files.FirstOrDefault()?.TryGetLocalPath();
            if (path is null) return;
            _viewModel.ImportRecipe(path);
            _viewModel.OperationError = null;
        }
        catch (Exception exception) { _viewModel.OperationError = _viewModel.IsChinese ? $"导入配方失败（{exception.GetType().Name}）；未覆盖已保存配方。" : $"Recipe import failed ({exception.GetType().Name}); saved recipes were not overwritten."; }
    }

    private async void ExportRecipeClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_viewModel.SelectedTemplate is not { } selected) return;
        try
        {
            var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions { Title = _viewModel.IsChinese ? "导出配方" : "Export recipe",
                SuggestedFileName = selected.Name + ".json", FileTypeChoices = [new FilePickerFileType("PaperBatch recipe") { Patterns = ["*.json"] }] });
            var path = file?.TryGetLocalPath();
            if (path is null) return;
            _viewModel.ExportRecipe(selected, path);
            _viewModel.OperationError = null;
        }
        catch (Exception exception) { _viewModel.OperationError = _viewModel.IsChinese ? $"导出配方失败（{exception.GetType().Name}）；不会覆盖现有文件。" : $"Recipe export failed ({exception.GetType().Name}); existing files are never overwritten."; }
    }

    private async void LoadRecipeClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_viewModel.SelectedTemplate is not { } selected) return;
        var choice = await RecipeQueueChoiceWindow.ShowAsync(this, _viewModel.IsChinese);
        if (choice is null) return;
        try
        {
            await _viewModel.LoadRecipeSourcesAsync(selected, choice.Value);
            _viewModel.OperationError = null;
            _viewModel.NavigateCommand.Execute("Queue");
        }
        catch (Exception exception) { _viewModel.OperationError = _viewModel.IsChinese ? $"无法加载已保存路径（{exception.GetType().Name}）；未提交打印任务。" : $"Saved paths could not be loaded ({exception.GetType().Name}); no print job was submitted."; }
    }

    private void RefreshRecipesClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => _viewModel.RefreshTemplates();

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

internal sealed record RecipeEditorResult(string Name, RecipeDefaultsSnapshot Defaults);

internal sealed class RecipeEditorWindow : Window
{
    private readonly TextBox _name;
    private readonly TextBox _copies = new() { Text = "1", MaxLength = 3 };
    private readonly TextBox _media = new() { Text = "Letter", MaxLength = 128 };
    private readonly ComboBox _color;
    private readonly ComboBox _duplex;
    private readonly ComboBox _orientation;
    private readonly ComboBox _scaling;
    private RecipeEditorResult? _result;

    private RecipeEditorWindow(string? currentName, bool chinese, bool renameOnly)
    {
        Title = chinese ? "配方" : "Recipe";
        Width = 420; Height = renameOnly ? 220 : 520; CanResize = false;
        _name = new TextBox { PlaceholderText = chinese ? "配方名称" : "Recipe name", MaxLength = 160 };
        _color = Choices(chinese, (PaperBatch.Core.Domain.ColorMode.Monochrome, "Monochrome", "黑白"), (PaperBatch.Core.Domain.ColorMode.Color, "Color", "彩色"));
        _color.SelectedIndex = 1;
        _duplex = Choices(chinese, (PaperBatch.Core.Domain.DuplexMode.Simplex, "Simplex", "单面"),
            (PaperBatch.Core.Domain.DuplexMode.LongEdge, "Long edge", "双面长边翻转"), (PaperBatch.Core.Domain.DuplexMode.ShortEdge, "Short edge", "双面短边翻转"));
        _orientation = Choices(chinese, (PaperBatch.Core.Domain.Orientation.Auto, "Auto", "自动"),
            (PaperBatch.Core.Domain.Orientation.Portrait, "Portrait", "纵向"), (PaperBatch.Core.Domain.Orientation.Landscape, "Landscape", "横向"));
        _scaling = Choices(chinese, (PaperBatch.Core.Domain.ScalingMode.ShrinkToFit, "Shrink to fit", "缩小以适应"),
            (PaperBatch.Core.Domain.ScalingMode.ActualSize, "Actual size", "原始大小"));
        _name.Text = currentName ?? string.Empty;
        var body = new StackPanel { Margin = new Thickness(22), Spacing = 10 };
        body.Children.Add(new TextBlock { Text = chinese ? "配方名称" : "Recipe name" });
        body.Children.Add(_name);
        if (!renameOnly)
        {
            body.Children.Add(new TextBlock { Text = chinese ? "副本数（配方参考值）" : "Copies (recipe default)" }); body.Children.Add(_copies);
            body.Children.Add(new TextBlock { Text = chinese ? "颜色模式（参考值）" : "Color mode (reference only)" }); body.Children.Add(_color);
            body.Children.Add(new TextBlock { Text = chinese ? "双面模式（参考值）" : "Duplex mode (reference only)" }); body.Children.Add(_duplex);
            body.Children.Add(new TextBlock { Text = chinese ? "方向（参考值）" : "Orientation (reference only)" }); body.Children.Add(_orientation);
            body.Children.Add(new TextBlock { Text = chinese ? "缩放（参考值）" : "Scaling (reference only)" }); body.Children.Add(_scaling);
            body.Children.Add(new TextBlock { Text = chinese ? "纸张名称（参考值，不会选择打印机）" : "Media name (reference only; no printer is selected)" }); body.Children.Add(_media);
        }
        var actions = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right, Spacing = 8 };
        var cancel = new Button { Content = chinese ? "取消" : "Cancel" };
        cancel.Click += (_, _) => Close();
        var save = new Button { Content = chinese ? "保存" : "Save", IsDefault = true };
        save.Click += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(_name.Text)) return;
            if (!int.TryParse(_copies.Text, out var count) || count is < 1 or > 999) return;
            _result = new RecipeEditorResult(_name.Text.Trim(), new RecipeDefaultsSnapshot(count,
                ChoiceValue(_color, PaperBatch.Core.Domain.ColorMode.Color),
                ChoiceValue(_duplex, PaperBatch.Core.Domain.DuplexMode.Simplex),
                ChoiceValue(_orientation, PaperBatch.Core.Domain.Orientation.Auto),
                ChoiceValue(_scaling, PaperBatch.Core.Domain.ScalingMode.ShrinkToFit),
                string.IsNullOrWhiteSpace(_media.Text) ? "Letter" : _media.Text.Trim()));
            Close();
        };
        actions.Children.Add(cancel); actions.Children.Add(save); body.Children.Add(actions);
        Content = body;
    }

    public static async Task<RecipeEditorResult?> ShowAsync(Window owner, string? name, bool chinese, bool renameOnly = false)
    {
        var dialog = new RecipeEditorWindow(name, chinese, renameOnly);
        await dialog.ShowDialog(owner);
        return dialog._result;
    }

    private static ComboBox Choices<T>(bool chinese, params (T Value, string English, string Chinese)[] values)
    {
        var combo = new ComboBox();
        foreach (var (value, english, translated) in values)
            combo.Items.Add(new ComboBoxItem { Content = chinese ? translated : english, Tag = value });
        combo.SelectedIndex = 0;
        return combo;
    }

    private static T ChoiceValue<T>(ComboBox combo, T fallback) =>
        combo.SelectedItem is ComboBoxItem { Tag: T selected } ? selected : fallback;
}

internal sealed class RecipeConfirmWindow : Window
{
    private bool _confirmed;
    private RecipeConfirmWindow(string name, bool chinese)
    {
        Title = chinese ? "删除配方" : "Delete recipe"; Width = 430; Height = 190; CanResize = false;
        var body = new StackPanel { Margin = new Thickness(22), Spacing = 14 };
        body.Children.Add(new TextBlock { Text = chinese ? $"确定删除“{name}”吗？" : $"Delete “{name}”?", TextWrapping = Avalonia.Media.TextWrapping.Wrap });
        var buttons = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right, Spacing = 8 };
        var cancel = new Button { Content = chinese ? "取消" : "Cancel" }; cancel.Click += (_, _) => Close();
        var delete = new Button { Content = chinese ? "删除" : "Delete" }; delete.Click += (_, _) => { _confirmed = true; Close(); };
        buttons.Children.Add(cancel); buttons.Children.Add(delete); body.Children.Add(buttons); Content = body;
    }
    public static async Task<bool> ShowAsync(Window owner, string name, bool chinese)
    {
        var dialog = new RecipeConfirmWindow(name, chinese); await dialog.ShowDialog(owner); return dialog._confirmed;
    }
}

internal sealed class RecipeQueueChoiceWindow : Window
{
    private bool? _choice;
    private RecipeQueueChoiceWindow(bool chinese)
    {
        Title = chinese ? "加载配方来源" : "Load recipe sources"; Width = 490; Height = 230; CanResize = false;
        var body = new StackPanel { Margin = new Thickness(22), Spacing = 12 };
        body.Children.Add(new TextBlock { Text = chinese ? "来源会重新检查并加入队列。此操作不会打印。选择如何处理现有队列：" : "Sources will be revalidated and added to the queue. Nothing will print. Choose how to handle the current queue:", TextWrapping = Avalonia.Media.TextWrapping.Wrap });
        var buttons = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right, Spacing = 8 };
        var cancel = new Button { Content = chinese ? "取消" : "Cancel" }; cancel.Click += (_, _) => Close();
        var add = new Button { Content = chinese ? "追加到队列" : "Add to queue" }; add.Click += (_, _) => { _choice = false; Close(); };
        var replace = new Button { Content = chinese ? "替换队列" : "Replace queue" }; replace.Click += (_, _) => { _choice = true; Close(); };
        buttons.Children.Add(cancel); buttons.Children.Add(add); buttons.Children.Add(replace); body.Children.Add(buttons); Content = body;
    }
    public static async Task<bool?> ShowAsync(Window owner, bool chinese)
    {
        var dialog = new RecipeQueueChoiceWindow(chinese); await dialog.ShowDialog(owner); return dialog._choice;
    }
}
