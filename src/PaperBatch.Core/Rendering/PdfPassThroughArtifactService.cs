using System.Security.Cryptography;
using PaperBatch.Core.Domain;
using PaperBatch.Core.Intake;

namespace PaperBatch.Core.Rendering;

/// <summary>Copies a safe PDF snapshot into the artifact store without claiming to render it.</summary>
public sealed class PdfPassThroughArtifactService(SourceSnapshotCache snapshots, RenderArtifactStore artifacts)
{
    public async Task<StoredPdfArtifact> PersistAsync(string snapshotId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(snapshotId);
        using var source = snapshots.OpenRead(snapshotId);
        var preflight = SourceFormatPreflight.Inspect(source, ".pdf");
        if (preflight.Status != SourcePreflightStatus.Ready)
            throw new InvalidDataException("The PDF snapshot is malformed, encrypted, or otherwise unsafe for pass-through.");

        source.Position = 0;
        var digest = Convert.ToHexString(SHA256.HashData(source)).ToLowerInvariant();
        var length = source.Length;
        source.Position = 0;
        cancellationToken.ThrowIfCancellationRequested();
        var receipt = await artifacts.PersistPdfAsync(source, "paperbatch.pdf-pass-through", "1",
            new Dictionary<string, string>(StringComparer.Ordinal) { ["operation"] = "exact-byte-copy" },
            cancellationToken).ConfigureAwait(false);
        if (!string.Equals(receipt.Artifact.Sha256, digest, StringComparison.OrdinalIgnoreCase) ||
            receipt.Artifact.LengthBytes != length)
            throw new InvalidDataException("The persisted PDF artifact does not match its source snapshot.");
        return receipt;
    }

    public async Task<StoredPdfArtifact> PersistAsync(SourceSnapshot snapshot, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        using var source = snapshots.OpenRead(snapshot.SnapshotId);
        var preflight = SourceFormatPreflight.Inspect(source, ".pdf");
        if (preflight.Status != SourcePreflightStatus.Ready)
            throw new InvalidDataException("The PDF snapshot is malformed, encrypted, or otherwise unsafe for pass-through.");

        source.Position = 0;
        cancellationToken.ThrowIfCancellationRequested();
        var digest = Convert.ToHexString(SHA256.HashData(source)).ToLowerInvariant();
        if (source.Length != snapshot.LengthBytes ||
            !string.Equals(digest, snapshot.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The PDF snapshot metadata does not match its content.");

        source.Position = 0;
        var receipt = await artifacts.PersistPdfAsync(source, "paperbatch.pdf-pass-through", "1",
            new Dictionary<string, string>(StringComparer.Ordinal) { ["operation"] = "exact-byte-copy" },
            cancellationToken).ConfigureAwait(false);
        if (!string.Equals(receipt.Artifact.Sha256, snapshot.Sha256, StringComparison.OrdinalIgnoreCase) ||
            receipt.Artifact.LengthBytes != snapshot.LengthBytes)
            throw new InvalidDataException("The persisted PDF artifact does not match its source snapshot.");
        return receipt;
    }
}
