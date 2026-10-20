using System.Diagnostics;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;

namespace PaperBatch.CompatibilityProbe;

public sealed record PdfiumProbeResult(
    ProbeEvidence Identity,
    ProbeEvidence ApiExports,
    ProbeEvidence CompositionBehavior,
    ProbeEvidence FormBehavior);

public sealed record PdfiumIdentity(string RequestedPath, string ResolvedPath, long SizeBytes, string Sha256, string? FileVersion, string Architecture);

public static class PdfiumProbe
{
    private const long MaximumImageSizeBytes = 512L * 1024 * 1024;

    public static PdfiumProbeResult Collect(string? path)
    {
        var behaviorNotRun = "No native PDFium document was loaded; exported API presence does not prove composition or form behavior.";
        var composition = ProbeEvidence.NotRun(EvidenceSource.Native, "native PDFium composition smoke test", behaviorNotRun);
        var forms = ProbeEvidence.NotRun(EvidenceSource.Native, "native PDFium form smoke test", behaviorNotRun);

        if (string.IsNullOrWhiteSpace(path))
        {
            var reason = "Supply an explicit --pdfium-path to inspect a specific binary; the probe does not search for or load a DLL.";
            var unavailableBehavior = "No PDFium binary or Windows native test image was supplied, so composition and form behavior are unavailable in this environment.";
            return new PdfiumProbeResult(
                ProbeEvidence.NotRun(EvidenceSource.Native, "explicit binary inspection", reason),
                ProbeEvidence.NotRun(EvidenceSource.Native, "read-only PE export inspection", reason),
                ProbeEvidence.Unavailable(EvidenceSource.Native, "native PDFium composition smoke test", unavailableBehavior),
                ProbeEvidence.Unavailable(EvidenceSource.Native, "native PDFium form smoke test", unavailableBehavior));
        }

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(path);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            var blocked = ProbeEvidence.Blocked(EvidenceSource.Native, "explicit binary path", exception.Message);
            return new PdfiumProbeResult(blocked, blocked, composition, forms);
        }

        try
        {
            using var stream = LockedFilePath.OpenReadOnlyRegularFile(fullPath, FileShare.Read);
            if (stream.Length > MaximumImageSizeBytes)
            {
                var tooLarge = ProbeEvidence.Blocked(EvidenceSource.Native, "explicit binary inspection", "The binary exceeds the 512 MiB read-only inspection limit.");
                return new PdfiumProbeResult(tooLarge, tooLarge, composition, forms);
            }

            var image = new byte[checked((int)stream.Length)];
            stream.ReadExactly(image);
            if (stream.ReadByte() >= 0)
            {
                throw new IOException("The binary grew while it was being inspected.");
            }

            var digest = Convert.ToHexString(SHA256.HashData(image)).ToLowerInvariant();
            var stablePath = GetMetadataPathForLockedFile(fullPath, stream.SafeFileHandle);
            var resolvedPath = LockedFilePath.ResolveTargetPath(fullPath, stream.SafeFileHandle);
            var version = OperatingSystem.IsWindows()
                ? FileVersionInfo.GetVersionInfo(stablePath).FileVersion
                : null;
            PeImageInfo peInfo;
            try
            {
                peInfo = PeImageParser.Parse(image);
            }
            catch (InvalidDataException exception)
            {
                var identity = ProbeEvidence.Verified(EvidenceSource.Native, "read file metadata and SHA-256; binary was not loaded", new
                {
                    requestedPath = fullPath,
                    resolvedPath,
                    sizeBytes = image.LongLength,
                    sha256 = digest,
                    fileVersion = string.IsNullOrWhiteSpace(version) ? null : version,
                    architecture = (string?)null
                });
                var unsupported = ProbeEvidence.Blocked(EvidenceSource.Native, "read-only PE export inspection", exception.Message);
                return new PdfiumProbeResult(identity, unsupported, composition, forms);
            }

            var identityValue = new PdfiumIdentity(
                fullPath,
                resolvedPath,
                image.LongLength,
                digest,
                string.IsNullOrWhiteSpace(version) ? null : version,
                peInfo.Architecture);
            var capabilities = PdfiumCapabilityClassifier.Classify(peInfo.ExportNames);
            return new PdfiumProbeResult(
                ProbeEvidence.Verified(EvidenceSource.Native, "read PE metadata and SHA-256; binary was not loaded", identityValue),
                ProbeEvidence.Verified(EvidenceSource.Native, "parse PE export table without loading the binary", capabilities),
                composition,
                forms);
        }
        catch (FileNotFoundException exception)
        {
            var unavailable = ProbeEvidence.Unavailable(EvidenceSource.Native, "explicit binary inspection", exception.Message);
            var unavailableBehavior = ProbeEvidence.Unavailable(EvidenceSource.Native, "native PDFium behavior smoke tests", "The supplied PDFium binary is unavailable.");
            return new PdfiumProbeResult(unavailable, unavailable, unavailableBehavior, unavailableBehavior);
        }
        catch (DirectoryNotFoundException exception)
        {
            var unavailable = ProbeEvidence.Unavailable(EvidenceSource.Native, "explicit binary inspection", exception.Message);
            var unavailableBehavior = ProbeEvidence.Unavailable(EvidenceSource.Native, "native PDFium behavior smoke tests", "The supplied PDFium binary is unavailable.");
            return new PdfiumProbeResult(unavailable, unavailable, unavailableBehavior, unavailableBehavior);
        }
        catch (UnauthorizedAccessException exception)
        {
            var blocked = ProbeEvidence.Blocked(EvidenceSource.Native, "read-only binary inspection", exception.Message);
            return new PdfiumProbeResult(blocked, blocked, composition, forms);
        }
        catch (NotSupportedException exception)
        {
            var blocked = ProbeEvidence.Blocked(EvidenceSource.Native, "read-only binary inspection", exception.Message);
            return new PdfiumProbeResult(blocked, blocked, composition, forms);
        }
        catch (IOException exception)
        {
            var blocked = ProbeEvidence.Blocked(EvidenceSource.Native, "read-only binary inspection", exception.Message);
            return new PdfiumProbeResult(blocked, blocked, composition, forms);
        }
        catch (System.ComponentModel.Win32Exception exception)
        {
            var blocked = ProbeEvidence.Blocked(EvidenceSource.Native, "read binary version metadata", exception.Message);
            return new PdfiumProbeResult(blocked, blocked, composition, forms);
        }
    }

    internal static string GetMetadataPathForLockedFile(string path, SafeFileHandle handle) =>
        LockedFilePath.Resolve(path, handle);
}
