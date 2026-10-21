using Avalonia;

namespace PaperBatch.Desktop;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        if (PaperBatch.Core.Ipc.CoordinatorProcessLauncher.IsHostMode(args))
        {
            RunCoordinatorHost();
            return;
        }
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    private static void RunCoordinatorHost()
    {
        using var shutdown = new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArgs) => { eventArgs.Cancel = true; shutdown.Cancel(); };
        var host = new PaperBatch.Core.Ipc.CoordinatorRuntimeHost("PaperBatch", new PaperBatch.Core.Ipc.ProductionCoordinatorRequestHandler());
        if (!host.IsOwner) return;
        try { host.RunAsync(shutdown.Token).GetAwaiter().GetResult(); }
        finally { host.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();
}
