using System.Diagnostics;
using PaperBatch.CompatibilityProbe;

internal static class OfficeProbeProcessTests
{
    public static void BoundsConcurrentVersionOutput()
    {
        var startInfo = CreateChildProcess("flood");
        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not start the synthetic OfficeProbe output process.");

        var result = OfficeProbe.RunVersionProcessWithBoundedOutput(process, TimeSpan.FromSeconds(10));

        Assert.True(result.Exited);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal(OfficeProbe.MaximumVersionOutputCharacters, result.StandardOutput.Length);
        Assert.Equal(OfficeProbe.MaximumVersionOutputCharacters, result.StandardError.Length);
        Assert.True(result.StandardOutput.StartsWith("LibreOffice 26.2.2.2\n", StringComparison.Ordinal));
        Assert.True(result.StandardError.StartsWith("synthetic stderr\n", StringComparison.Ordinal));
    }

    public static void StopsTimedOutVersionProcess()
    {
        var root = Path.Combine(Path.GetTempPath(), $"paperbatch-officeprobe-process-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var parentMarker = Path.Combine(root, "parent.pid");
        var childMarker = Path.Combine(root, "child.pid");
        try
        {
            var startInfo = CreateChildProcess("spawn-hang", parentMarker, childMarker);
            using var process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Could not start the synthetic hanging OfficeProbe process.");
            var capture = Task.Run(() => OfficeProbe.RunVersionProcessWithBoundedOutput(
                process, TimeSpan.FromSeconds(2)));
            WaitForFile(parentMarker);
            WaitForFile(childMarker);

            var result = capture.GetAwaiter().GetResult();

            Assert.False(result.Exited);
            Assert.True(ProcessHasExited(ReadProcessId(parentMarker)), "the timed-out root process should exit");
            Assert.True(ProcessHasExited(ReadProcessId(childMarker)), "the timed-out root's descendant should exit");
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }

        BoundsDrainWaitWhenDescendantHoldsOutputAfterParentExit();
        ReportsUncontainedDescendantTimeoutWithoutClaimingItStopped();
    }

    private static void BoundsDrainWaitWhenDescendantHoldsOutputAfterParentExit()
    {
        if (!OperatingSystem.IsWindows()) return;

        var root = Path.Combine(Path.GetTempPath(), $"paperbatch-officeprobe-descendant-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var childMarker = Path.Combine(root, "child.pid");
        try
        {
            var startInfo = CreateChildProcess("spawn-child-and-exit", childMarker);
            using var process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Could not start the synthetic OfficeProbe process.");

            var result = OfficeProbe.RunVersionProcessWithBoundedOutput(process, TimeSpan.FromSeconds(1));
            WaitForFile(childMarker);

            Assert.False(result.Exited);
            Assert.True(process.HasExited);
            Assert.True(ProcessHasExited(ReadProcessId(childMarker)), "the contained descendant should exit after its parent timeout");
        }
        finally
        {
            if (File.Exists(childMarker)) KillProcess(ReadProcessId(childMarker));
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static void ReportsUncontainedDescendantTimeoutWithoutClaimingItStopped()
    {
        if (!OperatingSystem.IsLinux()) return;

        var root = Path.Combine(Path.GetTempPath(), $"paperbatch-officeprobe-uncontained-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var childMarker = Path.Combine(root, "child.pid");
        try
        {
            var startInfo = CreateChildProcess("spawn-child-and-exit", childMarker);
            using var process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Could not start the synthetic OfficeProbe process.");

            var result = OfficeProbe.RunVersionProcessWithBoundedOutput(process, TimeSpan.FromSeconds(1));
            WaitForFile(childMarker);

            Assert.False(result.Exited);
            Assert.True(process.HasExited);
            Assert.False(ProcessHasExited(ReadProcessId(childMarker)));
        }
        finally
        {
            if (File.Exists(childMarker)) KillProcess(ReadProcessId(childMarker));
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    public static int RunChild(string mode, string[] arguments)
    {
        if (mode == "flood")
        {
            Console.Out.Write("LibreOffice 26.2.2.2\n");
            Console.Error.Write("synthetic stderr\n");
            var stdoutChunk = new string('O', 1024);
            var stderrChunk = new string('E', 1024);
            for (var index = 0; index < 1024; index++)
            {
                Console.Out.Write(stdoutChunk);
                Console.Error.Write(stderrChunk);
            }
            return 0;
        }

        if (mode == "hang")
        {
            File.WriteAllText(arguments[0], Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
            Thread.Sleep(Timeout.Infinite);
        }

        if (mode == "spawn-hang")
        {
            var childStart = CreateChildProcess("hang", arguments[1]);
            using var child = Process.Start(childStart)
                ?? throw new InvalidOperationException("Could not start the synthetic hanging descendant.");
            File.WriteAllText(arguments[0], Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
            Thread.Sleep(300);
            Thread.Sleep(Timeout.Infinite);
        }

        if (mode == "spawn-child-and-exit")
        {
            Thread.Sleep(100);
            var childStart = CreateChildProcess("hang", arguments[0]);
            childStart.RedirectStandardOutput = false;
            childStart.RedirectStandardError = false;
            using var child = Process.Start(childStart)
                ?? throw new InvalidOperationException("Could not start the synthetic output-holding descendant.");
            return 0;
        }

        return 2;
    }

    private static ProcessStartInfo CreateChildProcess(string mode, params string[] arguments)
    {
        var executable = Environment.ProcessPath ?? throw new InvalidOperationException("Test host path is unavailable.");
        var startInfo = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        var entryAssembly = System.Reflection.Assembly.GetEntryAssembly()?.Location;
        if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            startInfo.ArgumentList.Add(entryAssembly ?? throw new InvalidOperationException("Test assembly path is unavailable."));
        startInfo.ArgumentList.Add("--office-probe-output-child");
        startInfo.ArgumentList.Add(mode);
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);
        return startInfo;
    }

    private static void WaitForFile(string path)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (!File.Exists(path) && DateTime.UtcNow < deadline) Thread.Sleep(10);
        Assert.True(File.Exists(path), $"The synthetic child did not create {Path.GetFileName(path)}.");
    }

    private static int ReadProcessId(string path) => int.Parse(File.ReadAllText(path), System.Globalization.CultureInfo.InvariantCulture);

    private static bool ProcessHasExited(int processId)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        do
        {
            try
            {
                using var process = Process.GetProcessById(processId);
                if (process.HasExited) return true;
            }
            catch (ArgumentException)
            {
                return true;
            }
            Thread.Sleep(10);
        } while (DateTime.UtcNow < deadline);
        return false;
    }

    private static void KillProcess(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch (ArgumentException)
        {
        }
        catch (InvalidOperationException)
        {
        }
    }
}
