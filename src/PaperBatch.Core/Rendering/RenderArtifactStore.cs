using System.Security.Cryptography;
using System.Text.Json;
using PaperBatch.Core.Intake;

namespace PaperBatch.Core.Rendering;

/// <summary>Persists immutable PDF artifacts under opaque, content-derived IDs.</summary>
public sealed class RenderArtifactStore
{
    private const string OwnerMarkerName = ".paperbatch-render-artifacts-v1";
    private const string OwnerMarker = "PaperBatch.RenderArtifacts.v1\n";
    private const int BufferSize = 64 * 1024;
    private readonly string _root;
    private readonly long _maximumArtifactBytes;

    public RenderArtifactStore(string rootPath, long maximumArtifactBytes = 512L * 1024 * 1024)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        if (maximumArtifactBytes <= 0) throw new ArgumentOutOfRangeException(nameof(maximumArtifactBytes));
        _root = Path.GetFullPath(rootPath);
        _maximumArtifactBytes = maximumArtifactBytes;
        InitializeRoot();
    }

    public async Task<StoredPdfArtifact> PersistPdfAsync(
        Stream pdf,
        string engineId,
        string engineVersion,
        IReadOnlyDictionary<string, string> options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(pdf);
        ArgumentException.ThrowIfNullOrWhiteSpace(engineId);
        ArgumentException.ThrowIfNullOrWhiteSpace(engineVersion);
        ArgumentNullException.ThrowIfNull(options);
        if (!pdf.CanRead || !pdf.CanSeek) throw new ArgumentException("The PDF stream must be readable and seekable.", nameof(pdf));
        pdf.Position = 0;
        var tempId = ".tmp_" + Guid.NewGuid().ToString("N");
        var temporaryDirectory = Path.Combine(_root, tempId);
        CreatePrivateDirectory(temporaryDirectory);
        var temporaryContent = Path.Combine(temporaryDirectory, "content.pdf");
        var temporaryReceipt = Path.Combine(temporaryDirectory, "receipt.json");
        try
        {
            long length = 0;
            string digest;
            await using (var output = CreatePrivateFile(temporaryContent))
            using (var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            {
                var buffer = new byte[BufferSize];
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var read = await pdf.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
                    if (read == 0) break;
                    length = checked(length + read);
                    if (length > _maximumArtifactBytes) throw new IOException("The rendered artifact exceeds its configured size limit.");
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    hash.AppendData(buffer, 0, read);
                }
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                digest = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
            }

            if (length == 0) throw new InvalidDataException("A rendered PDF artifact cannot be empty.");
            using (var persisted = new FileStream(temporaryContent, FileMode.Open, FileAccess.Read, FileShare.Read))
                ValidatePdfStructure(persisted);
            var artifactId = "artifact_" + digest;
            var artifact = new PdfArtifact(artifactId, digest, length);
            var receipt = new StoredPdfArtifact(artifact, engineId, engineVersion,
                options.OrderBy(pair => pair.Key, StringComparer.Ordinal)
                    .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal));

            var artifactDirectory = ArtifactDirectory(artifactId);
            if (Directory.Exists(artifactDirectory))
            {
                var existing = ReadReceipt(artifactId);
                using var verified = OpenRead(artifactId);
                if (existing.Artifact.Sha256 != digest || existing.Artifact.LengthBytes != length)
                    throw new IOException("An artifact ID already exists with different content.");
                var sameOptions = existing.Options.Count == receipt.Options.Count && receipt.Options.All(pair =>
                    existing.Options.TryGetValue(pair.Key, out var value) && value == pair.Value);
                if (existing.EngineId != engineId || existing.EngineVersion != engineVersion ||
                    !sameOptions)
                    throw new IOException("Identical PDF bytes already have a different renderer receipt.");
                return existing;
            }

            await using (var output = CreatePrivateFile(temporaryReceipt))
            {
                await JsonSerializer.SerializeAsync(output, receipt, cancellationToken: cancellationToken).ConfigureAwait(false);
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            try
            {
                Directory.Move(temporaryDirectory, artifactDirectory);
                return receipt;
            }
            catch (IOException) when (Directory.Exists(artifactDirectory))
            {
                var winner = ReadReceipt(artifactId);
                using var verifiedWinner = OpenRead(artifactId);
                var winnerOptionsMatch = winner.Options.Count == receipt.Options.Count && receipt.Options.All(pair =>
                    winner.Options.TryGetValue(pair.Key, out var value) && value == pair.Value);
                if (winner.Artifact.Sha256 != digest || winner.Artifact.LengthBytes != length ||
                    winner.EngineId != engineId || winner.EngineVersion != engineVersion || !winnerOptionsMatch)
                    throw new IOException("A concurrently published artifact has conflicting content or provenance.");
                return winner;
            }
        }
        finally
        {
            TryDeleteDirectory(temporaryDirectory);
        }
    }

    /// <summary>Opens the exact persisted PDF after verifying its content-derived ID and receipt.</summary>
    public Stream OpenRead(string artifactId)
    {
        var receipt = ReadReceipt(artifactId);
        var content = new FileStream(ContentPath(artifactId), FileMode.Open, FileAccess.Read, FileShare.Read,
            BufferSize, FileOptions.SequentialScan);
        try
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[BufferSize];
            long length = 0;
            int read;
            while ((read = content.Read(buffer, 0, buffer.Length)) != 0)
            {
                length = checked(length + read);
                if (length > _maximumArtifactBytes) throw new InvalidDataException("The persisted artifact exceeds its configured size limit.");
                hash.AppendData(buffer, 0, read);
            }
            var digest = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
            if (length != receipt.Artifact.LengthBytes || !string.Equals(digest, receipt.Artifact.Sha256, StringComparison.Ordinal) ||
                !string.Equals(artifactId, "artifact_" + digest, StringComparison.Ordinal))
                throw new InvalidDataException("The persisted artifact failed its identity check.");
            content.Position = 0;
            return content;
        }
        catch
        {
            content.Dispose();
            throw;
        }
    }

    public StoredPdfArtifact GetReceipt(string artifactId) => ReadReceipt(artifactId);

    /// <summary>Deletes owned artifacts absent from the caller's complete retained-run reference set.</summary>
    public IReadOnlyList<string> DeleteUnreferencedArtifacts(IReadOnlySet<string> retainedArtifactIds)
    {
        ArgumentNullException.ThrowIfNull(retainedArtifactIds);
        var deleted = new List<string>();
        foreach (var path in Directory.EnumerateDirectories(_root))
        {
            var artifactId = Path.GetFileName(path);
            if (!IsArtifactId(artifactId) || retainedArtifactIds.Contains(artifactId)) continue;
            var attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("An artifact directory cannot be a reparse point.");
            var entries = Directory.GetFileSystemEntries(path);
            if (entries.Any(entry => Path.GetFileName(entry) is not ("content.pdf" or "receipt.json")))
                throw new IOException("An artifact directory contains unexpected entries.");
            foreach (var entry in entries) EnsureNoReparsePoints(entry);
            Directory.Delete(path, recursive: true);
            deleted.Add(artifactId);
        }
        return deleted;
    }

    private static void EnsureNoReparsePoints(string path)
    {
        var attributes = File.GetAttributes(path);
        if ((attributes & FileAttributes.ReparsePoint) != 0)
            throw new IOException("An artifact entry cannot be a reparse point.");
        if ((attributes & FileAttributes.Directory) == 0) return;
        foreach (var entry in Directory.GetFileSystemEntries(path)) EnsureNoReparsePoints(entry);
    }

    private static bool IsArtifactId(string artifactId) =>
        artifactId.Length == 73 && artifactId.StartsWith("artifact_", StringComparison.Ordinal) &&
        artifactId.AsSpan(9).ToString().All(Uri.IsHexDigit);


    private static void ValidatePdfStructure(Stream pdf)
    {
        if (!pdf.CanRead || !pdf.CanSeek || pdf.Length < 16)
            throw new InvalidDataException("The artifact is not a complete PDF document.");

        pdf.Position = 0;
        var header = new byte[Math.Min(1024, checked((int)Math.Min(pdf.Length, 1024)))];
        var headerLength = pdf.Read(header, 0, header.Length);
        if (headerLength < 5 || header.AsSpan(0, headerLength).IndexOf("%PDF-"u8) < 0)
            throw new InvalidDataException("The artifact does not contain a PDF header.");

        var tailLength = (int)Math.Min(pdf.Length, 4096);
        var tail = new byte[tailLength];
        pdf.Position = pdf.Length - tailLength;
        var read = 0;
        while (read < tail.Length)
        {
            var count = pdf.Read(tail, read, tail.Length - read);
            if (count == 0) throw new InvalidDataException("The artifact has a truncated PDF trailer.");
            read += count;
        }
        var text = System.Text.Encoding.ASCII.GetString(tail);
        var eof = text.LastIndexOf("%%EOF", StringComparison.Ordinal);
        var startXref = eof < 0 ? -1 : text.LastIndexOf("startxref", eof, StringComparison.Ordinal);
        if (eof < 0 || startXref < 0 || text[(eof + 5)..].Any(character => !char.IsWhiteSpace(character)))
            throw new InvalidDataException("The artifact is missing a complete PDF trailer.");

        var numberStart = startXref + "startxref".Length;
        while (numberStart < eof && char.IsWhiteSpace(text[numberStart])) numberStart++;
        var numberEnd = numberStart;
        while (numberEnd < eof && char.IsAsciiDigit(text[numberEnd])) numberEnd++;
        if (numberStart == numberEnd || !long.TryParse(text.AsSpan(numberStart, numberEnd - numberStart), out var xrefOffset) ||
            xrefOffset < 0 || xrefOffset >= pdf.Length)
            throw new InvalidDataException("The artifact has an invalid PDF cross-reference offset.");

        pdf.Position = xrefOffset;
        var xref = new byte[1024];
        var xrefLength = pdf.Read(xref, 0, xref.Length);
        var xrefText = System.Text.Encoding.ASCII.GetString(xref, 0, xrefLength).TrimStart();
        var table = xrefText.StartsWith("xref", StringComparison.Ordinal) &&
            (xrefText.Length == 4 || char.IsWhiteSpace(xrefText[4]));
        var stream = xrefText.Contains(" obj", StringComparison.Ordinal) &&
            xrefText.Contains("/Type /XRef", StringComparison.Ordinal);
        if (!table && !stream)
            throw new InvalidDataException("The artifact cross-reference data is invalid.");
    }

    private StoredPdfArtifact ReadReceipt(string artifactId)
    {
        if (string.IsNullOrWhiteSpace(artifactId) || !artifactId.StartsWith("artifact_", StringComparison.Ordinal) ||
            artifactId.Length != 73 || !artifactId.AsSpan(9).ToString().All(Uri.IsHexDigit))
            throw new ArgumentException("An artifact ID is required.", nameof(artifactId));
        StoredPdfArtifact receipt;
        try
        {
            receipt = JsonSerializer.Deserialize<StoredPdfArtifact>(File.ReadAllBytes(ReceiptPath(artifactId)))
                ?? throw new InvalidDataException("The artifact receipt is invalid.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("The artifact receipt is invalid.", exception);
        }
        if (receipt.Artifact is null || receipt.Options is null || receipt.EngineId is null || receipt.EngineVersion is null ||
            receipt.Artifact.ArtifactId != artifactId || receipt.Artifact.Sha256 is null || receipt.Artifact.Sha256.Length != 64 ||
            !receipt.Artifact.Sha256.All(Uri.IsHexDigit) ||
            !string.Equals(receipt.Artifact.Sha256, artifactId[9..], StringComparison.Ordinal) ||
            receipt.Artifact.LengthBytes < 1)
            throw new InvalidDataException("The artifact receipt does not match its artifact ID.");
        return receipt;
    }

    private void InitializeRoot()
    {
        Directory.CreateDirectory(_root);
        if ((File.GetAttributes(_root) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("The artifact root cannot be a reparse point.");
        if (!OperatingSystem.IsWindows())
        {
            const UnixFileMode shared = UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;
            if ((File.GetUnixFileMode(_root) & shared) != 0) File.SetUnixFileMode(_root,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        else
        {
            SourceSnapshotCache.ApplyPrivateWindowsPermissions(_root);
        }
        var marker = Path.Combine(_root, OwnerMarkerName);
        if (File.Exists(marker))
        {
            if (File.ReadAllText(marker) != OwnerMarker) throw new IOException("The artifact root is not owned by PaperBatch.");
            return;
        }
        if (Directory.EnumerateFileSystemEntries(_root).Any()) throw new IOException("The artifact root is not empty and has no PaperBatch owner marker.");
        using var output = CreatePrivateFile(marker);
        using var writer = new StreamWriter(output);
        writer.Write(OwnerMarker);
    }

    private FileStream CreatePrivateFile(string path)
    {
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None, BufferSize = BufferSize };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        return new FileStream(path, options);
    }

    private string ArtifactDirectory(string artifactId) => Path.Combine(_root, artifactId);
    private string ContentPath(string artifactId) => Path.Combine(ArtifactDirectory(artifactId), "content.pdf");
    private string ReceiptPath(string artifactId) => Path.Combine(ArtifactDirectory(artifactId), "receipt.json");
    private static void CreatePrivateDirectory(string path)
    {
        if (OperatingSystem.IsWindows()) Directory.CreateDirectory(path);
        else Directory.CreateDirectory(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }
    private static void TryDelete(string path) { try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    private static void TryDeleteDirectory(string path) { try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
}

public sealed record PdfArtifact(string ArtifactId, string Sha256, long LengthBytes);

public sealed record StoredPdfArtifact(
    PdfArtifact Artifact,
    string EngineId,
    string EngineVersion,
    IReadOnlyDictionary<string, string> Options);
