using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using PaperBatch.Desktop.Presentation;

namespace PaperBatch.Desktop;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var demo = desktop.Args?.Contains("--demo", StringComparer.Ordinal) == true;
            desktop.MainWindow = new MainWindow(demo ? DesktopShellViewModel.CreateDemo() : DesktopShellViewModel.CreateProduction());
        }

        base.OnFrameworkInitializationCompleted();
    }
}
