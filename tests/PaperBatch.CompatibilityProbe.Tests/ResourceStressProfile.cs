using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using PaperBatch.Core.Domain;
using PaperBatch.Core.Intake;

internal static class ResourceStressProfile
{
    private const int MaximumFileCount = 1_000;
    private const int MaximumLogicalPageCount = 10_000;
    private const int MaximumBudgetSeconds = 900;
    private const string SyntheticPdf = "%PDF-1.4\n% PaperBatch synthetic stress descriptor\n%%EOF\n";

    internal static int RunCommand(string[] args)
    {
        var files = 1_000;
        var pages = 10_000;
        var budgetSeconds = 300;
        var maximumFiles = MaximumFileCount;
        var maximumPages = MaximumLogicalPageCount;
        string? reportPath = null;
        for (var index = 1; index < args.Length; index++)
        {
            if (index + 1 >= args.Length) return Usage();
            var value = args[++index];
            switch (args[index - 1])
            {
                case "--files" when int.TryParse(value, out var parsedFiles): files = parsedFiles; break;
                case "--pages" when int.TryParse(value, out var parsedPages): pages = parsedPages; break;
                case "--max-files" when int.TryParse(value, out var parsedMaximumFiles): maximumFiles = parsedMaximumFiles; break;
                case "--max-pages" when int.TryParse(value, out var parsedMaximumPages): maximumPages = parsedMaximumPages; break;
                case "--budget-seconds" when int.TryParse(value, out var parsedBudget): budgetSeconds = parsedBudget; break;
                case "--report": reportPath = Path.GetFullPath(value); break;
                default: return Usage();
            }
        }

        if (maximumFiles is < 1 or > MaximumFileCount || maximumPages is < 1 or > MaximumLogicalPageCount ||
            files is < 1 || files > maximumFiles || pages is < 1 || pages > maximumPages ||
            budgetSeconds is < 1 or > MaximumBudgetSeconds || pages % files != 0)
            return Usage();

        var result = Run(files, pages, TimeSpan.FromSeconds(budgetSeconds)) with
        {
            ReportLocation = reportPath ?? "stdout",
            Limits = new { maximumFiles, maximumLogicalPages = maximumPages, maximumBudgetSeconds = MaximumBudgetSeconds, configuredBudgetMilliseconds = budgetSeconds * 1_000L }
        };
        var json = JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true });
        if (reportPath is null) Console.WriteLine(json);
        else
        {
            Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
            File.WriteAllText(reportPath, json + Environment.NewLine);
            Console.WriteLine(json);
        }
        return result.Status == "passed" ? 0 : 2;

        static int Usage()
        {
            Console.Error.WriteLine("Usage: dotnet run --project tests/PaperBatch.CompatibilityProbe.Tests -- --resource-stress [--files 1..1000] [--pages 1..10000] [--max-files 1..1000] [--max-pages 1..10000] [--budget-seconds 1..900] [--report path]; requested counts must fit the caps and pages must divide evenly across files.");
            return 64;
        }
    }

    internal static ResourceStressReport Run(int fileCount, int logicalPageCount, TimeSpan budget, CancellationToken cancellationToken = default)
    {
        if (fileCount is < 1 or > MaximumFileCount || logicalPageCount is < 1 or > MaximumLogicalPageCount ||
            logicalPageCount % fileCount != 0 || budget <= TimeSpan.Zero || budget > TimeSpan.FromSeconds(MaximumBudgetSeconds))
            throw new ArgumentOutOfRangeException(nameof(fileCount), "Stress profile values must stay within the bounded, evenly divisible workload.");

        var stopwatch = Stopwatch.StartNew();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cancellation.CancelAfter(budget);
        var beforeManagedBytes = GC.GetTotalMemory(forceFullCollection: true);
        var peakManagedBytes = beforeManagedBytes;
        var process = Process.GetCurrentProcess();
        var workingDirectory = Path.Combine(Path.GetTempPath(), "paperbatch-resource-stress-" + Guid.NewGuid().ToString("N"));
        var status = "failed";
        var enumeratedFiles = 0;
        var preparedPages = 0;
        long generatedFixtureBytes = 0;
        string? failure = null;
        try
        {
            Directory.CreateDirectory(workingDirectory);
            var paths = new List<SourceInput>(fileCount);
            var fixture = System.Text.Encoding.ASCII.GetBytes(SyntheticPdf);
            for (var index = 0; index < fileCount; index++)
            {
                cancellation.Token.ThrowIfCancellationRequested();
                var path = Path.Combine(workingDirectory, $"synthetic-{index:D4}.pdf");
                File.WriteAllBytes(path, fixture);
                generatedFixtureBytes += fixture.LongLength;
                paths.Add(new SourceInput(path));
                if ((index + 1) % 100 == 0) peakManagedBytes = Math.Max(peakManagedBytes, GC.GetTotalMemory(forceFullCollection: false));
            }

            var enumeration = SourceEnumerator.Enumerate(paths,
                new SourceEnumerationOptions { MaximumEntries = fileCount, MaximumPathLength = 32_767 }, cancellation.Token);
            enumeratedFiles = enumeration.Items.Count(item => !item.HasErrors);
            if (enumeration.IsCancelled)
                throw new OperationCanceledException(cancellation.Token);
            if (enumeratedFiles != fileCount)
                throw new InvalidOperationException("Synthetic source enumeration did not return the requested file count.");

            var pagesPerFile = logicalPageCount / fileCount;
            var settings = new PrintSettings(1, ColorMode.Monochrome, DuplexMode.Simplex, "iso_a4_210x297mm", Orientation.Portrait, ScalingMode.ShrinkToFit);
            var capabilities = new RendererCapabilities(1, ["application/pdf"]);
            var items = new List<PreparedItem>(fileCount);
            var segments = new List<CompatibleSegment>(fileCount);
            for (var fileIndex = 0; fileIndex < fileCount; fileIndex++)
            {
                cancellation.Token.ThrowIfCancellationRequested();
                var itemId = $"stress_item_{fileIndex:D4}";
                var digest = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(itemId))).ToLowerInvariant();
                var mappings = new PageMapping[pagesPerFile];
                for (var pageIndex = 0; pageIndex < pagesPerFile; pageIndex++)
                    mappings[pageIndex] = new PageMapping(pageIndex + 1, pageIndex + 1);
                items.Add(new PreparedItem(itemId,
                    new SourceSnapshot("snapshot_" + itemId, digest, fixture.LongLength, DateTimeOffset.UnixEpoch),
                    new RendererResult("synthetic", "stress-profile", "artifact_" + itemId, digest, fixture.LongLength, pagesPerFile, capabilities),
                    mappings, settings));
                preparedPages += mappings.Length;
                segments.Add(new CompatibleSegment("segment_" + fileIndex.ToString("D4"), fileIndex, [itemId], "synthetic-pdf", settings));
                if ((fileIndex + 1) % 100 == 0) peakManagedBytes = Math.Max(peakManagedBytes, GC.GetTotalMemory(forceFullCollection: false));
            }

            _ = PreparedRun.Create("stress_run", DateTimeOffset.UnixEpoch,
                new string('a', 64), items, segments);
            peakManagedBytes = Math.Max(peakManagedBytes, GC.GetTotalMemory(forceFullCollection: false));
            cancellation.Token.ThrowIfCancellationRequested();
            status = "passed";
        }
        catch (OperationCanceledException)
        {
            status = "cancelled";
            failure = "The configured time budget expired before the workload completed.";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            failure = exception.Message;
        }
        finally
        {
            stopwatch.Stop();
            try { Directory.Delete(workingDirectory, recursive: true); }
            catch (IOException) { status = status == "passed" ? "failed" : status; failure ??= "Temporary synthetic fixtures could not be removed."; }
            catch (UnauthorizedAccessException) { status = status == "passed" ? "failed" : status; failure ??= "Temporary synthetic fixtures could not be removed."; }
        }

        process.Refresh();
        var afterManagedBytes = GC.GetTotalMemory(forceFullCollection: true);
        return new ResourceStressReport(
            SchemaVersion: 1, Profile: "synthetic-resource-stress", EvidenceClass: "simulation-only",
            Status: status, Runtime: RuntimeInformation.FrameworkDescription, OperatingSystem: RuntimeInformation.OSDescription,
            Architecture: RuntimeInformation.ProcessArchitecture.ToString(), ProcessorCount: Environment.ProcessorCount,
            TotalAvailableMemoryBytes: GC.GetGCMemoryInfo().TotalAvailableMemoryBytes,
            Commit: ResolveCommit(),
            RequestedFiles: fileCount, EnumeratedFiles: enumeratedFiles, RequestedLogicalPages: logicalPageCount,
            PreparedLogicalPages: preparedPages, ElapsedMilliseconds: stopwatch.ElapsedMilliseconds,
            ManagedMemoryDeltaBytes: afterManagedBytes - beforeManagedBytes, SampledPeakManagedMemoryBytes: peakManagedBytes,
            PeakWorkingSetBytes: process.PeakWorkingSet64,
            GeneratedFixtureBytes: generatedFixtureBytes, ArtifactBytes: 0,
            Limits: new { maximumFiles = MaximumFileCount, maximumLogicalPages = MaximumLogicalPageCount, maximumBudgetSeconds = MaximumBudgetSeconds, configuredBudgetMilliseconds = (long)budget.TotalMilliseconds },
            TemporaryFixtureDirectory: workingDirectory, TemporaryFixturesRemoved: !Directory.Exists(workingDirectory),
            ArtifactLocation: "none; no renderer or spool artifact is produced", ReportLocation: "not-written", Failure: failure);
    }

    private static string ResolveCommit()
    {
        var configured = Environment.GetEnvironmentVariable("GITHUB_SHA") ?? Environment.GetEnvironmentVariable("PAPERBATCH_COMMIT");
        if (!string.IsNullOrWhiteSpace(configured)) return configured;
        var directory = new DirectoryInfo(Environment.CurrentDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, ".git")) || Directory.Exists(Path.Combine(directory.FullName, ".git")))
            {
                try
                {
                    using var git = Process.Start(new ProcessStartInfo
                    {
                        FileName = "git",
                        WorkingDirectory = directory.FullName,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        ArgumentList = { "rev-parse", "HEAD" }
                    });
                    if (git is null) return "unreported";
                    var output = git.StandardOutput.ReadToEnd().Trim();
                    git.WaitForExit();
                    return git.ExitCode == 0 && output.Length == 40 ? output : "unreported";
                }
                catch (System.ComponentModel.Win32Exception) { return "unreported"; }
                catch (IOException) { return "unreported"; }
            }
            directory = directory.Parent;
        }
        return "unreported";
    }
}

internal sealed record ResourceStressReport(
    int SchemaVersion, string Profile, string EvidenceClass, string Status, string Runtime, string OperatingSystem,
    string Architecture, int ProcessorCount, long TotalAvailableMemoryBytes, string Commit, int RequestedFiles,
    int EnumeratedFiles, int RequestedLogicalPages, int PreparedLogicalPages, long ElapsedMilliseconds,
    long ManagedMemoryDeltaBytes, long SampledPeakManagedMemoryBytes, long PeakWorkingSetBytes, long GeneratedFixtureBytes, long ArtifactBytes,
    object Limits, string TemporaryFixtureDirectory, bool TemporaryFixturesRemoved, string ArtifactLocation, string ReportLocation, string? Failure);
