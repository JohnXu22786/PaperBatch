using Avalonia.Controls;
using PaperBatch.Desktop.Presentation;

namespace PaperBatch.Desktop;

internal partial class MainWindow : Window
{
    public MainWindow(DesktopShellViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }
}
