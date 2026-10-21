using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
using PaperBatch.Core.Recipes;
using PaperBatch.Core.Watch;
using PaperBatch.Core.Ipc;

if (CoordinatorProcessLauncher.IsHostMode(args))
{
    await using var host = new CoordinatorRuntimeHost("PaperBatch", new ProductionCoordinatorRequestHandler());
    if (!host.IsOwner) return 0;
    await host.RunAsync();
    return 0;
}
return await PaperBatchCli.ExecuteAsync(args, Console.Out, Console.Error);

public interface ICliCoordinator
{
    ValueTask<string> ExecuteAsync(string operation, IReadOnlyList<string> arguments, CancellationToken cancellationToken = default);
    ValueTask<string> GetCapabilitiesAsync(CancellationToken cancellationToken = default);
}

public sealed class NamedPipeCliCoordinator : ICliCoordinator
{
    private readonly ILocalCoordinatorTransport _transport;
    public NamedPipeCliCoordinator(ILocalCoordinatorTransport? transport = null) =>
        _transport = transport ?? new AutoStartingCoordinatorTransport();

    public async ValueTask<string> ExecuteAsync(string operation, IReadOnlyList<string> arguments, CancellationToken cancellationToken = default)
    {
        var requestArguments = arguments.ToArray();
        if (operation is "prepare" or "dry-run" or "watch" && requestArguments.Length == 1)
            requestArguments[0] = Path.GetFullPath(requestArguments[0]);
        string? recipeSha256 = operation == "prepare" && requestArguments.Length == 1 ? ComputeRecipeDigest(requestArguments[0]) : null;
        var payload = JsonSerializer.SerializeToElement(recipeSha256 is null
            ? (object)new { arguments = requestArguments }
            : new { arguments = requestArguments, recipeSha256 });
        var idempotencyKey = CoordinatorRequestKeys.Create(operation, requestArguments, recipeSha256);
        var request = new IpcRequestEnvelope(IpcJson.CurrentProtocolVersion, Guid.NewGuid(), "cli." + operation, idempotencyKey, payload);
        var response = await _transport.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.Error is { } error)
        {
            if (error.Code == "unavailable") throw new NotSupportedException(error.Message);
            if (error.Code == "permission_denied") throw new UnauthorizedAccessException(error.Message);
            throw new IOException($"Coordinator request failed ({error.Code}): {error.Message}");
        }
        return response.Payload!.Value.TryGetProperty("result", out var result) && result.ValueKind == JsonValueKind.String
            ? result.GetString()!
            : response.Payload.Value.GetRawText();
    }

    private static string ComputeRecipeDigest(string path)
    {
        using var stream = new FileStream(Path.GetFullPath(path), FileMode.Open, FileAccess.Read, FileShare.Read,
            16 * 1024, FileOptions.SequentialScan);
        var maximumBytes = JobRecipeJson.MaximumRecipeBytes;
        if (stream.Length > maximumBytes) throw new InvalidDataException("Recipe exceeds the size limit.");
        using var buffer = new MemoryStream();
        var chunk = new byte[Math.Min(16 * 1024, maximumBytes + 1)];
        int read;
        while ((read = stream.Read(chunk, 0, chunk.Length)) > 0)
        {
            if (buffer.Length + read > maximumBytes) throw new InvalidDataException("Recipe exceeds the size limit.");
            buffer.Write(chunk, 0, read);
        }
        return Convert.ToHexString(SHA256.HashData(buffer.GetBuffer().AsSpan(0, checked((int)buffer.Length)))).ToLowerInvariant();
    }

    public async ValueTask<string> GetCapabilitiesAsync(CancellationToken cancellationToken = default)
    {
        var response = await _transport.SendAsync(new IpcRequestEnvelope(IpcJson.CurrentProtocolVersion, Guid.NewGuid(),
            "runtime.status", null, JsonSerializer.SerializeToElement(new { })), cancellationToken).ConfigureAwait(false);
        if (response.Error is { } error) throw new IOException($"Coordinator capability query failed ({error.Code}): {error.Message}");
        return response.Payload!.Value.GetRawText();
    }
}

public sealed class PaperBatchCli
{
    private static readonly HashSet<string> CoordinatorCommands = new(StringComparer.Ordinal) { "dry-run", "prepare", "submit", "status", "cancel", "watch" };
    private readonly ICliCoordinator? _coordinator;
    private readonly string _recipeStoreDirectory;
    private readonly Func<IEnumerable<DiagnosticEntry>>? _diagnosticSource;
    private readonly Func<string, Stream> _openRecipeRead;
    private readonly Func<string, Stream> _createRecipeFile;

    public PaperBatchCli(ICliCoordinator? coordinator = null, string? recipeStoreDirectory = null, Func<IEnumerable<DiagnosticEntry>>? diagnosticSource = null,
        Func<string, Stream>? openRecipeRead = null, Func<string, Stream>? createRecipeFile = null)
    {
        _coordinator = coordinator;
        _recipeStoreDirectory = Path.GetFullPath(recipeStoreDirectory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PaperBatch", "recipes"));
        _diagnosticSource = diagnosticSource;
        _openRecipeRead = openRecipeRead ?? (path => new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 16 * 1024, FileOptions.SequentialScan));
        _createRecipeFile = createRecipeFile ?? (path => new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None));
    }

    public static async Task<int> ExecuteAsync(string[] args, TextWriter output, TextWriter error, ICliCoordinator? coordinator = null, PaperBatchCli? cli = null)
    {
        cli ??= new PaperBatchCli(coordinator ?? new NamedPipeCliCoordinator());
        try
        {
            var result = await cli.ExecuteAsync(args);
            await output.WriteLineAsync(result);
            return 0;
        }
        catch (NotSupportedException exception)
        {
            await error.WriteLineAsync(JsonSerializer.Serialize(new { error = "unavailable", message = exception.Message }));
            return 3;
        }
        catch (UnauthorizedAccessException)
        {
            await error.WriteLineAsync(JsonSerializer.Serialize(new { error = "permission_denied", message = "The requested file operation is not permitted." }));
            return 2;
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or JsonException or InvalidDataException or InvalidOperationException or TimeoutException)
        {
            await error.WriteLineAsync(exception.Message);
            return 2;
        }
    }

    public async ValueTask<string> ExecuteAsync(IReadOnlyList<string> args, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(args);
        if (args.Count == 0 || args[0] is "help" or "--help" or "-h") return Usage;
        var command = args[0];
        if (command == "doctor")
        {
            if (_coordinator is null) throw new NotSupportedException("No local coordinator transport is configured.");
            var capabilities = JsonDocument.Parse(await _coordinator.GetCapabilitiesAsync(cancellationToken));
            return JsonSerializer.Serialize(new { cli = "available", coordinator = "connected", capabilities = capabilities.RootElement.Clone() });
        }
        if (command == "diagnostics")
        {
            if (args.Count > 2 || (args.Count == 2 && args[1] != "--include-sensitive"))
                throw new ArgumentException("Use 'diagnostics [--include-sensitive]'.");
            var includeSensitive = args.Count == 2;
            if (_diagnosticSource is null) throw new NotSupportedException("Diagnostics were not collected: no diagnostic source is configured.");
            return JsonSerializer.Serialize(DiagnosticOutput.Create(_diagnosticSource(), includeSensitive));
        }
        if (command == "recipe") return ExecuteRecipe(args.Skip(1).ToArray());
        if (CoordinatorCommands.Contains(command))
        {
            if (_coordinator is null) throw new NotSupportedException($"Command '{command}' was not executed: no local coordinator transport is configured.");
            return await _coordinator.ExecuteAsync(command, args.Skip(1).ToArray(), cancellationToken);
        }
        throw new ArgumentException($"Unknown command '{command}'.\n{Usage}");
    }

    private string ExecuteRecipe(string[] args)
    {
        if (args.Length == 0) throw new ArgumentException("Use 'recipe validate <file>', 'recipe import <file>', or 'recipe export <recipe-id> <destination-file>'.");
        if (args[0] == "validate" && args.Length == 2)
        {
            var parsed = JobRecipeJson.Parse(ReadRecipeFile(Path.GetFullPath(args[1])));
            return JsonSerializer.Serialize(new { valid = true, schemaVersion = parsed.SchemaVersion, recipeId = parsed.RecipeId, sourceCount = parsed.Sources.Length });
        }
        if (args[0] == "import" && args.Length == 2)
        {
            var parsed = JobRecipeJson.Parse(ReadRecipeFile(Path.GetFullPath(args[1])));
            var serialized = JobRecipeJson.Serialize(parsed) + Environment.NewLine;
            if (!OperatingSystem.IsWindows())
            {
                const UnixFileMode ownerDirectory = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
                Directory.CreateDirectory(_recipeStoreDirectory, ownerDirectory);
                File.SetUnixFileMode(_recipeStoreDirectory, ownerDirectory);
            }
            else Directory.CreateDirectory(_recipeStoreDirectory);
            var destination = RecipeStorePath(parsed.RecipeId);
            if (File.Exists(destination))
            {
                if (string.Equals(File.ReadAllText(destination), serialized, StringComparison.Ordinal)) return "Recipe already imported.";
                throw new IOException("A different recipe with this ID is already imported.");
            }
            using (var stream = CreateRecipeStoreFile(destination))
            using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
            {
                writer.Write(serialized);
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }
            return "Recipe imported to the local recipe store.";
        }
        if (args[0] == "export" && args.Length == 3)
        {
            var recipe = JobRecipeJson.Parse(ReadRecipeFile(RecipeStorePath(args[1])));
            var destination = Path.GetFullPath(args[2]);
            using var stream = _createRecipeFile(destination);
            using var writer = new StreamWriter(stream, new UTF8Encoding(false));
            writer.Write(JobRecipeJson.Serialize(recipe));
            writer.Flush();
            if (stream is FileStream fileStream) fileStream.Flush(flushToDisk: true);
            else stream.Flush();
            return "Recipe exported in canonical v1 format.";
        }
        throw new ArgumentException("Use 'recipe validate <file>', 'recipe import <file>', or 'recipe export <recipe-id> <destination-file>'.");
    }

    private byte[] ReadRecipeFile(string path)
    {
        using var stream = _openRecipeRead(path);
        var maximumBytes = JobRecipeJson.MaximumRecipeBytes;
        if (stream.Length > maximumBytes) throw new InvalidDataException($"Recipe exceeds the {maximumBytes}-byte limit.");
        var bytes = new byte[maximumBytes + 1];
        var total = 0;
        int read;
        while ((read = stream.Read(bytes, total, bytes.Length - total)) > 0)
        {
            total += read;
            if (total > maximumBytes) throw new InvalidDataException($"Recipe exceeds the {maximumBytes}-byte limit.");
        }
        return bytes.AsSpan(0, total).ToArray();
    }

    private static FileStream CreateRecipeStoreFile(string path)
    {
        if (OperatingSystem.IsWindows()) return new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        return new FileStream(path, new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            Share = FileShare.None,
            UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite
        });
    }

    private string RecipeStorePath(string recipeId)
    {
        if (string.IsNullOrWhiteSpace(recipeId)) throw new ArgumentException("Recipe ID is required.", nameof(recipeId));
        var name = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(recipeId))).ToLowerInvariant() + ".json";
        return Path.Combine(_recipeStoreDirectory, name);
    }

    private const string Usage = "PaperBatch CLI\n  doctor\n  dry-run <recipe>\n  prepare <recipe>\n  submit <job-id>\n  status <job-id>\n  cancel <job-id>\n  watch <folder>\n  recipe validate <file>\n  recipe import <file>\n  recipe export <recipe-id> <destination-file>\n  diagnostics [--include-sensitive]\nNo command claims that OS completion is physical output.";
}
