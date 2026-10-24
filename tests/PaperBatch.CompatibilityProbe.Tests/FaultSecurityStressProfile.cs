using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

internal static class FaultSecurityStressProfile
{
    internal const int MaximumScenarioCount = 10;

    private static readonly (string Id, string Client, string Endpoint, string Assertion, Action Run)[] Scenarios =
    [
        ("lifecycle.ambiguous_acceptance_restart", "PaperBatch WindowsPrintCoordinator test harness", "local SQLite store and injected fake spooler", "unknown OS acceptance survives restart and is never automatically resubmitted", PrintingTests.AmbiguousSubmissionIsNotRetriedAfterRestart),
        ("lifecycle.cancellation_race", "PaperBatch WindowsPrintCoordinator test harness", "local SQLite store and injected fake spooler", "the winning OS status is retained when cancellation races with completion", PrintingTests.CancellationRacePreservesOsStatus),
        ("lifecycle.cancel_receipt_restart", "PaperBatch WindowsPrintCoordinator test harness", "local SQLite store and injected fake spooler", "a cancellation retry after restart uses the exact owned OS receipt", PrintingTests.CancellationRetryAfterRestartUsesReceipt),
        ("idempotency.concurrent_receipts", "PaperBatch WindowsPrintCoordinator test harness", "local SQLite store and injected fake spooler", "concurrent jobs keep separate stable submission receipts", PrintingTests.ConcurrentJobsKeepDistinctReceipts),
        ("idempotency.run_creation", "PaperBatch SQLite persistence test harness", "temporary local SQLite database", "matching create requests replay the existing run without duplicating jobs", CoreTests.RunCreationIdempotencyReplaysMatchingRequests),
        ("resources.snapshot_cache_quota", "PaperBatch source snapshot test harness", "temporary local filesystem", "cache quota and retention preserve referenced snapshots and reject over-quota writes", SourceIntakeTests.SnapshotQuotaAndRetentionAreSafe),
        ("resources.bounded_profile", "PaperBatch synthetic resource stress profile", "temporary local filesystem; no network or print endpoint", "10 synthetic files and 100 logical pages complete within the configured test budget and fixed profile caps", RunBoundedResourceProfile),
        ("security.malicious_input_bounds", "PaperBatch source preflight test harness", "synthetic in-memory ZIP/OOXML inputs", "malformed, encrypted, and unsafe container inputs fail closed within parser limits", SourceIntakeTests.PreflightRejectsMismatchMalformedEncryptionAndUnsafePackages),
        ("privacy.diagnostic_redaction", "PaperBatch diagnostic test harness", "in-memory synthetic diagnostics", "serialized diagnostics omit private paths, tokens, and document body text", WatchFolderTests.DiagnosticsRedactSensitiveFields),
        ("concurrency.watch_scan_serialization", "PaperBatch watch-folder test harness", "temporary local filesystem with synthetic concurrent scans", "concurrent scans serialize observation updates and preserve deterministic readiness", WatchFolderTests.ConcurrentScansSerializeObservationUpdates)
    ];

    internal static int RunCommand(string[] args)
    {
        string? reportPath = null;
        for (var index = 1; index < args.Length; index++)
        {
            if (args[index] != "--report" || index + 1 >= args.Length || reportPath is not null)
                return Usage();
            reportPath = Path.GetFullPath(args[++index]);
        }

        var report = Run() with { ReportLocation = reportPath is null ? "stdout" : "file" };
        var json = JsonSerializer.Serialize(report, new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        });
        if (reportPath is null)
        {
            Console.WriteLine(json);
        }
        else
        {
            Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
            File.WriteAllText(reportPath, json + Environment.NewLine);
            Console.WriteLine(json);
        }
        return report.Status == "passed" ? 0 : 2;

        static int Usage()
        {
            Console.Error.WriteLine("Usage: dotnet run --project tests/PaperBatch.CompatibilityProbe.Tests -- --fault-security-stress [--report path]");
            return 64;
        }
    }

    internal static FaultSecurityStressReport Run()
    {
        var results = new FaultSecurityScenarioResult[Scenarios.Length];
        for (var index = 0; index < Scenarios.Length; index++)
        {
            var scenario = Scenarios[index];
            var status = "passed";
            string? failureCode = null;
            try
            {
                scenario.Run();
            }
            catch (Exception exception)
            {
                status = "failed";
                failureCode = "assertion_or_execution_failure:" + exception.GetType().Name;
            }
            results[index] = new FaultSecurityScenarioResult(
                scenario.Id, status, scenario.Client, scenario.Endpoint, scenario.Assertion, failureCode);
        }

        var runner = Environment.GetEnvironmentVariable("RUNNER_NAME") ??
            (Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true" ? "github-hosted" : "local");
        var sourceIdentity = ResolveSourceIdentity();
        return new FaultSecurityStressReport(
            SchemaVersion: 1,
            Profile: "u9-fault-security-stress",
            EvidenceClass: "simulation-only",
            Status: results.All(result => result.Status == "passed") ? "passed" : "failed",
            Commit: sourceIdentity.Commit,
            WorkingTreeClean: sourceIdentity.WorkingTreeClean,
            SourceTreeDiffSha256: sourceIdentity.SourceTreeDiffSha256,
            OperatingSystem: RuntimeInformation.OSDescription,
            Architecture: RuntimeInformation.ProcessArchitecture.ToString(),
            Runner: runner,
            RunnerImage: Environment.GetEnvironmentVariable("ImageOS") is { Length: > 0 } imageOs
                ? imageOs + (Environment.GetEnvironmentVariable("ImageVersion") is { Length: > 0 } imageVersion ? "/" + imageVersion : "")
                : "not-reported",
            ToolVersions: new SortedDictionary<string, string>(StringComparer.Ordinal)
            {
                ["dotnetSdk"] = ResolveDotnetSdkVersion(),
                ["dotnetRuntime"] = RuntimeInformation.FrameworkDescription,
                ["testAssembly"] = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "not-reported"
            },
            Limits: new { maximumScenarioCount = MaximumScenarioCount, scenarioCount = results.Length, resourceProfileFiles = 10, resourceProfileLogicalPages = 100, resourceProfileBudgetSeconds = 20 },
            Scenarios: results,
            ReportLocation: "not-written",
            EvidenceLimit: "Synthetic fixtures and injected local adapters only. No real queue, network endpoint, physical print, power-loss, fleet-scale, OEM-driver, Office/WPS, or arbitrary-host claim.");
    }

    private static void RunBoundedResourceProfile()
    {
        var report = ResourceStressProfile.Run(10, 100, TimeSpan.FromSeconds(20));
        if (report.Status != "passed" || report.EnumeratedFiles != 10 || report.PreparedLogicalPages != 100 ||
            report.ArtifactBytes != 0 || !report.TemporaryFixturesRemoved)
            throw new InvalidOperationException("Bounded synthetic resource profile failed its fixed assertions.");
    }

    private static (string Commit, bool WorkingTreeClean, string SourceTreeDiffSha256) ResolveSourceIdentity()
    {
        var configured = Environment.GetEnvironmentVariable("GITHUB_SHA") ?? Environment.GetEnvironmentVariable("PAPERBATCH_COMMIT");
        try
        {
            var commit = !string.IsNullOrWhiteSpace(configured) ? configured : RunGit("rev-parse", "HEAD");
            var status = RunGit("status", "--porcelain=v1", "--untracked-files=all");
            var diff = RunGit("diff", "--binary", "HEAD", "--");
            var untracked = RunGit("ls-files", "--others", "--exclude-standard", "-z");
            if (commit is null || status is null || diff is null || untracked is null) return ("unreported", false, "unreported");

            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            hash.AppendData(Encoding.UTF8.GetBytes(diff));
            foreach (var relativePath in untracked.Split('\0', StringSplitOptions.RemoveEmptyEntries).Order(StringComparer.Ordinal))
            {
                hash.AppendData(Encoding.UTF8.GetBytes(relativePath));
                hash.AppendData([0]);
                var fullPath = Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, relativePath));
                if ((File.GetAttributes(fullPath) & FileAttributes.ReparsePoint) != 0)
                {
                    var linkTarget = new FileInfo(fullPath).LinkTarget ?? "unreported-link-target";
                    hash.AppendData(Encoding.UTF8.GetBytes(linkTarget));
                }
                else
                {
                    hash.AppendData(File.ReadAllBytes(fullPath));
                }
                hash.AppendData([0]);
            }

            return (commit, status.Length == 0, Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant());
        }
        catch (System.ComponentModel.Win32Exception) { return ("unreported", false, "unreported"); }
        catch (IOException) { return ("unreported", false, "unreported"); }
        catch (UnauthorizedAccessException) { return ("unreported", false, "unreported"); }
        catch (ArgumentException) { return ("unreported", false, "unreported"); }
    }

    private static string? RunGit(params string[] arguments)
    {
        var start = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = Environment.CurrentDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var git = Process.Start(start);
        if (git is null) return null;
        var output = git.StandardOutput.ReadToEnd();
        git.WaitForExit();
        return git.ExitCode == 0 ? output.TrimEnd('\r', '\n') : null;
    }

    private static string ResolveDotnetSdkVersion()
    {
        try
        {
            using var dotnet = Process.Start(new ProcessStartInfo
            {
                FileName = "dotnet",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                ArgumentList = { "--version" }
            });
            if (dotnet is null) return "not-reported";
            var output = dotnet.StandardOutput.ReadToEnd().Trim();
            dotnet.WaitForExit();
            return dotnet.ExitCode == 0 && output.Length <= 64 ? output : "not-reported";
        }
        catch (System.ComponentModel.Win32Exception) { return "not-reported"; }
        catch (IOException) { return "not-reported"; }
    }
}

internal sealed record FaultSecurityScenarioResult(
    string Id, string Status, string Client, string Endpoint, string Assertion, string? FailureCode);

internal sealed record FaultSecurityStressReport(
    int SchemaVersion, string Profile, string EvidenceClass, string Status, string Commit,
    bool WorkingTreeClean, string SourceTreeDiffSha256,
    string OperatingSystem, string Architecture, string Runner, string RunnerImage,
    SortedDictionary<string, string> ToolVersions, object Limits, FaultSecurityScenarioResult[] Scenarios,
    string ReportLocation, string EvidenceLimit);
