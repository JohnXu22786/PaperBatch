namespace PaperBatch.Core.Watch;

public sealed record DiagnosticEntry(string Code, string Message, string? Path = null, string? Secret = null, string? DocumentText = null);

public sealed record DiagnosticReport(DateTimeOffset GeneratedAtUtc, IReadOnlyList<DiagnosticEntry> Diagnostics, bool IncludesSensitiveData);

public static class DiagnosticOutput
{
    public const int MaximumDiagnostics = 100;
    public const int MaximumTextLength = 512;

    public static DiagnosticReport Create(IEnumerable<DiagnosticEntry> diagnostics, bool includeSensitiveData = false)
    {
        ArgumentNullException.ThrowIfNull(diagnostics);
        var entries = diagnostics.Take(MaximumDiagnostics + 1).ToArray();
        if (entries.Length > MaximumDiagnostics) throw new InvalidDataException($"Diagnostic output is limited to {MaximumDiagnostics} entries.");
        return new DiagnosticReport(DateTimeOffset.UtcNow, entries.Select(entry => new DiagnosticEntry(
            SafeCode(entry.Code),
            includeSensitiveData ? Bound(entry.Message, MaximumTextLength) ?? string.Empty : "Sensitive details omitted.",
            includeSensitiveData ? Bound(entry.Path, MaximumTextLength) : null,
            includeSensitiveData ? Bound(entry.Secret, MaximumTextLength) : null,
            includeSensitiveData ? Bound(entry.DocumentText, MaximumTextLength) : null)).ToArray(), includeSensitiveData);
    }

    private static string SafeCode(string? value)
    {
        var safe = new string((value ?? string.Empty).Where(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-').Take(64).ToArray());
        return safe.Length == 0 ? "unknown" : safe;
    }

    private static string? Bound(string? value, int maximum) => value is null ? null : new string(value.Where(character => !char.IsControl(character)).Take(maximum).ToArray());
}
