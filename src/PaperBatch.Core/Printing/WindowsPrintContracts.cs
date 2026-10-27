using System.Collections.Immutable;
using PaperBatch.Core.Domain;
using PaperBatch.Core.Persistence;

namespace PaperBatch.Core.Printing;

public enum CapabilityState
{
    Supported,
    Unsupported,
    Unavailable,
    Unknown
}

public enum PrintOption
{
    Media,
    Tray,
    Color,
    Duplex,
    Resolution,
    Finishing,
    Copies
}

public sealed record PrintOptionCapability(
    CapabilityState State,
    ImmutableArray<string> Values,
    string? Detail)
{
    public PrintOptionCapability(CapabilityState state, IEnumerable<string> values, string? detail)
        : this(state, values?.ToImmutableArray() ?? throw new ArgumentNullException(nameof(values)), detail) { }
}

/// <summary>A point-in-time view of canonical option values supported by the selected driver.</summary>
public sealed record PrinterCapabilitySnapshot(
    string QueueIdentity,
    string DisplayName,
    string DriverName,
    string PortName,
    string Fingerprint,
    ImmutableDictionary<PrintOption, PrintOptionCapability> Options)
{
    /// <summary>Exact driver-native tokens for reported values when the API exposes them.</summary>
    public ImmutableDictionary<PrintOption, ImmutableDictionary<string, string>> NativeTokens { get; init; } =
        ImmutableDictionary<PrintOption, ImmutableDictionary<string, string>>.Empty;

    public PrinterCapabilitySnapshot(
        string queueIdentity,
        string displayName,
        string driverName,
        string portName,
        string fingerprint,
        IReadOnlyDictionary<PrintOption, PrintOptionCapability> options)
        : this(queueIdentity, displayName, driverName, portName, fingerprint,
            options?.ToImmutableDictionary() ?? throw new ArgumentNullException(nameof(options)))
    {
    }
}

public sealed record WindowsPrintJobOptions(
    string QueueIdentity,
    string CapabilityFingerprint,
    string Media,
    string? Tray,
    string Color,
    string Duplex)
{
    public static WindowsPrintJobOptions Map(
        string queueIdentity,
        PrinterCapabilitySnapshot capabilities,
        PrintSettings settings)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(queueIdentity);
        ArgumentNullException.ThrowIfNull(capabilities);
        ArgumentNullException.ThrowIfNull(settings);
        if (!string.Equals(queueIdentity, capabilities.QueueIdentity, StringComparison.Ordinal))
            throw new PrintCapabilityException("The selected queue identity changed.");

        var media = Require(capabilities, PrintOption.Media, settings.Media);
        var tray = settings.TrayId is null ? null : Require(capabilities, PrintOption.Tray, settings.TrayId);
        var color = Require(capabilities, PrintOption.Color, settings.ColorMode == ColorMode.Color ? "Color" : "Monochrome");
        var duplex = Require(capabilities, PrintOption.Duplex, settings.DuplexMode.ToString());
        RequireCopies(capabilities, settings.Copies);
        return new WindowsPrintJobOptions(queueIdentity, capabilities.Fingerprint, media, tray, color, duplex);
    }

    private static void RequireCopies(PrinterCapabilitySnapshot snapshot, int copies)
    {
        if (!snapshot.Options.TryGetValue(PrintOption.Copies, out var capability) || capability.State != CapabilityState.Supported)
            throw new PrintCapabilityException("Copies are not reported as supported by this driver.");
        var maximum = capability.Values.Select(value => value.Split("..", StringSplitOptions.TrimEntries))
            .Where(parts => parts.Length == 2 && parts[0] == "1" && int.TryParse(parts[1], out _))
            .Select(parts => int.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture)).DefaultIfEmpty(0).Max();
        if (copies < 1 || maximum < copies)
            throw new PrintCapabilityException("The requested copy count is outside the driver's reported supported range.");
    }

    private static string Require(PrinterCapabilitySnapshot snapshot, PrintOption option, string requested)
    {
        if (!snapshot.Options.TryGetValue(option, out var capability))
            throw new PrintCapabilityException($"The driver did not report {option} capability.");
        if (capability.State != CapabilityState.Supported)
            throw new PrintCapabilityException($"{option} is {capability.State}: {capability.Detail ?? "no driver detail"}.");
        if (!capability.Values.Contains(requested, StringComparer.Ordinal))
            throw new PrintCapabilityException($"The selected {option} value is not reported by this driver.");
        return requested;
    }
}

public sealed class PrintCapabilityException(string message) : InvalidOperationException(message);

public sealed class PrintSubmissionException(string message, bool mayHaveReachedOs, Exception? innerException = null)
    : IOException(message, innerException)
{
    public bool MayHaveReachedOs { get; } = mayHaveReachedOs;
}

public enum PrintBackendJobStatus
{
    Accepted,
    Processing,
    ReportedComplete,
    Failed,
    Cancelled,
    Unknown
}

public enum PrintSubmissionStatus
{
    Accepted,
    Rejected,
    FailedBeforeSubmit,
    NeedsReviewUnknown
}

public sealed record PrintSubmissionResult(PrintSubmissionStatus Status, StoredJob? Job, string? Reason);

public sealed record WindowsPrintSubmission(
    WindowsPrintJobOptions Options,
    CompatibleSegment Segment,
    string RunNonce,
    DateTimeOffset SubmittedAtUtc,
    FinalPdfArtifact Artifact,
    Stream PdfContent,
    IReadOnlyList<int> FinalPageNumbers);

/// <summary>All native calls are isolated here; implementations must own and dispose handles they open.</summary>
public interface IWindowsPrintBackend
{
    Task<PrinterCapabilitySnapshot> GetCapabilitiesAsync(string queueIdentity, CancellationToken cancellationToken = default);
    Task<OsJobReceipt> SubmitAsync(WindowsPrintSubmission submission, CancellationToken cancellationToken = default);
    Task<PrintBackendJobStatus> GetStatusAsync(OsJobReceipt receipt, CancellationToken cancellationToken = default);
    Task<bool> CancelAsync(OsJobReceipt receipt, CancellationToken cancellationToken = default);
}
