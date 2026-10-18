using System.Runtime.InteropServices;

namespace PaperBatch.CompatibilityProbe;

public sealed record EnvironmentMeasurement(
    string OperatingSystem,
    string OperatingSystemArchitecture,
    string ProcessArchitecture,
    string Runtime,
    bool Is64BitProcess,
    string? MachineName);

public sealed record ProbeReport(
    string SchemaVersion,
    DateTimeOffset GeneratedAtUtc,
    ProbeEvidence Environment,
    ProbeEvidence DeviceIdentifiers,
    PdfiumProbeResult Pdfium,
    OfficeProbeResult Office,
    PrintProbeResult Printing,
    G1GateResult G1);

public static class ProbeCollector
{
    public static ProbeReport Collect(
        string? pdfiumPath,
        bool includeLocalQueues,
        bool includeDeviceIdentifiers,
        string? libreOfficeEnginePath = null,
        string? expectedLibreOfficeEngineSha256 = null)
    {
        var machineName = includeDeviceIdentifiers ? Environment.MachineName : null;
        var environment = ProbeEvidence.Verified(
            EvidenceSource.Environment,
            "System.Runtime.InteropServices.RuntimeInformation and process properties",
            new EnvironmentMeasurement(
                RuntimeInformation.OSDescription,
                RuntimeInformation.OSArchitecture.ToString(),
                RuntimeInformation.ProcessArchitecture.ToString(),
                RuntimeInformation.FrameworkDescription,
                Environment.Is64BitProcess,
                machineName));

        var deviceIdentifiers = includeDeviceIdentifiers
            ? ProbeEvidence.Verified(EvidenceSource.Environment, "explicit --include-device-identifiers opt-in", new
            {
                machineName,
                labId = ReadOptionalLabId()
            })
            : ProbeEvidence.NotRun(EvidenceSource.Environment, "privacy-preserving default", "Machine and lab identifiers are omitted unless --include-device-identifiers is supplied.");

        var pdfium = PdfiumProbe.Collect(pdfiumPath);
        var office = OfficeProbe.Collect(expectedLibreOfficeEngineSha256, libreOfficeEnginePath);
        var printing = PrintProbe.Collect(includeLocalQueues);
        var gate = G1GateEvaluator.Evaluate(new G1Evidence(
            pdfium.CompositionBehavior,
            pdfium.FormBehavior,
            office.Exports.MicrosoftOffice,
            office.Exports.WpsOffice,
            office.Exports.LibreOffice,
            office.Coexistence.MicrosoftOffice,
            office.Coexistence.WpsOffice,
            office.Coexistence.LibreOffice,
            printing.GdiQueueDriverRoute,
            printing.GdiJobId,
            printing.PhysicalDeviceOutput));

        return new ProbeReport(
            SchemaVersion: "1.1",
            GeneratedAtUtc: DateTimeOffset.UtcNow,
            Environment: environment,
            DeviceIdentifiers: deviceIdentifiers,
            Pdfium: pdfium,
            Office: office,
            Printing: printing,
            G1: gate);
    }

    private static string? ReadOptionalLabId()
    {
        var value = Environment.GetEnvironmentVariable("PAPERBATCH_PROBE_ENVIRONMENT_ID");
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }
}
