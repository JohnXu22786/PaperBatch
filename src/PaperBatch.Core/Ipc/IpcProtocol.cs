using System.Text.Json;
using System.Text.Json.Serialization;
using PaperBatch.Core.Serialization;

namespace PaperBatch.Core.Ipc;

public sealed record IpcRequestEnvelope(
    int ProtocolVersion,
    Guid CorrelationId,
    string Operation,
    string? IdempotencyKey,
    JsonElement Payload);

public sealed record IpcResponseEnvelope(
    int ProtocolVersion,
    Guid CorrelationId,
    JsonElement? Payload,
    IpcError? Error);

public sealed record IpcError(string Code, string Message, bool Retryable);

public sealed record NegotiatedCapabilities(int ProtocolVersion, int MaximumMessageBytes);

public static class IpcCapabilities
{
    public static NegotiatedCapabilities Negotiate(
        IEnumerable<int> localProtocolVersions,
        IEnumerable<int> peerProtocolVersions,
        int localMaximumMessageBytes,
        int peerMaximumMessageBytes)
    {
        ArgumentNullException.ThrowIfNull(localProtocolVersions);
        ArgumentNullException.ThrowIfNull(peerProtocolVersions);
        if (localMaximumMessageBytes <= 0 || peerMaximumMessageBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(localMaximumMessageBytes), "Message limits must be positive.");
        }

        var local = localProtocolVersions.Where(version => version > 0).ToHashSet();
        var common = peerProtocolVersions.Where(version => version > 0 && local.Contains(version)).ToArray();
        if (common.Length == 0)
        {
            throw new NotSupportedException("The IPC peers do not share a protocol version.");
        }

        return new NegotiatedCapabilities(
            common.Max(),
            Math.Min(Math.Min(localMaximumMessageBytes, peerMaximumMessageBytes), IpcJson.MaximumMessageBytes));
    }
}

/// <summary>Transport seam for same-user local coordinator connections. It does not imply a network listener.</summary>
public interface ILocalCoordinatorTransport
{
    ValueTask<IpcResponseEnvelope> SendAsync(IpcRequestEnvelope request, CancellationToken cancellationToken = default);
}

public static class IpcJson
{
    public const int CurrentProtocolVersion = 1;
    public const int MaximumMessageBytes = 256 * 1024;
    public const int MaximumErrorMessageLength = 512;
    private const int MaximumOperationLength = 96;
    private const int MaximumIdempotencyKeyLength = 128;

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 16,
        WriteIndented = false
    };

    public static byte[] SerializeRequest(IpcRequestEnvelope request)
    {
        ValidateRequest(request);
        return SerializeBounded(request);
    }

    public static IpcRequestEnvelope ParseRequest(ReadOnlySpan<byte> utf8Json)
    {
        EnsureBounded(utf8Json.Length);
        using var document = ParseDocument(utf8Json);
        var request = document.RootElement.Deserialize<IpcRequestEnvelope>(SerializerOptions)
            ?? throw new JsonException("IPC request JSON produced no value.");
        ValidateRequest(request);
        return request;
    }

    public static byte[] SerializeResponse(IpcResponseEnvelope response)
    {
        ValidateResponse(response);
        return SerializeBounded(response);
    }

    public static IpcResponseEnvelope ParseResponse(ReadOnlySpan<byte> utf8Json)
    {
        EnsureBounded(utf8Json.Length);
        using var document = ParseDocument(utf8Json);
        var response = document.RootElement.Deserialize<IpcResponseEnvelope>(SerializerOptions)
            ?? throw new JsonException("IPC response JSON produced no value.");
        ValidateResponse(response);
        return response;
    }

    public static IpcResponseEnvelope CreateErrorResponse(Guid correlationId, string code, string message, bool retryable = false)
    {
        if (correlationId == Guid.Empty)
        {
            throw new ArgumentException("A correlation ID is required.", nameof(correlationId));
        }

        if (string.IsNullOrWhiteSpace(code) || code.Length > 64 || code.Any(character => !IsTokenCharacter(character)))
        {
            throw new ArgumentException("Error codes must be short token identifiers.", nameof(code));
        }

        ArgumentNullException.ThrowIfNull(message);
        var safeMessage = new string(message.Where(character => !char.IsControl(character)).Take(MaximumErrorMessageLength).ToArray());
        return new IpcResponseEnvelope(
            CurrentProtocolVersion,
            correlationId,
            Payload: null,
            new IpcError(code, safeMessage, retryable));
    }

    private static byte[] SerializeBounded<T>(T value)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, SerializerOptions);
        EnsureBounded(bytes.Length);
        return bytes;
    }

    private static JsonDocument ParseDocument(ReadOnlySpan<byte> utf8Json)
    {
        var document = JsonDocument.Parse(utf8Json.ToArray(), new JsonDocumentOptions
        {
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow,
            MaxDepth = 16
        });
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            document.Dispose();
            throw new JsonException("An IPC envelope must be a JSON object.");
        }

        try
        {
            StrictJson.RejectDuplicateProperties(document.RootElement);
            return document;
        }
        catch
        {
            document.Dispose();
            throw;
        }
    }

    private static void ValidateRequest(IpcRequestEnvelope request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.ProtocolVersion != CurrentProtocolVersion)
        {
            throw new NotSupportedException($"IPC protocol version {request.ProtocolVersion} is not supported.");
        }

        if (request.CorrelationId == Guid.Empty)
        {
            throw new JsonException("IPC requests require a correlation ID.");
        }

        if (string.IsNullOrWhiteSpace(request.Operation) || request.Operation.Length > MaximumOperationLength ||
            request.Operation.Any(character => !IsTokenCharacter(character) && character != '.'))
        {
            throw new JsonException("IPC operation must be a bounded token identifier.");
        }

        if (request.IdempotencyKey is { } key &&
            (key.Length is < 1 or > MaximumIdempotencyKeyLength || key.Any(char.IsControl)))
        {
            throw new JsonException("IPC idempotency keys must be bounded and contain no control characters.");
        }

        if (request.Payload.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException("IPC request payloads must be JSON objects.");
        }

        StrictJson.RejectDuplicateProperties(request.Payload);
    }

    private static void ValidateResponse(IpcResponseEnvelope response)
    {
        ArgumentNullException.ThrowIfNull(response);
        if (response.ProtocolVersion != CurrentProtocolVersion)
        {
            throw new NotSupportedException($"IPC protocol version {response.ProtocolVersion} is not supported.");
        }

        if (response.CorrelationId == Guid.Empty || (response.Payload.HasValue == (response.Error is not null)))
        {
            throw new JsonException("IPC responses require a correlation ID and exactly one payload or error.");
        }

        if (response.Error is { } error)
        {
            if (string.IsNullOrWhiteSpace(error.Code) || error.Code.Length > 64 || error.Code.Any(character => !IsTokenCharacter(character)) ||
                error.Message is null || error.Message.Length > MaximumErrorMessageLength || error.Message.Any(char.IsControl))
            {
                throw new JsonException("IPC error envelopes must contain bounded token codes and messages.");
            }
        }
        else
        {
            StrictJson.RejectDuplicateProperties(response.Payload!.Value);
        }
    }

    private static bool IsTokenCharacter(char character) =>
        character is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '-' or '_';

    private static void EnsureBounded(int size)
    {
        if (size < 0 || size > MaximumMessageBytes)
        {
            throw new InvalidDataException($"IPC message exceeds the {MaximumMessageBytes}-byte limit.");
        }
    }
}
