using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using PaperBatch.Core.Domain;

namespace PaperBatch.Core.Serialization;

/// <summary>Versioned persistence codec for immutable prepared-run metadata.</summary>
public static class PreparedRunJson
{
    public const int CurrentSchemaVersion = 3;
    public const int MaximumDocumentBytes = 8 * 1024 * 1024;

    private static readonly JsonSerializerOptions SerializerOptions = CreateOptions();

    public static string Serialize(PreparedRun preparedRun)
    {
        ArgumentNullException.ThrowIfNull(preparedRun);
        var hasPageOwnership = preparedRun.Segments.All(segment => !segment.FinalPageNumbers.IsDefaultOrEmpty);
        var version = preparedRun.FinalArtifact is null ? 1 : hasPageOwnership ? CurrentSchemaVersion : 2;
        var document = new StoredPreparedRun(version, ToDocument(preparedRun));
        var bytes = JsonSerializer.SerializeToUtf8Bytes(document, SerializerOptions);
        if (bytes.Length > MaximumDocumentBytes)
        {
            throw new InvalidDataException($"Prepared-run metadata exceeds the {MaximumDocumentBytes}-byte limit.");
        }

        return Encoding.UTF8.GetString(bytes);
    }

    public static PreparedRun Parse(ReadOnlySpan<byte> utf8Json)
    {
        if (utf8Json.Length > MaximumDocumentBytes)
        {
            throw new InvalidDataException($"Prepared-run metadata exceeds the {MaximumDocumentBytes}-byte limit.");
        }

        using var document = JsonDocument.Parse(utf8Json.ToArray(), new JsonDocumentOptions
        {
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow,
            MaxDepth = 32
        });

        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException("Prepared-run metadata must be a JSON object.");
        }

        StrictJson.RejectDuplicateProperties(root);
        if (!root.TryGetProperty("schemaVersion", out var versionElement) ||
            versionElement.ValueKind != JsonValueKind.Number || !versionElement.TryGetInt32(out var version))
        {
            throw new JsonException("Prepared-run metadata must declare an integer schemaVersion.");
        }

        if (version is not (1 or 2 or CurrentSchemaVersion))
        {
            throw new NotSupportedException($"Prepared-run schema version {version} is not supported; current version is {CurrentSchemaVersion}.");
        }

        if (!root.TryGetProperty("run", out var runElement) || runElement.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException("Prepared-run metadata must contain a run object.");
        }

        var stored = JsonSerializer.Deserialize<StoredPreparedRun>(utf8Json, SerializerOptions)
            ?? throw new JsonException("Prepared-run metadata produced no value.");
        var run = stored.Run ?? throw new JsonException("Prepared-run metadata has no run value.");

        var hasFinalArtifactField = runElement.TryGetProperty("finalArtifact", out _);
        if (version == 2 && (!hasFinalArtifactField || run.FinalArtifact is null))
            throw new JsonException("Prepared-run schema version 2 requires a final artifact identity.");
        if (version == 3 && (!hasFinalArtifactField || run.FinalArtifact is null))
            throw new JsonException("Prepared-run schema version 3 requires a final artifact identity.");
        if (version == 1 && hasFinalArtifactField)
            throw new JsonException("Prepared-run schema version 1 cannot contain a final artifact identity.");

        var storedSegments = runElement.TryGetProperty("segments", out var segmentsElement) && segmentsElement.ValueKind == JsonValueKind.Array
            ? segmentsElement.EnumerateArray().ToArray()
            : throw new JsonException("Prepared-run segments are required.");
        foreach (var segmentElement in storedSegments)
        {
            if (segmentElement.ValueKind != JsonValueKind.Object)
                continue;
            var hasPageNumbers = segmentElement.ValueKind == JsonValueKind.Object &&
                segmentElement.TryGetProperty("finalPageNumbers", out var pageNumbersElement) &&
                pageNumbersElement.ValueKind == JsonValueKind.Array && pageNumbersElement.GetArrayLength() > 0;
            if (version == 3 && !hasPageNumbers)
                throw new JsonException("Prepared-run schema version 3 requires final page ownership for every segment.");
            if (version < 3 && segmentElement.TryGetProperty("finalPageNumbers", out _))
                throw new JsonException($"Prepared-run schema version {version} cannot contain final page ownership.");
        }

        try
        {
            var items = (run.Items ?? throw new JsonException("Prepared-run items are required."))
                .Select(item => FromDocument(item ?? throw new JsonException("Prepared-run item entries cannot be null.")))
                .ToArray();
            var segments = (run.Segments ?? throw new JsonException("Prepared-run segments are required."))
                .Select(segment => FromDocument(segment ?? throw new JsonException("Prepared-run segment entries cannot be null.")))
                .ToArray();
            return PreparedRun.Create(
                Required(run.RunId, "runId"),
                run.CreatedAtUtc,
                Required(run.SnapshotFingerprint, "snapshotFingerprint"),
                items,
                segments,
                run.FinalArtifact is null ? null : FromDocument(run.FinalArtifact));
        }
        catch (ArgumentException exception)
        {
            throw new JsonException("Prepared-run metadata violates the domain invariants.", exception);
        }
    }

    private static PreparedRunDocument ToDocument(PreparedRun run) => new(
        run.RunId,
        run.CreatedAtUtc,
        run.SnapshotFingerprint,
        run.Items.Select(ToDocument).ToArray(),
        run.Segments.Select(ToDocument).ToArray(),
        run.FinalArtifact is null ? null : ToDocument(run.FinalArtifact));

    private static FinalPdfArtifactDocument ToDocument(FinalPdfArtifact artifact) => new(
        artifact.ArtifactId, artifact.Sha256, artifact.LengthBytes, artifact.PageCount);

    private static FinalPdfArtifact FromDocument(FinalPdfArtifactDocument artifact) => new(
        Required(artifact.ArtifactId, "finalArtifact.artifactId"),
        Required(artifact.Sha256, "finalArtifact.sha256"),
        artifact.LengthBytes,
        artifact.PageCount);

    private static PreparedItemDocument ToDocument(PreparedItem item) => new(
        item.ItemId,
        new SourceSnapshotDocument(item.Source.SnapshotId, item.Source.Sha256, item.Source.LengthBytes, item.Source.CapturedAtUtc),
        new RendererResultDocument(
            item.Render.RendererId,
            item.Render.RendererVersion,
            item.Render.ArtifactId,
            item.Render.ArtifactSha256,
            item.Render.ArtifactLengthBytes,
            item.Render.PageCount,
            new RendererCapabilitiesDocument(item.Render.Capabilities.ContractVersion, item.Render.Capabilities.SupportedMediaTypes.ToArray())),
        item.PageMap.ToArray(),
        ToDocument(item.PrintSettings));

    private static CompatibleSegmentDocument ToDocument(CompatibleSegment segment) => new(
        segment.SegmentId,
        segment.Index,
        segment.ItemIds.ToArray(),
        segment.CompatibilityKey,
        ToDocument(segment.PrintSettings),
        segment.FinalPageNumbers.IsDefaultOrEmpty ? null : segment.FinalPageNumbers.ToArray());

    private static PrintSettingsDocument ToDocument(PrintSettings settings) => new(
        settings.Copies,
        settings.ColorMode,
        settings.DuplexMode,
        settings.Media,
        settings.Orientation,
        settings.ScalingMode,
        settings.PrinterId,
        settings.TrayId);

    private static PreparedItem FromDocument(PreparedItemDocument item) => new(
        Required(item.ItemId, "itemId"),
        FromDocument(item.Source ?? throw new JsonException("Prepared-item source is required.")),
        FromDocument(item.Render ?? throw new JsonException("Prepared-item renderer result is required.")),
        item.PageMap ?? throw new JsonException("Prepared-item pageMap is required."),
        FromDocument(item.PrintSettings ?? throw new JsonException("Prepared-item printSettings is required.")));

    private static SourceSnapshot FromDocument(SourceSnapshotDocument source) => new(
        Required(source.SnapshotId, "snapshotId"),
        Required(source.Sha256, "sha256"),
        source.LengthBytes,
        source.CapturedAtUtc);

    private static RendererResult FromDocument(RendererResultDocument render) => new(
        Required(render.RendererId, "rendererId"),
        Required(render.RendererVersion, "rendererVersion"),
        Required(render.ArtifactId, "artifactId"),
        Required(render.ArtifactSha256, "artifactSha256"),
        render.ArtifactLengthBytes,
        render.PageCount,
        FromDocument(render.Capabilities ?? throw new JsonException("Renderer capabilities are required.")));

    private static RendererCapabilities FromDocument(RendererCapabilitiesDocument capabilities) => new(
        capabilities.ContractVersion,
        capabilities.SupportedMediaTypes ?? throw new JsonException("Supported media types are required."));

    private static CompatibleSegment FromDocument(CompatibleSegmentDocument segment) => new(
        Required(segment.SegmentId, "segmentId"),
        segment.Index,
        segment.ItemIds ?? throw new JsonException("Segment itemIds are required."),
        Required(segment.CompatibilityKey, "compatibilityKey"),
        FromDocument(segment.PrintSettings ?? throw new JsonException("Segment printSettings are required.")),
        segment.FinalPageNumbers);

    private static PrintSettings FromDocument(PrintSettingsDocument settings) => new(
        settings.Copies,
        settings.ColorMode,
        settings.DuplexMode,
        Required(settings.Media, "media"),
        settings.Orientation,
        settings.ScalingMode,
        settings.PrinterId,
        settings.TrayId);

    private static string Required(string? value, string propertyName) =>
        !string.IsNullOrWhiteSpace(value) && !value.Any(char.IsControl)
            ? value
            : throw new JsonException($"Prepared-run field '{propertyName}' is required.");

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = false,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            MaxDepth = 32
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false));
        return options;
    }

    private sealed record StoredPreparedRun(
        [property: JsonRequired] int SchemaVersion,
        [property: JsonRequired] PreparedRunDocument? Run);

    private sealed record PreparedRunDocument(
        [property: JsonRequired] string? RunId,
        [property: JsonRequired] DateTimeOffset CreatedAtUtc,
        [property: JsonRequired] string? SnapshotFingerprint,
        [property: JsonRequired] PreparedItemDocument[]? Items,
        [property: JsonRequired] CompatibleSegmentDocument[]? Segments,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] FinalPdfArtifactDocument? FinalArtifact);

    private sealed record FinalPdfArtifactDocument(
        [property: JsonRequired] string? ArtifactId,
        [property: JsonRequired] string? Sha256,
        [property: JsonRequired] long LengthBytes,
        [property: JsonRequired] int PageCount);

    private sealed record PreparedItemDocument(
        [property: JsonRequired] string? ItemId,
        [property: JsonRequired] SourceSnapshotDocument? Source,
        [property: JsonRequired] RendererResultDocument? Render,
        [property: JsonRequired] PageMapping[]? PageMap,
        [property: JsonRequired] PrintSettingsDocument? PrintSettings);

    private sealed record SourceSnapshotDocument(
        [property: JsonRequired] string? SnapshotId,
        [property: JsonRequired] string? Sha256,
        [property: JsonRequired] long LengthBytes,
        [property: JsonRequired] DateTimeOffset CapturedAtUtc);

    private sealed record RendererResultDocument(
        [property: JsonRequired] string? RendererId,
        [property: JsonRequired] string? RendererVersion,
        [property: JsonRequired] string? ArtifactId,
        [property: JsonRequired] string? ArtifactSha256,
        [property: JsonRequired] long ArtifactLengthBytes,
        [property: JsonRequired] int PageCount,
        [property: JsonRequired] RendererCapabilitiesDocument? Capabilities);

    private sealed record RendererCapabilitiesDocument(
        [property: JsonRequired] int ContractVersion,
        [property: JsonRequired] string[]? SupportedMediaTypes);

    private sealed record CompatibleSegmentDocument(
        [property: JsonRequired] string? SegmentId,
        [property: JsonRequired] int Index,
        [property: JsonRequired] string[]? ItemIds,
        [property: JsonRequired] string? CompatibilityKey,
        [property: JsonRequired] PrintSettingsDocument? PrintSettings,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int[]? FinalPageNumbers);

    private sealed record PrintSettingsDocument(
        [property: JsonRequired] int Copies,
        [property: JsonRequired] ColorMode ColorMode,
        [property: JsonRequired] DuplexMode DuplexMode,
        [property: JsonRequired] string? Media,
        [property: JsonRequired] Orientation Orientation,
        [property: JsonRequired] ScalingMode ScalingMode,
        string? PrinterId,
        string? TrayId);
}
