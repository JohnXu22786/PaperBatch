namespace PaperBatch.CompatibilityProbe;

public enum EvidenceStatus
{
    Verified,
    Unavailable,
    Blocked,
    NotRun
}

public enum EvidenceSource
{
    Environment,
    Native,
    Simulation
}

public sealed record ProbeEvidence(
    EvidenceStatus Status,
    EvidenceSource Source,
    string Method,
    object? Value,
    string? Limitation)
{
    public static ProbeEvidence Verified(EvidenceSource source, string method, object value) =>
        new(EvidenceStatus.Verified, source, method, value, null);

    public static ProbeEvidence Unavailable(EvidenceSource source, string method, string limitation) =>
        new(EvidenceStatus.Unavailable, source, method, null, limitation);

    public static ProbeEvidence Blocked(EvidenceSource source, string method, string limitation) =>
        new(EvidenceStatus.Blocked, source, method, null, limitation);

    public static ProbeEvidence NotRun(EvidenceSource source, string method, string reason) =>
        new(EvidenceStatus.NotRun, source, method, null, reason);
}

public sealed record G1Evidence(
    ProbeEvidence PdfiumComposition,
    ProbeEvidence PdfiumForms,
    ProbeEvidence MicrosoftOfficeExport,
    ProbeEvidence WpsExport,
    ProbeEvidence LibreOfficeExport,
    ProbeEvidence MicrosoftOfficeCoexistence,
    ProbeEvidence WpsCoexistence,
    ProbeEvidence LibreOfficeCoexistence,
    ProbeEvidence GdiQueueDriverRoute,
    ProbeEvidence GdiJobId,
    ProbeEvidence PhysicalDeviceOutput);

public sealed record G1GateResult(string Status, IReadOnlyList<string> MissingRequirements);

public static class G1GateEvaluator
{
    public static G1GateResult Evaluate(G1Evidence evidence)
    {
        var required = new (string Name, ProbeEvidence Evidence)[]
        {
            ("PDFium page-composition behavior", evidence.PdfiumComposition),
            ("PDFium form behavior", evidence.PdfiumForms),
            ("Microsoft Office document export", evidence.MicrosoftOfficeExport),
            ("WPS document export", evidence.WpsExport),
            ("LibreOffice document export", evidence.LibreOfficeExport),
            ("Microsoft Office coexistence", evidence.MicrosoftOfficeCoexistence),
            ("WPS coexistence", evidence.WpsCoexistence),
            ("LibreOffice coexistence", evidence.LibreOfficeCoexistence),
            ("GDI queue and driver route", evidence.GdiQueueDriverRoute),
            ("GDI job-ID behavior", evidence.GdiJobId),
            ("physical device output", evidence.PhysicalDeviceOutput)
        };

        var missing = required
            .Where(item => item.Evidence.Status != EvidenceStatus.Verified || item.Evidence.Source != EvidenceSource.Native)
            .Select(item => item.Name)
            .ToArray();

        return new G1GateResult(missing.Length == 0 ? "complete" : "open", missing);
    }
}
