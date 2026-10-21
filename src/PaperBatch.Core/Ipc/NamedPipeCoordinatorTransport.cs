using System.Buffers.Binary;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Collections.Concurrent;

namespace PaperBatch.Core.Ipc;

/// <summary>Runs one bounded request at a time per connected pipe client and accepts only current-user clients.</summary>
public sealed class NamedPipeCoordinatorServer : IAsyncDisposable
{
    public const int MaximumConcurrentClients = 8;
    private const int ConnectBufferSize = 4096;
    private readonly TimeSpan _clientFrameTimeout;
    private readonly string _pipeName;
    private readonly Func<IpcRequestEnvelope, CancellationToken, ValueTask<IpcResponseEnvelope>> _dispatch;
    private readonly SemaphoreSlim _clients = new(MaximumConcurrentClients, MaximumConcurrentClients);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly ConcurrentDictionary<Task, byte> _clientTasks = new();
    private Task? _runTask;
    private int _started;

    public NamedPipeCoordinatorServer(string pipeName, Func<IpcRequestEnvelope, CancellationToken, ValueTask<IpcResponseEnvelope>> dispatch,
        TimeSpan? clientFrameTimeout = null)
    {
        if (string.IsNullOrWhiteSpace(pipeName) || pipeName.Length > 200 || pipeName.Any(ch => !(char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_')))
            throw new ArgumentException("Pipe name must be a bounded token.", nameof(pipeName));
        _pipeName = pipeName;
        _dispatch = dispatch ?? throw new ArgumentNullException(nameof(dispatch));
        _clientFrameTimeout = clientFrameTimeout ?? TimeSpan.FromSeconds(5);
        if (_clientFrameTimeout <= TimeSpan.Zero || _clientFrameTimeout > TimeSpan.FromMinutes(1))
            throw new ArgumentOutOfRangeException(nameof(clientFrameTimeout));
    }

    public Task RunAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _started, 1) != 0)
            throw new InvalidOperationException("The coordinator pipe server has already been started.");
        _runTask = AcceptLoopAsync(cancellationToken);
        return _runTask;
    }

    public async ValueTask DisposeAsync()
    {
        _shutdown.Cancel();
        var runTask = _runTask;
        if (runTask is not null)
        {
            try { await runTask.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }
        try { await Task.WhenAll(_clientTasks.Keys.ToArray()).ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        _shutdown.Dispose();
        _clients.Dispose();
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown.Token);
        cancellationToken = linked.Token;
        while (true)
        {
            await _clients.WaitAsync(cancellationToken).ConfigureAwait(false);
            var pipe = new NamedPipeServerStream(_pipeName, PipeDirection.InOut, MaximumConcurrentClients,
                PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly,
                ConnectBufferSize, ConnectBufferSize);
            try
            {
                await pipe.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
                var clientTask = ProcessClientAsync(pipe, cancellationToken);
                _clientTasks.TryAdd(clientTask, 0);
                _ = clientTask.ContinueWith(completed => _clientTasks.TryRemove(completed, out var removed), CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
            catch
            {
                pipe.Dispose();
                _clients.Release();
                throw;
            }
        }
    }

    private async Task ProcessClientAsync(NamedPipeServerStream pipe, CancellationToken cancellationToken)
    {
        await using (pipe.ConfigureAwait(false))
        {
            var maximumMessageBytes = IpcJson.MaximumMessageBytes;
            try
            {
                while (pipe.IsConnected && !cancellationToken.IsCancellationRequested)
                {
                    byte[] requestBytes;
                    using (var frameDeadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
                    {
                        frameDeadline.CancelAfter(_clientFrameTimeout);
                        try { requestBytes = await PipeFrames.ReadAsync(pipe, maximumMessageBytes, frameDeadline.Token).ConfigureAwait(false); }
                        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { break; }
                    }
                    var request = IpcJson.ParseRequest(requestBytes);
                    IpcResponseEnvelope response;
                    if (request.Operation == "runtime.capabilities")
                    {
                        if (!request.Payload.TryGetProperty("maximumMessageBytes", out var clientMaximumElement) ||
                            !clientMaximumElement.TryGetInt32(out var clientMaximum) || clientMaximum <= 0)
                            throw new InvalidDataException("IPC client message limit is invalid.");
                        maximumMessageBytes = Math.Min(clientMaximum, IpcJson.MaximumMessageBytes);
                        using var payload = JsonDocument.Parse("{\"protocolVersions\":[1],\"maximumMessageBytes\":262144}");
                        response = new IpcResponseEnvelope(IpcJson.CurrentProtocolVersion, request.CorrelationId, payload.RootElement.Clone(), null);
                    }
                    else
                    {
                        response = await _dispatch(request, cancellationToken).ConfigureAwait(false);
                        if (response.CorrelationId != request.CorrelationId)
                            response = IpcJson.CreateErrorResponse(request.CorrelationId, "correlation-mismatch", "Coordinator returned a mismatched correlation ID.");
                    }
                    await PipeFrames.WriteAsync(pipe, IpcJson.SerializeResponse(response), cancellationToken, maximumMessageBytes).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
            catch (Exception exception) when (exception is IOException or JsonException or InvalidDataException or NotSupportedException)
            {
                // Invalid or oversized frames are closed without attempting an untrusted response.
            }
            finally { _clients.Release(); }
        }
    }
}

/// <summary>A per-request named-pipe client. Capabilities are negotiated before application operations.</summary>
public sealed class NamedPipeLocalCoordinatorTransport : ILocalCoordinatorTransport
{
    private readonly string _pipeName;
    private readonly TimeSpan _connectTimeout;
    private readonly TimeSpan _requestTimeout;
    private readonly int[] _protocolVersions;
    private readonly int _maximumMessageBytes;

    public NamedPipeLocalCoordinatorTransport(string pipeName, TimeSpan? connectTimeout = null, TimeSpan? requestTimeout = null,
        IEnumerable<int>? protocolVersions = null, int maximumMessageBytes = IpcJson.MaximumMessageBytes)
    {
        if (string.IsNullOrWhiteSpace(pipeName)) throw new ArgumentException("Pipe name is required.", nameof(pipeName));
        if (maximumMessageBytes is <= 0 or > IpcJson.MaximumMessageBytes) throw new ArgumentOutOfRangeException(nameof(maximumMessageBytes));
        _pipeName = pipeName;
        _connectTimeout = connectTimeout ?? TimeSpan.FromSeconds(2);
        _requestTimeout = requestTimeout ?? TimeSpan.FromSeconds(30);
        _protocolVersions = (protocolVersions ?? [IpcJson.CurrentProtocolVersion]).Where(v => v > 0).Distinct().ToArray();
        if (_protocolVersions.Length == 0) throw new ArgumentException("At least one positive protocol version is required.", nameof(protocolVersions));
        _maximumMessageBytes = maximumMessageBytes;
    }

    public async ValueTask<IpcResponseEnvelope> SendAsync(IpcRequestEnvelope request, CancellationToken cancellationToken = default)
    {
        try { return await SendWithinDeadlineAsync(request, cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("The coordinator request exceeded its configured deadline.", exception);
        }
    }

    private async ValueTask<IpcResponseEnvelope> SendWithinDeadlineAsync(IpcRequestEnvelope request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_requestTimeout);
        await using var pipe = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        using (var connect = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token))
        {
            connect.CancelAfter(_connectTimeout);
            try { await pipe.ConnectAsync(connect.Token).ConfigureAwait(false); }
            catch (Exception exception) when (!cancellationToken.IsCancellationRequested && exception is IOException or OperationCanceledException)
            {
                throw new NotSupportedException("The local coordinator is unavailable.", exception);
            }
        }

        var handshakeId = Guid.NewGuid();
        using var handshakePayload = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            protocolVersions = _protocolVersions,
            maximumMessageBytes = _maximumMessageBytes
        }));
        var handshake = new IpcRequestEnvelope(IpcJson.CurrentProtocolVersion, handshakeId, "runtime.capabilities", null, handshakePayload.RootElement.Clone());
        await PipeFrames.WriteAsync(pipe, IpcJson.SerializeRequest(handshake), timeout.Token, _maximumMessageBytes).ConfigureAwait(false);
        var handshakeResponse = IpcJson.ParseResponse(await PipeFrames.ReadAsync(pipe, _maximumMessageBytes, timeout.Token).ConfigureAwait(false));
        if (handshakeResponse.CorrelationId != handshakeId || handshakeResponse.Error is not null)
            throw new InvalidDataException("Coordinator capability negotiation failed.");
        var serverVersions = handshakeResponse.Payload!.Value.GetProperty("protocolVersions").EnumerateArray().Select(item => item.GetInt32()).ToArray();
        var serverMaximum = handshakeResponse.Payload.Value.GetProperty("maximumMessageBytes").GetInt32();
        var negotiated = IpcCapabilities.Negotiate(_protocolVersions, serverVersions, _maximumMessageBytes, serverMaximum);
        if (request.ProtocolVersion != negotiated.ProtocolVersion)
            throw new NotSupportedException("Request protocol version does not match the negotiated coordinator version.");

        await PipeFrames.WriteAsync(pipe, IpcJson.SerializeRequest(request), timeout.Token, negotiated.MaximumMessageBytes).ConfigureAwait(false);
        var response = IpcJson.ParseResponse(await PipeFrames.ReadAsync(pipe, negotiated.MaximumMessageBytes, timeout.Token).ConfigureAwait(false));
        if (response.CorrelationId != request.CorrelationId)
            throw new InvalidDataException("Coordinator response correlation ID does not match the request.");
        return response;
    }

    public static string CreateCurrentUserPipeName(string applicationName = "PaperBatch")
    {
        if (string.IsNullOrWhiteSpace(applicationName)) throw new ArgumentException("Application name is required.", nameof(applicationName));
        var user = OperatingSystem.IsWindows()
            ? System.Security.Principal.WindowsIdentity.GetCurrent().User?.Value
                ?? throw new InvalidOperationException("The current Windows user SID could not be resolved.")
            : Environment.UserName;
        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(applicationName + "\n" + user))).ToLowerInvariant()[..24];
        return "paperbatch-" + digest;
    }
}

internal static class PipeFrames
{
    private const int HeaderLength = sizeof(int);

    public static async ValueTask WriteAsync(Stream stream, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken, int maximumBytes = IpcJson.MaximumMessageBytes)
    {
        if (payload.Length is <= 0 || payload.Length > maximumBytes || payload.Length > IpcJson.MaximumMessageBytes)
            throw new InvalidDataException("IPC frame length is outside the allowed bounds.");
        var header = new byte[HeaderLength];
        BinaryPrimitives.WriteInt32LittleEndian(header, payload.Length);
        await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public static async ValueTask<byte[]> ReadAsync(Stream stream, int maximumBytes, CancellationToken cancellationToken)
    {
        var header = new byte[HeaderLength];
        await ReadExactlyAsync(stream, header, cancellationToken).ConfigureAwait(false);
        var length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length is <= 0 || length > maximumBytes || length > IpcJson.MaximumMessageBytes)
            throw new InvalidDataException("IPC frame length is outside the allowed bounds.");
        var payload = new byte[length];
        await ReadExactlyAsync(stream, payload, cancellationToken).ConfigureAwait(false);
        return payload;
    }

    private static async ValueTask ReadExactlyAsync(Stream stream, Memory<byte> buffer, CancellationToken cancellationToken)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var count = await stream.ReadAsync(buffer[read..], cancellationToken).ConfigureAwait(false);
            if (count == 0) throw new EndOfStreamException("IPC peer closed an incomplete frame.");
            read += count;
        }
    }
}

/// <summary>Cross-process, per-user ownership guard for the shared coordinator process.</summary>
public sealed class CoordinatorInstanceLease : IDisposable
{
    private readonly FileStream _lockFile;

    private CoordinatorInstanceLease(FileStream lockFile) => _lockFile = lockFile;

    public static CoordinatorInstanceLease? TryAcquire(string applicationName = "PaperBatch")
    {
        var pipeName = NamedPipeLocalCoordinatorTransport.CreateCurrentUserPipeName(applicationName);
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PaperBatch", "CoordinatorLocks");
        if (string.IsNullOrWhiteSpace(root)) throw new IOException("The current user's local application data directory is unavailable.");
        if (!OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            File.SetUnixFileMode(root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        else Directory.CreateDirectory(root);
        var path = Path.Combine(root, pipeName + ".lock");
        try
        {
            var options = new FileStreamOptions
            {
                Mode = FileMode.OpenOrCreate,
                Access = FileAccess.ReadWrite,
                Share = FileShare.None,
                Options = FileOptions.WriteThrough
            };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            var stream = new FileStream(path, options);
            return new CoordinatorInstanceLease(stream);
        }
        catch (IOException) { return null; }
    }

    public void Dispose() => _lockFile.Dispose();
}
