using PaperBatch.Core.Intake;
using PaperBatch.Core.Rendering;

internal static class RendererTests
{
    public static void LibreOfficeRejectsSharedOperationDirectory()
    {
        if (OperatingSystem.IsWindows()) return;
        var operationDirectory = Path.Combine(Path.GetTempPath(), $"paperbatch-render-shared-{Guid.NewGuid():N}");
        Directory.CreateDirectory(operationDirectory);
        try
        {
            File.SetUnixFileMode(operationDirectory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute);
            Assert.Throws<IOException>(() => LibreOfficeProfilePolicy.CreatePrivateProfile(operationDirectory));
        }
        finally
        {
            Directory.Delete(operationDirectory, recursive: true);
        }
    }

    public static void LibreOfficeRejectsReplaceableOperationPathAncestors()
    {
        if (OperatingSystem.IsWindows()) return;
        var sharedParent = Path.Combine(Path.GetTempPath(), $"paperbatch-shared-parent-{Guid.NewGuid():N}");
        var operationDirectory = Path.Combine(sharedParent, "operation");
        Directory.CreateDirectory(operationDirectory);
        try
        {
            File.SetUnixFileMode(sharedParent, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute);
            File.SetUnixFileMode(operationDirectory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            Assert.Throws<IOException>(() => LibreOfficeProfilePolicy.CreatePrivateProfile(operationDirectory));
        }
        finally
        {
            Directory.Delete(sharedParent, recursive: true);
        }
    }

    public static void LibreOfficeUsesPrivateMacroRestrictedProfile()
    {
        var operationDirectory = Path.Combine(Path.GetTempPath(), $"paperbatch-render-{Guid.NewGuid():N}");
        Directory.CreateDirectory(operationDirectory);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(operationDirectory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        try
        {
            var profileUri = LibreOfficeProfilePolicy.CreatePrivateProfile(operationDirectory);
            var profilePath = profileUri.LocalPath;
            Assert.True(Path.GetFullPath(profilePath).StartsWith(Path.GetFullPath(operationDirectory) + Path.DirectorySeparatorChar, StringComparison.Ordinal));
            var settings = File.ReadAllText(Path.Combine(profilePath, "registrymodifications.xcu"));
            Assert.Contains("MacroSecurityLevel", settings);
            Assert.Contains("<value>3</value>", settings);
            Assert.Contains("/org.openoffice.Office.Common/Update/Links", settings);
            Assert.Contains("<value>0</value>", settings);
        }
        finally
        {
            Directory.Delete(operationDirectory, recursive: true);
        }
    }

    public static void OwnedProcessWorkerWaitsForSuccessfulChild()
    {
        var result = OwnedProcessWorker.RunAsync(CreateChildProcess("exit", null), TimeSpan.FromSeconds(10))
            .GetAwaiter().GetResult();
        Assert.Equal(OwnedProcessStatus.Exited, result.Status);
        Assert.Equal(0, result.ExitCode);
        Assert.True(result.ProcessId > 0);
    }

    public static void OwnedProcessWorkerKillsOnlyItsChildOnTimeout()
    {
        var marker = Path.Combine(Path.GetTempPath(), $"paperbatch-child-{Guid.NewGuid():N}.pid");
        try
        {
            var task = OwnedProcessWorker.RunAsync(CreateChildProcess("hang", marker), TimeSpan.FromMilliseconds(100));
            var result = task.GetAwaiter().GetResult();
            Assert.Equal(OwnedProcessStatus.TimedOut, result.Status);
            Assert.True(result.ProcessId > 0);
            Assert.True(ChildHasExited(result.ProcessId!.Value));
        }
        finally
        {
            if (File.Exists(marker)) File.Delete(marker);
        }
    }

    public static void OwnedProcessWorkerKillsItsChildOnCancellation()
    {
        var marker = Path.Combine(Path.GetTempPath(), $"paperbatch-child-{Guid.NewGuid():N}.pid");
        try
        {
            using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
            var result = OwnedProcessWorker.RunAsync(CreateChildProcess("hang", marker), TimeSpan.FromSeconds(10), cancellation.Token)
                .GetAwaiter().GetResult();
            Assert.Equal(OwnedProcessStatus.Cancelled, result.Status);
            Assert.True(result.ProcessId > 0);
            Assert.True(ChildHasExited(result.ProcessId!.Value));
        }
        finally
        {
            if (File.Exists(marker)) File.Delete(marker);
        }
    }

    private static System.Diagnostics.ProcessStartInfo CreateChildProcess(string mode, string? marker)
    {
        var executable = Environment.ProcessPath ?? throw new InvalidOperationException("Test host path is unavailable.");
        var startInfo = new System.Diagnostics.ProcessStartInfo(executable) { UseShellExecute = false };
        var entryAssembly = System.Reflection.Assembly.GetEntryAssembly()?.Location;
        if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        {
            startInfo.ArgumentList.Add(entryAssembly ?? throw new InvalidOperationException("Test assembly path is unavailable."));
        }
        startInfo.ArgumentList.Add("--renderer-worker-child");
        startInfo.ArgumentList.Add(mode);
        if (marker is not null) startInfo.ArgumentList.Add(marker);
        return startInfo;
    }

    private static bool ChildHasExited(int processId)
    {
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(processId);
            return process.HasExited;
        }
        catch (ArgumentException)
        {
            return true;
        }
    }

    public static void UnsupportedFormatIsReportedWithoutFallback()
    {
        var adapter = new FakeAdapter(SourceFormat.Docx, RendererExecutionContext.BackgroundWorker,
            (_, _) => Task.FromResult(new RenderResult(RenderStatus.Failed, null, "render failed")));
        var other = new FakeAdapter(SourceFormat.Docx, RendererExecutionContext.BackgroundWorker,
            (_, _) => throw new InvalidOperationException("fallback ran"));
        var coordinator = new RendererCoordinator([adapter, other]);

        var result = coordinator.RenderAsync(new RenderRequest("snapshot", SourceFormat.Docx,
            RendererExecutionContext.BackgroundWorker), TimeSpan.FromSeconds(1)).GetAwaiter().GetResult();

        Assert.Equal(RenderStatus.Failed, result.Status);
        Assert.Equal(1, adapter.CallCount);
        Assert.Equal(0, other.CallCount);
    }

    public static void MissingAdapterIsExplicitlyUnsupported()
    {
        var coordinator = new RendererCoordinator([]);
        var result = coordinator.RenderAsync(new RenderRequest("snapshot", SourceFormat.Pdf,
            RendererExecutionContext.BackgroundWorker), TimeSpan.FromSeconds(1)).GetAwaiter().GetResult();
        Assert.Equal(RenderStatus.Unsupported, result.Status);
    }

    public static void CancelledRequestDoesNotLaunchAnAdapter()
    {
        var adapter = new FakeAdapter(SourceFormat.Pdf, RendererExecutionContext.BackgroundWorker,
            (_, _) => throw new InvalidOperationException("cancelled request launched"));
        var coordinator = new RendererCoordinator([adapter]);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var result = coordinator.RenderAsync(new RenderRequest("snapshot", SourceFormat.Pdf,
            RendererExecutionContext.BackgroundWorker), TimeSpan.FromSeconds(1), cancellation.Token).GetAwaiter().GetResult();

        Assert.Equal(RenderStatus.Cancelled, result.Status);
        Assert.Equal(0, adapter.CallCount);
    }

    public static void CapabilityFailureStopsWithoutTryingAnotherAdapter()
    {
        var broken = new FakeAdapter(SourceFormat.Pdf, RendererExecutionContext.BackgroundWorker,
            (_, _) => Task.FromResult(new RenderResult(RenderStatus.Succeeded, null, null))) { ThrowOnCanRender = true };
        var other = new FakeAdapter(SourceFormat.Pdf, RendererExecutionContext.BackgroundWorker,
            (_, _) => throw new InvalidOperationException("fallback ran"));
        var coordinator = new RendererCoordinator([broken, other]);

        var result = coordinator.RenderAsync(new RenderRequest("snapshot", SourceFormat.Pdf,
            RendererExecutionContext.BackgroundWorker), TimeSpan.FromSeconds(1)).GetAwaiter().GetResult();

        Assert.Equal(RenderStatus.Failed, result.Status);
        Assert.Equal(0, other.CallCount);
    }

    public static void CapabilityCheckIsInsideTheRenderDeadline()
    {
        var capabilityCancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var adapter = new FakeAdapter(SourceFormat.Pdf, RendererExecutionContext.BackgroundWorker,
            (_, _) => throw new InvalidOperationException("render should not start"))
        {
            CapabilityCheck = async cancellationToken =>
            {
                using var registration = cancellationToken.Register(() => capabilityCancelled.TrySetResult());
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return true;
            }
        };
        var coordinator = new RendererCoordinator([adapter]);

        var result = coordinator.RenderAsync(new RenderRequest("snapshot", SourceFormat.Pdf,
            RendererExecutionContext.BackgroundWorker), TimeSpan.FromMilliseconds(25)).GetAwaiter().GetResult();

        Assert.Equal(RenderStatus.TimedOut, result.Status);
        Assert.True(capabilityCancelled.Task.Wait(TimeSpan.FromSeconds(1)));
    }

    public static void ThrowingCancellationCallbacksDoNotHideTimeout()
    {
        var adapter = new FakeAdapter(SourceFormat.Pdf, RendererExecutionContext.BackgroundWorker,
            async (_, cancellationToken) =>
            {
                using var registration = cancellationToken.Register(() => throw new InvalidOperationException("callback failed"));
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return new RenderResult(RenderStatus.Succeeded, null, null);
            });
        var coordinator = new RendererCoordinator([adapter]);

        var result = coordinator.RenderAsync(new RenderRequest("snapshot", SourceFormat.Pdf,
            RendererExecutionContext.BackgroundWorker), TimeSpan.FromMilliseconds(25)).GetAwaiter().GetResult();

        Assert.Equal(RenderStatus.TimedOut, result.Status);
    }

    public static void AdapterTimeoutIsDistinguished()
    {
        var adapter = new FakeAdapter(SourceFormat.Docx, RendererExecutionContext.BackgroundWorker,
            async (_, cancellationToken) =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return new RenderResult(RenderStatus.Succeeded, null, null);
            });
        var coordinator = new RendererCoordinator([adapter]);
        var result = coordinator.RenderAsync(new RenderRequest("snapshot", SourceFormat.Docx,
            RendererExecutionContext.BackgroundWorker), TimeSpan.FromMilliseconds(25)).GetAwaiter().GetResult();
        Assert.Equal(RenderStatus.TimedOut, result.Status);
    }

    private sealed class FakeAdapter(
        SourceFormat format,
        RendererExecutionContext executionContext,
        Func<RenderRequest, CancellationToken, Task<RenderResult>> render) : IRendererAdapter
    {
        public int CallCount { get; private set; }
        public Func<CancellationToken, Task<bool>>? CapabilityCheck { get; init; }
        public bool ThrowOnCanRender { get; init; }
        public async ValueTask<bool> CanRenderAsync(RenderRequest request, CancellationToken cancellationToken)
        {
            if (ThrowOnCanRender) throw new InvalidOperationException("capability check failed");
            if (CapabilityCheck is not null) return await CapabilityCheck(cancellationToken);
            return request.Format == format && request.ExecutionContext == executionContext;
        }
        public async Task<RenderResult> RenderAsync(RenderRequest request, CancellationToken cancellationToken)
        {
            CallCount++;
            return await render(request, cancellationToken);
        }
    }
}
