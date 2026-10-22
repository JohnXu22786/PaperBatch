using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using System.Collections.Immutable;
using PaperBatch.Core.Recipes;
using PaperBatch.Core.Properties;

namespace PaperBatch.Core.Ipc;

public interface ICoordinatorRequestHandler
{
    ValueTask<IpcResponseEnvelope> HandleAsync(IpcRequestEnvelope request, CancellationToken cancellationToken = default);
}

/// <summary>Owns the per-user lease and named-pipe endpoint for one host process.</summary>
public sealed class CoordinatorRuntimeHost : IAsyncDisposable
{
    private readonly CoordinatorInstanceLease? _lease;
    private readonly NamedPipeCoordinatorServer? _server;

    public CoordinatorRuntimeHost(string applicationName, ICoordinatorRequestHandler handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        _lease = CoordinatorInstanceLease.TryAcquire(applicationName);
        if (_lease is not null)
            _server = new NamedPipeCoordinatorServer(NamedPipeLocalCoordinatorTransport.CreateCurrentUserPipeName(applicationName), handler.HandleAsync);
    }

    public bool IsOwner => _lease is not null;

    public Task RunAsync(CancellationToken cancellationToken = default) =>
        _server?.RunAsync(cancellationToken) ?? Task.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        if (_server is not null) await _server.DisposeAsync().ConfigureAwait(false);
        _lease?.Dispose();
    }
}

/// <summary>Starts the caller executable in host mode only when its endpoint is not already reachable.</summary>
public static class CoordinatorProcessLauncher
{
    public const string HostArgument = "--coordinator-host";

    public static Process StartHostProcess(string? entryAssemblyPath = null)
    {
        var entry = entryAssemblyPath ?? Assembly.GetEntryAssembly()?.Location;
        if (string.IsNullOrWhiteSpace(entry) || !File.Exists(entry))
            throw new InvalidOperationException("The coordinator host executable path could not be resolved.");

        var processPath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(processPath))
            throw new InvalidOperationException("The current executable path could not be resolved.");

        var startInfo = new ProcessStartInfo(processPath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = AppContext.BaseDirectory
        };
        if (Path.GetFileNameWithoutExtension(processPath).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            startInfo.ArgumentList.Add(entry);
        startInfo.ArgumentList.Add(HostArgument);
        var process = Process.Start(startInfo) ?? throw new IOException("The coordinator host process could not be started.");
        // The host is intentionally long-lived. Close the redirected readers so its lifetime
        // cannot keep a CLI caller's captured stdout/stderr pipes open after the caller exits.
        process.StandardInput.Dispose();
        process.StandardOutput.Dispose();
        process.StandardError.Dispose();
        return process;
    }

    public static bool IsHostMode(IEnumerable<string> args) => args.Contains(HostArgument, StringComparer.Ordinal);
}

public sealed class UnavailableCoordinatorRequestHandler : ICoordinatorRequestHandler
{
    private readonly string[] _supportedOperations;

    public UnavailableCoordinatorRequestHandler(IEnumerable<string>? supportedOperations = null) =>
        _supportedOperations = (supportedOperations ?? []).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();

    public ValueTask<IpcResponseEnvelope> HandleAsync(IpcRequestEnvelope request, CancellationToken cancellationToken = default)
    {
        if (request.Operation == "runtime.status")
        {
            var payload = JsonSerializer.SerializeToElement(new
            {
                protocolVersion = IpcJson.CurrentProtocolVersion,
                maximumMessageBytes = IpcJson.MaximumMessageBytes,
                supportedOperations = _supportedOperations
            });
            return ValueTask.FromResult(new IpcResponseEnvelope(IpcJson.CurrentProtocolVersion,
                request.CorrelationId, payload, null));
        }
        return ValueTask.FromResult(IpcJson.CreateErrorResponse(request.CorrelationId, "unavailable",
            $"Coordinator operation '{request.Operation}' is unavailable."));
    }
}

/// <summary>Production coordinator backed by the existing source, artifact, run-store, and Windows print contracts.</summary>
public sealed class ProductionCoordinatorRequestHandler : ICoordinatorRequestHandler
{
    private static readonly TimeSpan WatchStabilityWindow = TimeSpan.FromSeconds(2);
    private readonly PaperBatch.Core.Preparation.PreparedArtifactCoordinator? _preparation;
    private readonly PaperBatch.Core.Persistence.SqliteRunStore _runs;
    private readonly PaperBatch.Core.Printing.WindowsPrintCoordinator? _printing;
    private readonly string? _preparationUnavailable;

    public ProductionCoordinatorRequestHandler(string? dataRoot = null)
    {
        var appData = AppDataPaths.LocalApplicationData;
        if (string.IsNullOrWhiteSpace(dataRoot) && string.IsNullOrWhiteSpace(appData)) throw new InvalidOperationException("Local application data is unavailable.");
        var root = Path.GetFullPath(dataRoot ?? Path.Combine(appData, "PaperBatch"));
        Directory.CreateDirectory(root);
        var cache = new PaperBatch.Core.Intake.SourceSnapshotCache(new PaperBatch.Core.Intake.SourceSnapshotCacheOptions
        {
            RootPath = Path.Combine(root, "SourceCache")
        });
        var artifacts = new PaperBatch.Core.Rendering.RenderArtifactStore(Path.Combine(root, "RenderArtifacts"));
        var renderer = new PaperBatch.Core.Rendering.RendererCoordinator([
            new PaperBatch.Core.Rendering.PdfPassThroughRendererAdapter(new PaperBatch.Core.Rendering.PdfPassThroughArtifactService(cache, artifacts)),
            new PaperBatch.Core.Rendering.BuiltInImageRendererAdapter(cache, artifacts)
        ]);
        _runs = new PaperBatch.Core.Persistence.SqliteRunStore(Path.Combine(root, "runs.db"));
        _runs.Initialize();
        var pdfiumPath = Environment.GetEnvironmentVariable("PAPERBATCH_PDFIUM_RUNTIME_PATH");
        var pdfiumHash = Environment.GetEnvironmentVariable("PAPERBATCH_PDFIUM_RUNTIME_SHA256");
        if (!string.IsNullOrWhiteSpace(pdfiumPath) && !string.IsNullOrWhiteSpace(pdfiumHash))
        {
            if (!(OperatingSystem.IsWindows() || OperatingSystem.IsLinux()))
                _preparationUnavailable = "Preparation requires a supported Windows or Linux PDFium runtime.";
            else
            {
                try
                {
                    var pdfium = new PaperBatch.Core.Rendering.PdfiumArtifactInspector(pdfiumPath, pdfiumHash);
                    pdfium.ValidateConfiguredRuntime();
                    _preparation = new PaperBatch.Core.Preparation.PreparedArtifactCoordinator(
                        new PaperBatch.Core.Intake.SourceIntakeService(cache), cache, renderer, pdfium, pdfium,
                        new PaperBatch.Core.Layout.PdfiumPageCompositionAdapter(pdfiumPath, pdfiumHash), artifacts, _runs);
                    if (OperatingSystem.IsWindows())
                    {
                        var native = new PaperBatch.Core.Printing.WindowsGdiNativeAdapter();
                        _printing = new PaperBatch.Core.Printing.WindowsPrintCoordinator(_runs,
                            new PaperBatch.Core.Printing.WindowsGdiPrintBackend(native,
                                new PaperBatch.Core.Printing.PinnedPdfiumWindowsPrintRasterizer(pdfiumPath, pdfiumHash)),
                            artifacts: artifacts);
                    }
                }
                catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException or InvalidDataException)
                {
                    _preparationUnavailable = "The configured hash-pinned PDFium runtime is unavailable or does not match its digest.";
                }
            }
        }
        else _preparationUnavailable = "Preparation requires the explicitly configured, hash-pinned PDFium runtime.";
    }

    public async ValueTask<IpcResponseEnvelope> HandleAsync(IpcRequestEnvelope request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            return request.Operation switch
            {
                "runtime.status" => Success(request, new
                {
                    protocolVersion = IpcJson.CurrentProtocolVersion,
                    maximumMessageBytes = IpcJson.MaximumMessageBytes,
                    dryRun = true,
                    prepare = _preparation is not null,
                    submit = _printing is not null,
                    status = true,
                    cancel = _printing is not null,
                    watch = OperatingSystem.IsWindows() ||
                        OperatingSystem.IsLinux() && System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture == System.Runtime.InteropServices.Architecture.X64,
                    officeWps = false,
                    physicalOutput = false
                }),
                "cli.dry-run" => DryRun(request),
                "cli.prepare" => await PrepareAsync(request, cancellationToken).ConfigureAwait(false),
                "desktop.prepare" => await PrepareDesktopAsync(request, cancellationToken).ConfigureAwait(false),
                "cli.submit" => await SubmitAsync(request, cancellationToken).ConfigureAwait(false),
                "cli.status" => await StatusAsync(request, cancellationToken).ConfigureAwait(false),
                "cli.cancel" => await CancelAsync(request, cancellationToken).ConfigureAwait(false),
                "cli.watch" => await WatchAsync(request, cancellationToken).ConfigureAwait(false),
                _ => Unavailable(request, "The requested coordinator operation is unavailable.")
            };
        }
        catch (OperationCanceledException) { throw; }
        catch (UnauthorizedAccessException)
        {
            return PermissionDenied(request, "The coordinator cannot access the requested local file or state.");
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidDataException or IOException or JsonException or InvalidOperationException or NotSupportedException)
        {
            return Unavailable(request, exception.Message);
        }
    }

    private IpcResponseEnvelope DryRun(IpcRequestEnvelope request)
    {
        var recipePath = GetArguments(request, 1)[0];
        var recipe = ReadRecipe(recipePath);
        var inputs = recipe.Sources.Select(source => new PaperBatch.Core.Intake.SourceInput(source.Location)).ToArray();
        var scan = PaperBatch.Core.Intake.SourceEnumerator.Enumerate(inputs);
        var unsupported = recipe.Sources.Any(source => Path.GetExtension(source.Location).ToLowerInvariant() is not (".pdf" or ".jpg" or ".jpeg" or ".png"));
        var status = scan.IsCancelled ? "cancelled" : scan.Items.Any(item => item.HasErrors) || unsupported ? "requires_review" : "enumerated";
        return Success(request, new
        {
            status,
            renderingPerformed = false,
            submissionPerformed = false,
            sourceCount = scan.Items.Length,
            diagnostics = scan.Diagnostics.Select(item => new { code = item.Code.ToString(), message = item.Message })
        });
    }

    private async ValueTask<IpcResponseEnvelope> WatchAsync(IpcRequestEnvelope request, CancellationToken cancellationToken)
    {
        var root = Path.GetFullPath(GetArguments(request, 1)[0]);
        var ruleId = "cli-watch-" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(root))).ToLowerInvariant()[..24];
        var scanner = new PaperBatch.Core.Watch.WatchFolderScanner(
            new PaperBatch.Core.Watch.WatchFolderOptions(root, ruleId, WatchStabilityWindow, IncludeOfficeDocuments: true));
        var outcomes = new List<WatchOutcome>();
        var processor = new PaperBatch.Core.Watch.WatchFolderProcessor(scanner,
            new WatchPreparationAdapter(this, outcomes));
        var observedAt = DateTimeOffset.UtcNow;
        _ = await processor.ReconcileAsync(observedAt, cancellationToken).ConfigureAwait(false);
        await Task.Delay(WatchStabilityWindow, cancellationToken).ConfigureAwait(false);
        var results = await processor.ReconcileAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        return Success(request, new
        {
            status = "scanned",
            preparationPerformed = outcomes.Any(item => item.state == "prepared_for_review"),
            submissionPerformed = false,
            results = outcomes,
            candidateCount = results.Count
        });
    }

    private sealed record WatchOutcome(string relativePath, string state, string? runId = null, string? reason = null);

    private sealed class WatchPreparationAdapter(ProductionCoordinatorRequestHandler owner, List<WatchOutcome> outcomes)
        : PaperBatch.Core.Watch.IWatchFolderCoordinator
    {
        public async ValueTask ProcessAsync(PaperBatch.Core.Watch.WatchFolderCandidate candidate, bool submitAutomatically,
            CancellationToken cancellationToken = default)
        {
            if (submitAutomatically) throw new InvalidOperationException("Watch-folder auto-submit is disabled by the default review policy.");
            var extension = Path.GetExtension(candidate.FullPath).ToLowerInvariant();
            if (candidate.Kind == PaperBatch.Core.Watch.WatchFileKind.Office || extension is not (".pdf" or ".jpg" or ".jpeg" or ".png"))
            {
                outcomes.Add(new WatchOutcome(candidate.RelativePath, "requires_review", reason: "This source format cannot be prepared by the configured coordinator."));
                return;
            }
            if (owner._preparation is null)
                throw new NotSupportedException(owner._preparationUnavailable ?? "Watch-folder preparation requires a validated PDFium runtime.");
            var now = DateTimeOffset.UtcNow;
            var settings = new PaperBatch.Core.Domain.PrintSettings(1, PaperBatch.Core.Domain.ColorMode.Color,
                PaperBatch.Core.Domain.DuplexMode.Simplex, "default", PaperBatch.Core.Domain.Orientation.Auto,
                PaperBatch.Core.Domain.ScalingMode.ShrinkToFit);
            var prepared = await owner._preparation.PrepareAsync(
                [new PaperBatch.Core.Intake.SourceInput(candidate.FullPath, ExpectedSha256: candidate.Sha256)], settings,
                new PaperBatch.Core.Layout.LayoutOptions(), candidate.IdempotencyKey,
                new PaperBatch.Core.Persistence.RetentionMetadata(now.AddDays(30), RetainWhileUnresolved: true), now,
                cancellationToken).ConfigureAwait(false);
            if (!prepared.Succeeded) throw new InvalidOperationException(string.Join(" ", prepared.Diagnostics));
            outcomes.Add(new WatchOutcome(candidate.RelativePath, "prepared_for_review", prepared.Run!.RunId));
        }
    }

    private async ValueTask<IpcResponseEnvelope> PrepareAsync(IpcRequestEnvelope request, CancellationToken cancellationToken)
    {
        if (_preparation is null) return Unavailable(request, _preparationUnavailable ?? "Preparation is unavailable.");
        var recipeBytes = ReadRecipeBytes(GetArguments(request, 1)[0]);
        if (!request.Payload.TryGetProperty("recipeSha256", out var recipeDigestElement) ||
            recipeDigestElement.ValueKind != JsonValueKind.String ||
            !string.Equals(recipeDigestElement.GetString(), Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(recipeBytes)).ToLowerInvariant(), StringComparison.Ordinal))
            return Unavailable(request, "Recipe content changed after the request was fingerprinted; retry with the current recipe.");
        var recipe = ResolveRecipeRelativeSources(PaperBatch.Core.Recipes.JobRecipeJson.Parse(recipeBytes), GetArguments(request, 1)[0]);
        if (recipe.Sources.IsDefaultOrEmpty) return Unavailable(request, "Preparation requires at least one source.");
        var inputs = recipe.Sources.Select(source => new PaperBatch.Core.Intake.SourceInput(source.Location)).ToArray();
        var resolved = new List<PaperBatch.Core.Recipes.ResolvedRecipeSettings>(recipe.Sources.Length);
        foreach (var source in recipe.Sources)
        {
            var type = Path.GetExtension(source.Location).ToLowerInvariant() switch
            {
                ".pdf" => "pdf", ".jpg" or ".jpeg" => "jpeg", ".png" => "png",
                ".doc" or ".docx" or ".xls" or ".xlsx" or ".ppt" or ".pptx" or ".wps" or ".et" or ".dps" => null,
                _ => null
            };
            if (type is null) return Unavailable(request, "Only PDF, JPEG, and PNG sources can be prepared; Office/WPS and unknown types require separate support.");
            resolved.Add(PaperBatch.Core.Recipes.RecipeSettingsResolver.Resolve(recipe, source, type, recipe.Settings));
        }
        var printSettings = resolved.Select(item => ToPrintSettings(item.Values)).ToArray();
        var documentSettings = resolved.Select(item => new PaperBatch.Core.Layout.LayoutDocumentSettings(
            NUp: item.NUp switch { 1 => PaperBatch.Core.Layout.NUpMode.One, 2 => PaperBatch.Core.Layout.NUpMode.Two, 4 => PaperBatch.Core.Layout.NUpMode.Four, _ => null },
            Fit: item.Values.ScalingMode == PaperBatch.Core.Domain.ScalingMode.ActualSize
                ? PaperBatch.Core.Layout.FitMode.ActualSize : PaperBatch.Core.Layout.FitMode.ShrinkToFit,
            SheetSize: ResolveSheetSize(item.Values), Ranges: item.PageRange is null ? default : ParsePageRange(item.PageRange))).ToArray();
        var now = DateTimeOffset.UtcNow;
        var result = await _preparation.PrepareAsync(inputs, printSettings[0], new PaperBatch.Core.Layout.LayoutOptions(Copies: 1),
            request.IdempotencyKey ?? throw new InvalidDataException("Preparation requires an idempotency key."),
            new PaperBatch.Core.Persistence.RetentionMetadata(now.AddDays(30), RetainWhileUnresolved: true), now,
            printSettings, documentSettings, cancellationToken).ConfigureAwait(false);
        if (!result.Succeeded) return Unavailable(request, string.Join(" ", result.Diagnostics));
        return Success(request, new { status = "prepared", runId = result.Run!.RunId, jobIds = _runs.GetJobs(result.Run.RunId).Select(job => job.JobId) });
    }

    private async ValueTask<IpcResponseEnvelope> PrepareDesktopAsync(IpcRequestEnvelope request, CancellationToken cancellationToken)
    {
        if (_preparation is null) return Unavailable(request, _preparationUnavailable ?? "Preparation is unavailable.");
        if (!request.Payload.TryGetProperty("inputs", out var inputsJson) ||
            !request.Payload.TryGetProperty("requestedSettings", out var settingsJson) ||
            !request.Payload.TryGetProperty("layoutOptions", out var layoutJson))
            throw new InvalidDataException("Desktop preparation request is missing required fields.");
        var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow, MaxDepth = 16 };
        var inputs = inputsJson.Deserialize<PaperBatch.Core.Intake.SourceInput[]>(options) ?? throw new InvalidDataException("Source inputs are invalid.");
        var settings = settingsJson.Deserialize<PaperBatch.Core.Domain.PrintSettings>(options) ?? throw new InvalidDataException("Print settings are invalid.");
        var layout = layoutJson.Deserialize<PaperBatch.Core.Layout.LayoutOptions>(options) ?? throw new InvalidDataException("Layout options are invalid.");
        IReadOnlyList<PaperBatch.Core.Domain.PrintSettings>? perItemSettings = request.Payload.TryGetProperty("perItemPrintSettings", out var perItemJson) && perItemJson.ValueKind == JsonValueKind.Array
            ? perItemJson.Deserialize<PaperBatch.Core.Domain.PrintSettings[]>(options) : null;
        IReadOnlyList<PaperBatch.Core.Layout.LayoutDocumentSettings>? perItemLayout = request.Payload.TryGetProperty("perItemLayoutSettings", out var perLayoutJson) && perLayoutJson.ValueKind == JsonValueKind.Array
            ? perLayoutJson.Deserialize<PaperBatch.Core.Layout.LayoutDocumentSettings[]>(options) : null;
        var now = DateTimeOffset.UtcNow;
        var prepared = await _preparation.PrepareAsync(inputs, settings, layout,
            request.IdempotencyKey ?? throw new InvalidDataException("Preparation requires an idempotency key."),
            new PaperBatch.Core.Persistence.RetentionMetadata(now.AddDays(30), RetainWhileUnresolved: true), now,
            perItemSettings, perItemLayout, cancellationToken).ConfigureAwait(false);
        if (!prepared.Succeeded) return Unavailable(request, string.Join(" ", prepared.Diagnostics));
        return Success(request, new { status = "prepared", serializedRun = PaperBatch.Core.Serialization.PreparedRunJson.Serialize(prepared.Run!), diagnostics = prepared.Diagnostics });
    }

    private async ValueTask<IpcResponseEnvelope> SubmitAsync(IpcRequestEnvelope request, CancellationToken cancellationToken)
    {
        if (_printing is null) return Unavailable(request, "Submission requires a configured PDFium runtime and Windows print backend.");
        var jobId = GetArguments(request, 1)[0];
        var job = _runs.GetJob(jobId) ?? throw new ArgumentException("The requested job does not exist.");
        var run = _runs.GetPreparedRun(job.RunId) ?? throw new InvalidDataException("The prepared run is unavailable.");
        var result = await _printing.SubmitAsync(jobId, run,
            request.IdempotencyKey ?? throw new InvalidDataException("Submission requires an idempotency key."),
            DateTimeOffset.UtcNow, cancellationToken).ConfigureAwait(false);
        return Success(request, new { status = result.Status.ToString(), jobId, detail = result.Reason });
    }

    private async ValueTask<IpcResponseEnvelope> StatusAsync(IpcRequestEnvelope request, CancellationToken cancellationToken)
    {
        var jobId = GetArguments(request, 1)[0];
        var job = _runs.GetJob(jobId) ?? throw new ArgumentException("The requested job does not exist.");
        if (_printing is not null && job.OsReceipt is not null)
            job = await _printing.RefreshStatusAsync(jobId, DateTimeOffset.UtcNow, cancellationToken).ConfigureAwait(false);
        return Success(request, new { job.JobId, job.RunId, status = job.State.ToString(), hasOsReceipt = job.OsReceipt is not null });
    }

    private async ValueTask<IpcResponseEnvelope> CancelAsync(IpcRequestEnvelope request, CancellationToken cancellationToken)
    {
        if (_printing is null) return Unavailable(request, "Cancellation requires a configured Windows print backend.");
        var jobId = GetArguments(request, 1)[0];
        var job = await _printing.CancelAsync(jobId, DateTimeOffset.UtcNow, cancellationToken).ConfigureAwait(false);
        return Success(request, new { status = job.State.ToString(), jobId });
    }

    private static JobRecipe ReadRecipe(string path)
    {
        return ResolveRecipeRelativeSources(PaperBatch.Core.Recipes.JobRecipeJson.Parse(ReadRecipeBytes(path)), path);
    }

    private static JobRecipe ResolveRecipeRelativeSources(JobRecipe recipe, string recipePath)
    {
        var recipeDirectory = Path.GetDirectoryName(Path.GetFullPath(recipePath))!;
        return recipe with
        {
            Sources = recipe.Sources.Select(source => source with
            {
                Location = Path.IsPathRooted(source.Location) ? source.Location : Path.GetFullPath(source.Location, recipeDirectory)
            }).ToImmutableArray()
        };
    }

    private static byte[] ReadRecipeBytes(string path)
    {
        var fullPath = Path.GetFullPath(path);
        using var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read, 16 * 1024, FileOptions.SequentialScan);
        if (stream.Length > PaperBatch.Core.Recipes.JobRecipeJson.MaximumRecipeBytes)
            throw new InvalidDataException("Recipe exceeds the size limit.");
        var buffer = new byte[PaperBatch.Core.Recipes.JobRecipeJson.MaximumRecipeBytes + 1];
        var total = 0;
        while (true)
        {
            var read = stream.Read(buffer, total, buffer.Length - total);
            if (read == 0) break;
            total += read;
            if (total > PaperBatch.Core.Recipes.JobRecipeJson.MaximumRecipeBytes)
                throw new InvalidDataException("Recipe exceeds the size limit.");
        }
        return buffer.AsSpan(0, total).ToArray();
    }

    private static string[] GetArguments(IpcRequestEnvelope request, int count)
    {
        if (!request.Payload.TryGetProperty("arguments", out var arguments) || arguments.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("Coordinator request arguments are missing.");
        var result = arguments.EnumerateArray().Select(value => value.ValueKind == JsonValueKind.String
            ? value.GetString()! : throw new InvalidDataException("Coordinator arguments must be strings.")).ToArray();
        if (result.Length != count) throw new ArgumentException($"Operation '{request.Operation}' requires {count} argument(s).");
        return result;
    }

    private static PaperBatch.Core.Domain.PrintSettings ToPrintSettings(PaperBatch.Core.Recipes.RecipePrintSettings settings) =>
        new(settings.Copies, settings.ColorMode, settings.DuplexMode, settings.Media, settings.Orientation,
            settings.ScalingMode, settings.PrinterId, settings.TrayId);

    private static PaperBatch.Core.Layout.PageSize? ResolveSheetSize(PaperBatch.Core.Recipes.RecipePrintSettings settings)
    {
        if (settings.Orientation == PaperBatch.Core.Domain.Orientation.Auto) return null;
        if (settings.Media is not ("Letter" or "A4"))
            throw new NotSupportedException("The requested media size is unavailable to the coordinator layout engine.");
        var size = settings.Media switch
        {
            "Letter" => new PaperBatch.Core.Layout.PageSize(612, 792),
            "A4" => new PaperBatch.Core.Layout.PageSize(595.28, 841.89),
            _ => throw new NotSupportedException("The requested media size is unavailable to the coordinator layout engine.")
        };
        return settings.Orientation == PaperBatch.Core.Domain.Orientation.Landscape
            ? new PaperBatch.Core.Layout.PageSize(size.HeightPoints, size.WidthPoints) : size;
    }

    private static System.Collections.Immutable.ImmutableArray<PaperBatch.Core.Layout.PageRange> ParsePageRange(string value)
    {
        var parts = value.Split('-');
        var first = int.Parse(parts[0], System.Globalization.CultureInfo.InvariantCulture);
        var last = parts.Length == 1 ? first : int.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture);
        return [new PaperBatch.Core.Layout.PageRange(first, last)];
    }

    private static bool IsOfficePath(string path) => Path.GetExtension(path).ToLowerInvariant() is ".doc" or ".docx" or ".xls" or ".xlsx" or ".ppt" or ".pptx" or ".wps" or ".et" or ".dps";
    private static IpcResponseEnvelope Success(IpcRequestEnvelope request, object payload) =>
        new(IpcJson.CurrentProtocolVersion, request.CorrelationId, JsonSerializer.SerializeToElement(payload), null);
    private static IpcResponseEnvelope Unavailable(IpcRequestEnvelope request, string message) =>
        IpcJson.CreateErrorResponse(request.CorrelationId, "unavailable", message);
    private static IpcResponseEnvelope PermissionDenied(IpcRequestEnvelope request, string message) =>
        IpcJson.CreateErrorResponse(request.CorrelationId, "permission_denied", message);
}

public sealed class AutoStartingCoordinatorTransport(ILocalCoordinatorTransport? transport = null) : ILocalCoordinatorTransport
{
    private readonly ILocalCoordinatorTransport _transport = transport ?? new NamedPipeLocalCoordinatorTransport(
        NamedPipeLocalCoordinatorTransport.CreateCurrentUserPipeName(), connectTimeout: TimeSpan.FromMilliseconds(250));

    public async ValueTask<IpcResponseEnvelope> SendAsync(IpcRequestEnvelope request, CancellationToken cancellationToken = default)
    {
        try { return await _transport.SendAsync(request, cancellationToken).ConfigureAwait(false); }
        catch (Exception exception) when (exception is NotSupportedException or IOException)
        {
            using var process = CoordinatorProcessLauncher.StartHostProcess();
            var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
            while (DateTimeOffset.UtcNow < deadline)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try { return await _transport.SendAsync(request, cancellationToken).ConfigureAwait(false); }
                catch (Exception retryException) when (retryException is NotSupportedException or IOException)
                {
                    await Task.Delay(100, cancellationToken).ConfigureAwait(false);
                }
            }
            throw new NotSupportedException("The coordinator host did not become ready within ten seconds.");
        }
    }
}

public static class CoordinatorRequestKeys
{
    public static string Create(string operation, IReadOnlyList<string> arguments, string? contentDigest = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);
        ArgumentNullException.ThrowIfNull(arguments);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new { operation, arguments, contentDigest });
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant();
    }
}
