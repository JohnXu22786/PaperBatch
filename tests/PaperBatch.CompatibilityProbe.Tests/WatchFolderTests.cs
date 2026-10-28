using PaperBatch.Core.Watch;
using PaperBatch.Core.Ipc;

internal static class WatchFolderTests
{
    public static void ScanIgnoresTemporaryFilesAndLinks()
    {
        using var tree = new TemporaryTree();
        var accepted = Path.Combine(tree.Root, "inbox", "report.pdf");
        Directory.CreateDirectory(Path.GetDirectoryName(accepted)!);
        File.WriteAllText(accepted, "pdf");
        File.WriteAllText(Path.Combine(tree.Root, "inbox", "~$report.docx"), "temporary");
        File.WriteAllText(Path.Combine(tree.Root, "inbox", "render.tmp"), "temporary");
        Directory.CreateDirectory(Path.Combine(tree.Root, "output"));
        File.WriteAllText(Path.Combine(tree.Root, "output", "copy.pdf"), "output");
        var linkCreated = false;
        try
        {
            Directory.CreateSymbolicLink(Path.Combine(tree.Root, "inbox", "linked"), Path.Combine(tree.Root, "output"));
            linkCreated = true;
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException or PlatformNotSupportedException) { }

        var scanner = new WatchFolderScanner(new WatchFolderOptions(tree.Root, "rule-a", TimeSpan.Zero, "output"));
        _ = scanner.Scan();
        var files = scanner.Scan().Select(candidate => candidate.RelativePath).ToArray();
        Assert.Equal(1, files.Length);
        Assert.Equal(Path.Combine("inbox", "report.pdf"), files[0]);
        Assert.Equal(true, linkCreated || files.Length == 1);
    }

    public static void ReadinessRequiresTwoMatchingObservations()
    {
        using var tree = new TemporaryTree();
        var path = Path.Combine(tree.Root, "one.pdf");
        File.WriteAllText(path, "pdf");
        var scanner = new WatchFolderScanner(new WatchFolderOptions(tree.Root, "rule-a", TimeSpan.FromSeconds(1)));
        var time = DateTimeOffset.Parse("2026-10-20T12:00:00Z");
        Assert.Equal(0, scanner.Scan(time).Count);
        Assert.Equal(0, scanner.Scan(time.AddMilliseconds(500)).Count);
        Assert.Equal(1, scanner.Scan(time.AddSeconds(1)).Count);
    }

    public static void IdempotencyKeyIsStableAcrossRestartsAndChangesOnGeneration()
    {
        using var tree = new TemporaryTree();
        var path = Path.Combine(tree.Root, "one.pdf");
        File.WriteAllText(path, "pdf");
        var options = new WatchFolderOptions(tree.Root, "rule-a", TimeSpan.Zero);
        var firstScanner = new WatchFolderScanner(options);
        _ = firstScanner.Scan(DateTimeOffset.UtcNow);
        var first = firstScanner.Scan(DateTimeOffset.UtcNow).Single();
        var restartedScanner = new WatchFolderScanner(options);
        _ = restartedScanner.Scan(DateTimeOffset.UtcNow);
        var restart = restartedScanner.Scan(DateTimeOffset.UtcNow).Single();
        Assert.Equal(first.IdempotencyKey, restart.IdempotencyKey);
        File.WriteAllText(path, "changed pdf");
        var changedScanner = new WatchFolderScanner(options);
        _ = changedScanner.Scan(DateTimeOffset.UtcNow);
        var changed = changedScanner.Scan(DateTimeOffset.UtcNow).Single();
        Assert.True(first.IdempotencyKey != changed.IdempotencyKey);
    }





    public static void ParentDirectoryLinkSwapFailsClosed()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var tree = new TemporaryTree();
        var root = Path.Combine(tree.Root, "watch");
        var inbox = Path.Combine(root, "inbox");
        var outside = Path.Combine(tree.Root, "outside");
        Directory.CreateDirectory(inbox);
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(inbox, "report.pdf"), "inside");
        File.WriteAllText(Path.Combine(outside, "report.pdf"), "outside");
        var fileAccess = new ParentSwapFileAccess(inbox, outside, Path.Combine(root, "inbox-held"));
        var scanner = new WatchFolderScanner(new WatchFolderOptions(root, "rule-a", TimeSpan.Zero), fileAccess: fileAccess);
        var time = DateTimeOffset.UtcNow;
        _ = scanner.Scan(time);
        Assert.Equal(0, scanner.Scan(time).Count);
        Assert.True(fileAccess.Attempted);
        Assert.True(File.Exists(Path.Combine(inbox, "report.pdf")));
        Assert.True(File.Exists(Path.Combine(outside, "report.pdf")));
    }

    public static void ReplacedWatchRootFailsClosed()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var tree = new TemporaryTree();
        var root = Path.Combine(tree.Root, "watch");
        var replacement = Path.Combine(tree.Root, "replacement");
        var backup = Path.Combine(tree.Root, "watch-held");
        Directory.CreateDirectory(Path.Combine(root, "inbox"));
        Directory.CreateDirectory(Path.Combine(replacement, "inbox"));
        File.WriteAllText(Path.Combine(root, "inbox", "report.pdf"), "original");
        File.WriteAllText(Path.Combine(replacement, "inbox", "report.pdf"), "replaced");
        var fileAccess = new RootSwapFileAccess(root, replacement, backup);
        var scanner = new WatchFolderScanner(new WatchFolderOptions(root, "rule-a", TimeSpan.Zero), fileAccess: fileAccess);
        var time = DateTimeOffset.UtcNow;
        _ = scanner.Scan(time);
        Assert.Equal(0, scanner.Scan(time).Count);
        Assert.True(fileAccess.Attempted);
        Assert.True(File.Exists(Path.Combine(root, "inbox", "report.pdf")));
        Assert.True(File.Exists(Path.Combine(replacement, "inbox", "report.pdf")));
    }

    public static void LinuxWatchRejectsFifoCandidates()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var tree = new TemporaryTree();
        var fifo = Path.Combine(tree.Root, "empty.pdf");
        if (MkFifo(fifo, 0x180) != 0) throw new IOException("Could not create FIFO fixture.");
        var scanner = new WatchFolderScanner(new WatchFolderOptions(tree.Root, "rule-a", TimeSpan.Zero));
        var now = DateTimeOffset.UtcNow;
        Assert.Equal(0, scanner.Scan(now).Count);
        Assert.Equal(0, scanner.Scan(now).Count);
    }

    [System.Runtime.InteropServices.DllImport("libc", EntryPoint = "mkfifo", SetLastError = true)]
    private static extern int MkFifo(string path, uint mode);

    private sealed class ParentSwapFileAccess(string inbox, string outside, string backup) : IWatchFolderFileAccess
    {
        private readonly PhysicalWatchFolderFileAccess _inner = new();
        public bool Attempted { get; private set; }
        public string GetRootIdentity(string rootPath) => _inner.GetRootIdentity(rootPath);
        public Stream OpenRead(string rootPath, string expectedRootIdentity, string relativePath)
        {
            if (!Attempted && relativePath == Path.Combine("inbox", "report.pdf"))
            {
                Attempted = true;
                Directory.Move(inbox, backup);
                Directory.CreateSymbolicLink(inbox, outside);
                try { return _inner.OpenRead(rootPath, expectedRootIdentity, relativePath); }
                finally
                {
                    Directory.Delete(inbox);
                    Directory.Move(backup, inbox);
                }
            }
            return _inner.OpenRead(rootPath, expectedRootIdentity, relativePath);
        }
        public PaperBatch.Core.Intake.SourceFileIdentity GetOpenedIdentity(Stream stream) => _inner.GetOpenedIdentity(stream);
    }

    private sealed class RootSwapFileAccess(string root, string replacement, string backup) : IWatchFolderFileAccess
    {
        private readonly PhysicalWatchFolderFileAccess _inner = new();
        public bool Attempted { get; private set; }
        public string GetRootIdentity(string rootPath) => _inner.GetRootIdentity(rootPath);
        public Stream OpenRead(string rootPath, string expectedRootIdentity, string relativePath)
        {
            if (!Attempted && relativePath == Path.Combine("inbox", "report.pdf"))
            {
                Attempted = true;
                Directory.Move(root, backup);
                Directory.Move(replacement, root);
                try { return _inner.OpenRead(rootPath, expectedRootIdentity, relativePath); }
                finally
                {
                    Directory.Move(root, replacement);
                    Directory.Move(backup, root);
                }
            }
            return _inner.OpenRead(rootPath, expectedRootIdentity, relativePath);
        }
        public PaperBatch.Core.Intake.SourceFileIdentity GetOpenedIdentity(Stream stream) => _inner.GetOpenedIdentity(stream);
    }

    public static void ConcurrentScansSerializeObservationUpdates()
    {
        using var tree = new TemporaryTree();
        File.WriteAllText(Path.Combine(tree.Root, "one.pdf"), "pdf");
        var scanner = new WatchFolderScanner(new WatchFolderOptions(tree.Root, "rule-a", TimeSpan.Zero));
        var time = DateTimeOffset.UtcNow;
        var scans = Enumerable.Range(0, 16).Select(_ => Task.Run(() => scanner.Scan(time))).ToArray();
        Task.WaitAll(scans);
        Assert.Equal(16, scans.Length);
        Assert.True(scans.All(scan => scan.Result.Count <= 1));
    }

    public static void BareRecipeCommandReturnsUsageError()
    {
        var cli = new PaperBatchCli();
        Assert.Throws<ArgumentException>(() => cli.ExecuteAsync(["recipe"]).AsTask().GetAwaiter().GetResult());
    }

    public static void ProcessorRequiresReviewForOffice()
    {
        using var tree = new TemporaryTree();
        File.WriteAllText(Path.Combine(tree.Root, "office.docx"), "office");
        File.WriteAllText(Path.Combine(tree.Root, "image.png"), "image");
        var scanner = new WatchFolderScanner(new WatchFolderOptions(tree.Root, "trusted-rule", TimeSpan.Zero));
        var coordinator = new RecordingCoordinator();
        var processor = new WatchFolderProcessor(scanner, coordinator, new WatchFolderPolicy(AutoSubmitTrustedRule: true, RuleIsTrusted: true));
        _ = processor.ReconcileAsync().AsTask().GetAwaiter().GetResult();
        var processed = processor.ReconcileAsync().AsTask().GetAwaiter().GetResult();
        Assert.Equal(2, processed.Count);
        Assert.Equal(false, coordinator.SubmitAutomaticallyByPath[Path.Combine(tree.Root, "office.docx")]);
        Assert.Equal(true, coordinator.SubmitAutomaticallyByPath[Path.Combine(tree.Root, "image.png")]);
        Assert.Equal(true, processed.Single(item => item.Candidate.Kind == WatchFileKind.Office).Candidate.RequiresUserReview);
    }

    public static void PreparationFailureDoesNotHideEarlierResultsOrSkipLaterCandidates()
    {
        using var tree = new TemporaryTree();
        File.WriteAllBytes(Path.Combine(tree.Root, "01-first.pdf"), RendererTests.OnePagePdf());
        File.WriteAllBytes(Path.Combine(tree.Root, "02-malformed.pdf"), [0x25, 0x50, 0x44, 0x58, 0x2d]);
        File.WriteAllBytes(Path.Combine(tree.Root, "03-last.pdf"), RendererTests.OnePagePdf());

        var scanner = new WatchFolderScanner(new WatchFolderOptions(tree.Root, "rule-a", TimeSpan.Zero));
        var coordinator = new FailingPreparationCoordinator();
        var processor = new WatchFolderProcessor(scanner, coordinator);
        var now = DateTimeOffset.Parse("2026-10-28T12:00:00Z");

        _ = processor.ReconcileAsync(now).AsTask().GetAwaiter().GetResult();
        var processed = processor.ReconcileAsync(now).AsTask().GetAwaiter().GetResult();

        Assert.Equal(3, processed.Count);
        var failed = processed.Single(item => item.Candidate.RelativePath == "02-malformed.pdf");
        Assert.Equal("Failed", failed.State.ToString());
        Assert.True(new[] { "01-first.pdf", "03-last.pdf" }.SequenceEqual(
            processed.Where(item => item.State.ToString() != "Failed").Select(item => item.Candidate.RelativePath)));
        Assert.True(new[] { "run-01-first", "run-03-last" }.SequenceEqual(coordinator.PreparedRunIds));
    }

    private sealed class FailingPreparationCoordinator : IWatchFolderCoordinator
    {
        public List<string> PreparedRunIds { get; } = [];

        public ValueTask ProcessAsync(WatchFolderCandidate candidate, bool submitAutomatically, CancellationToken cancellationToken = default)
        {
            var header = new byte[5];
            using (var source = File.OpenRead(candidate.FullPath))
                source.ReadExactly(header);
            if (!header.SequenceEqual(new byte[] { 0x25, 0x50, 0x44, 0x46, 0x2d }))
                throw new WatchPreparationFailedException("Preparation failed for malformed document.");

            PreparedRunIds.Add("run-" + Path.GetFileNameWithoutExtension(candidate.RelativePath));
            return ValueTask.CompletedTask;
        }
    }

    private sealed class RecordingCoordinator : IWatchFolderCoordinator
    {
        public Dictionary<string, bool> SubmitAutomaticallyByPath { get; } = new();
        public ValueTask ProcessAsync(WatchFolderCandidate candidate, bool submitAutomatically, CancellationToken cancellationToken = default)
        {
            SubmitAutomaticallyByPath.Add(candidate.FullPath, submitAutomatically);
            return ValueTask.CompletedTask;
        }
    }

    public static void DebouncerCoalescesHints()
    {
        var debouncer = new WatchFolderDebouncer(TimeSpan.FromSeconds(2));
        var start = DateTimeOffset.Parse("2026-10-20T12:00:00Z");
        debouncer.NotifyChange(start);
        debouncer.NotifyChange(start.AddSeconds(1));
        Assert.Equal(false, debouncer.TryTakeDueScan(start.AddSeconds(2)));
        Assert.Equal(true, debouncer.TryTakeDueScan(start.AddSeconds(3)));
        Assert.Equal(false, debouncer.TryTakeDueScan(start.AddSeconds(4)));
    }

    public static void DiagnosticsRedactSensitiveFields()
    {
        var report = DiagnosticOutput.Create([new DiagnosticEntry("source.failed", "Failed at /private/report.pdf token-value body text", "/private/report.pdf", "token-value", "body text")]);
        var json = System.Text.Json.JsonSerializer.Serialize(report);
        Assert.Equal(false, report.IncludesSensitiveData);
        Assert.True(!json.Contains("/private", StringComparison.Ordinal));
        Assert.True(!json.Contains("token-value", StringComparison.Ordinal));
        Assert.True(!json.Contains("body text", StringComparison.Ordinal));
        var cli = new PaperBatchCli(diagnosticSource: () => [new DiagnosticEntry("source.failed", "Sensitive /private/report.pdf", "/private/report.pdf")]);
        var cliReport = cli.ExecuteAsync(["diagnostics"]).AsTask().GetAwaiter().GetResult();
        Assert.True(!cliReport.Contains("/private/report.pdf", StringComparison.Ordinal));
        Assert.Throws<NotSupportedException>(() => new PaperBatchCli().ExecuteAsync(["diagnostics"]).AsTask().GetAwaiter().GetResult());
    }


    public static void CliValidatesRecipes()
    {
        var validPath = Path.Combine(Directory.GetCurrentDirectory(), "tests", "PaperBatch.CompatibilityProbe.Tests", "Fixtures", "recipe-v1.json");
        using var store = new TemporaryTree();
        var cli = new PaperBatchCli(recipeStoreDirectory: Path.Combine(store.Root, "recipes"));
        var report = cli.ExecuteAsync(["recipe", "validate", validPath]).AsTask().GetAwaiter().GetResult();
        Assert.Contains("\"valid\":true", report);
        var source = File.ReadAllText(validPath);
        var imported = cli.ExecuteAsync(["recipe", "import", validPath]).AsTask().GetAwaiter().GetResult();
        Assert.Contains("imported", imported);
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(Path.Combine(store.Root, "recipes")));
            var importedFile = Directory.GetFiles(Path.Combine(store.Root, "recipes")).Single();
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(importedFile));
        }
        var recipeId = "office-packet";
        using var output = new TemporaryFile();
        File.WriteAllText(output.Path, "keep existing destination");
        Assert.Throws<IOException>(() => cli.ExecuteAsync(["recipe", "export", recipeId, output.Path]).AsTask().GetAwaiter().GetResult());
        Assert.Equal("keep existing destination", File.ReadAllText(output.Path));
        using var exported = new TemporaryFile();
        var exportResult = cli.ExecuteAsync(["recipe", "export", recipeId, exported.Path]).AsTask().GetAwaiter().GetResult();
        Assert.Contains("exported", exportResult);
        Assert.Contains("office-packet", File.ReadAllText(exported.Path));
        Assert.Equal(source, File.ReadAllText(validPath));
        using var temporary = new TemporaryFile();
        File.WriteAllText(temporary.Path, "{\"schemaVersion\":3}");
        Assert.Throws<PaperBatch.Core.Recipes.UnsupportedSchemaVersionException>(() => cli.ExecuteAsync(["recipe", "import", temporary.Path]).AsTask().GetAwaiter().GetResult());
        File.WriteAllBytes(temporary.Path, new byte[PaperBatch.Core.Recipes.JobRecipeJson.MaximumRecipeBytes + 1]);
        Assert.Throws<InvalidDataException>(() => cli.ExecuteAsync(["recipe", "validate", temporary.Path]).AsTask().GetAwaiter().GetResult());
    }

    public static void CliReportsPermissionDeniedForRecipeImportAndExport()
    {
        var fixture = Path.Combine(Directory.GetCurrentDirectory(), "tests", "PaperBatch.CompatibilityProbe.Tests", "Fixtures", "recipe-v1.json");
        using var tree = new TemporaryTree();
        var store = Path.Combine(tree.Root, "recipes");
        var ordinary = new PaperBatchCli(recipeStoreDirectory: store);
        _ = ordinary.ExecuteAsync(["recipe", "import", fixture]).AsTask().GetAwaiter().GetResult();

        using var importOutput = new StringWriter();
        using var importError = new StringWriter();
        var importDenied = new PaperBatchCli(recipeStoreDirectory: store, openRecipeRead: _ => throw new UnauthorizedAccessException("/private/input.json"));
        var importExit = PaperBatchCli.ExecuteAsync(["recipe", "import", fixture], importOutput, importError, cli: importDenied).GetAwaiter().GetResult();
        Assert.Equal(2, importExit);
        Assert.Contains("permission_denied", importError.ToString());
        Assert.True(!importError.ToString().Contains("/private", StringComparison.Ordinal));

        using var exportOutput = new StringWriter();
        using var exportError = new StringWriter();
        var exportDenied = new PaperBatchCli(recipeStoreDirectory: store, createRecipeFile: _ => throw new UnauthorizedAccessException("/private/output.json"));
        var exportExit = PaperBatchCli.ExecuteAsync(["recipe", "export", "office-packet", Path.Combine(tree.Root, "out.json")], exportOutput, exportError, cli: exportDenied).GetAwaiter().GetResult();
        Assert.Equal(2, exportExit);
        Assert.Contains("permission_denied", exportError.ToString());
        Assert.True(!exportError.ToString().Contains("/private", StringComparison.Ordinal));
    }

    private sealed class TemporaryFile : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "paperbatch-recipe-" + Guid.NewGuid().ToString("N") + ".json");
        public void Dispose() { if (File.Exists(Path)) File.Delete(Path); }
    }

    public static void CliCoordinatorUsesStableIdempotencyKeys()
    {
        var transport = new RecordingTransport();
        var coordinator = new NamedPipeCliCoordinator(transport);
        var first = coordinator.ExecuteAsync("submit", ["job-42"]).AsTask().GetAwaiter().GetResult();
        var retry = coordinator.ExecuteAsync("submit", ["job-42"]).AsTask().GetAwaiter().GetResult();
        Assert.Equal("queued", first);
        Assert.Equal("queued", retry);
        Assert.Equal(2, transport.Requests.Count);
        Assert.Equal(transport.Requests[0].IdempotencyKey!, transport.Requests[1].IdempotencyKey!);
        Assert.True(transport.Requests[0].CorrelationId != transport.Requests[1].CorrelationId);
        Assert.Equal("cli.submit", transport.Requests[0].Operation);
    }

    public static void CliPrepareIdempotencyTracksRecipeContent()
    {
        using var recipe = new TemporaryFile();
        var transport = new RecordingTransport();
        var coordinator = new NamedPipeCliCoordinator(transport);
        var relativePath = Path.GetRelativePath(Environment.CurrentDirectory, recipe.Path);
        File.WriteAllText(recipe.Path, "{\"first\":true}");
        _ = coordinator.ExecuteAsync("prepare", [relativePath]).AsTask().GetAwaiter().GetResult();
        _ = coordinator.ExecuteAsync("prepare", [relativePath]).AsTask().GetAwaiter().GetResult();
        Assert.Equal(Path.GetFullPath(relativePath), transport.Requests[0].Payload.GetProperty("arguments")[0].GetString()!);
        Assert.Equal(transport.Requests[0].IdempotencyKey!, transport.Requests[1].IdempotencyKey!);
        Assert.Equal(transport.Requests[0].Payload.GetProperty("recipeSha256").GetString()!,
            transport.Requests[1].Payload.GetProperty("recipeSha256").GetString()!);
        File.WriteAllText(recipe.Path, "{\"second\":true}");
        _ = coordinator.ExecuteAsync("prepare", [recipe.Path]).AsTask().GetAwaiter().GetResult();
        Assert.True(transport.Requests[2].IdempotencyKey != transport.Requests[1].IdempotencyKey);
        Assert.True(transport.Requests[2].Payload.GetProperty("recipeSha256").GetString() != transport.Requests[1].Payload.GetProperty("recipeSha256").GetString());
    }

    private sealed class RecordingTransport : ILocalCoordinatorTransport
    {
        public List<IpcRequestEnvelope> Requests { get; } = [];
        public ValueTask<IpcResponseEnvelope> SendAsync(IpcRequestEnvelope request, CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            using var payload = System.Text.Json.JsonDocument.Parse("{\"result\":\"queued\"}");
            return ValueTask.FromResult(new IpcResponseEnvelope(1, request.CorrelationId, payload.RootElement.Clone(), null));
        }
    }

    public static void CliDoesNotPretendToSubmit()
    {
        var cli = new PaperBatchCli();
        try
        {
            _ = cli.ExecuteAsync(["submit", "job-1"]).AsTask().GetAwaiter().GetResult();
            throw new InvalidOperationException("Expected the unavailable coordinator boundary to reject submit.");
        }
        catch (NotSupportedException exception)
        {
            Assert.Contains("was not executed", exception.Message);
            Assert.Contains("no local coordinator", exception.Message);
        }
        using var output = new StringWriter();
        using var error = new StringWriter();
        var exitCode = PaperBatchCli.ExecuteAsync(["submit", "job-1"], output, error, cli: new PaperBatchCli()).GetAwaiter().GetResult();
        Assert.Equal(3, exitCode);
        Assert.Contains("\"error\":\"unavailable\"", error.ToString());
    }

    private sealed class TemporaryTree : IDisposable
    {
        public TemporaryTree()
        {
            Root = Path.Combine(Path.GetTempPath(), "paperbatch-watch-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
        }
        public string Root { get; }
        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
