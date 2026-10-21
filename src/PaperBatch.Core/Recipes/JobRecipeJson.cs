using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using PaperBatch.Core.Domain;
using PaperBatch.Core.Serialization;

namespace PaperBatch.Core.Recipes;

public sealed record RecipeSource(string SourceId, string Location)
{
    /// <summary>Explicit per-source requested values, applied after the first matching rule.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public RecipeSettingsOverride? Override { get; init; }
}

public sealed record RecipePrintSettings(
    int Copies,
    ColorMode ColorMode,
    DuplexMode DuplexMode,
    Orientation Orientation,
    ScalingMode ScalingMode,
    string Media,
    string? PrinterId = null,
    string? TrayId = null);

/// <summary>Optional requested values supported by a saved rule or per-source override.</summary>
public sealed record RecipeSettingsOverride(
    int? Copies = null,
    ColorMode? ColorMode = null,
    DuplexMode? DuplexMode = null,
    Orientation? Orientation = null,
    ScalingMode? ScalingMode = null,
    string? Media = null,
    string? PrinterId = null,
    string? TrayId = null,
    int? NUp = null,
    string? PageRange = null);

/// <summary>First-match rule. FileNameGlob is a bounded `*`/`?` glob against the base filename.</summary>
public sealed record RecipeSettingRule(string RuleId, string FileNameGlob, string? SourceType, RecipeSettingsOverride Values);

public sealed record ResolvedRecipeSettings(RecipePrintSettings Values, int? NUp, string? PageRange,
    IReadOnlyDictionary<string, string> Provenance, string? MatchingRuleId);

/// <summary>Resolves batch/recipe defaults, the first matching rule, then a source override.</summary>
public static class RecipeSettingsResolver
{
    public static ResolvedRecipeSettings Resolve(JobRecipe recipe, RecipeSource source, string sourceType,
        RecipePrintSettings batchDefaults)
    {
        ArgumentNullException.ThrowIfNull(recipe);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(batchDefaults);
        if (sourceType is not ("pdf" or "jpeg" or "png")) throw new ArgumentOutOfRangeException(nameof(sourceType));
        var selectedRule = (recipe.Rules.IsDefault ? [] : recipe.Rules)
            .FirstOrDefault(rule => (rule.SourceType is null || rule.SourceType == sourceType) &&
                IsMatch(Path.GetFileName(source.Location), rule.FileNameGlob));
        var provenance = new Dictionary<string, string>(StringComparer.Ordinal);
        var values = new RecipePrintSettings(
            batchDefaults.Copies, batchDefaults.ColorMode, batchDefaults.DuplexMode, batchDefaults.Orientation,
            batchDefaults.ScalingMode, batchDefaults.Media, batchDefaults.PrinterId, batchDefaults.TrayId);
        foreach (var field in new[] { "copies", "colorMode", "duplexMode", "orientation", "scalingMode", "media", "printerId", "trayId" })
            provenance[field] = "batch/recipe defaults";
        var nUp = (int?)null;
        var range = (string?)null;
        if (selectedRule is not null)
        {
            values = Apply(values, selectedRule.Values);
            ApplyProvenance(provenance, selectedRule.Values, "rule " + selectedRule.RuleId);
            nUp = selectedRule.Values.NUp ?? nUp;
            range = selectedRule.Values.PageRange ?? range;
        }
        if (source.Override is { } perFile)
        {
            values = Apply(values, perFile);
            ApplyProvenance(provenance, perFile, "file override");
            nUp = perFile.NUp ?? nUp;
            range = perFile.PageRange ?? range;
        }
        return new(values, nUp, range, provenance, selectedRule?.RuleId);
    }

    public static bool IsMatch(string fileName, string glob)
    {
        if (fileName.Length > 2048 || fileName.Contains('/') || fileName.Contains('\\') || glob.Length > 128) return false;
        var f = 0; var g = 0; var star = -1; var retry = 0;
        while (f < fileName.Length)
        {
            if (g < glob.Length && (glob[g] == '?' || char.ToUpperInvariant(glob[g]) == char.ToUpperInvariant(fileName[f]))) { f++; g++; }
            else if (g < glob.Length && glob[g] == '*') { star = g++; retry = f; }
            else if (star >= 0) { g = star + 1; f = ++retry; }
            else return false;
        }
        while (g < glob.Length && glob[g] == '*') g++;
        return g == glob.Length;
    }

    private static RecipePrintSettings Apply(RecipePrintSettings current, RecipeSettingsOverride value) => current with
    {
        Copies = value.Copies ?? current.Copies,
        ColorMode = value.ColorMode ?? current.ColorMode,
        DuplexMode = value.DuplexMode ?? current.DuplexMode,
        Orientation = value.Orientation ?? current.Orientation,
        ScalingMode = value.ScalingMode ?? current.ScalingMode,
        Media = value.Media ?? current.Media,
        PrinterId = value.PrinterId ?? current.PrinterId,
        TrayId = value.TrayId ?? current.TrayId
    };

    private static void ApplyProvenance(Dictionary<string, string> provenance, RecipeSettingsOverride value, string origin)
    {
        if (value.Copies is not null) provenance["copies"] = origin;
        if (value.ColorMode is not null) provenance["colorMode"] = origin;
        if (value.DuplexMode is not null) provenance["duplexMode"] = origin;
        if (value.Orientation is not null) provenance["orientation"] = origin;
        if (value.ScalingMode is not null) provenance["scalingMode"] = origin;
        if (value.Media is not null) provenance["media"] = origin;
        if (value.PrinterId is not null) provenance["printerId"] = origin;
        if (value.TrayId is not null) provenance["trayId"] = origin;
        if (value.NUp is not null) provenance["nUp"] = origin;
        if (value.PageRange is not null) provenance["pageRange"] = origin;
    }
}

/// <summary>A local job recipe. Locations are references to user-selected inputs, never embedded document data.</summary>
public sealed record JobRecipe(
    int SchemaVersion,
    string RecipeId,
    string Name,
    ImmutableArray<RecipeSource> Sources,
    RecipePrintSettings Settings)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public ImmutableArray<RecipeSettingRule> Rules { get; init; }
}

public sealed class UnsupportedSchemaVersionException(int requestedVersion, int supportedVersion)
    : NotSupportedException($"Recipe schema version {requestedVersion} is not supported; current version is {supportedVersion}.")
{
    public int RequestedVersion { get; } = requestedVersion;
    public int SupportedVersion { get; } = supportedVersion;
}

public static class JobRecipeJson
{
    public const int CurrentSchemaVersion = 2;
    public const int MaximumRecipeBytes = 256 * 1024;

    private static readonly JsonSerializerOptions SerializerOptions = CreateOptions();

    public static JobRecipe Parse(ReadOnlySpan<byte> utf8Json)
    {
        if (utf8Json.Length > MaximumRecipeBytes)
        {
            throw new InvalidDataException($"Recipe exceeds the {MaximumRecipeBytes}-byte limit.");
        }

        using var document = JsonDocument.Parse(utf8Json.ToArray(), new JsonDocumentOptions
        {
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow,
            MaxDepth = 16
        });

        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException("A recipe must be a JSON object.");
        }

        StrictJson.RejectDuplicateProperties(document.RootElement);

        if (!document.RootElement.TryGetProperty("schemaVersion", out var versionElement) ||
            versionElement.ValueKind != JsonValueKind.Number || !versionElement.TryGetInt32(out var version))
        {
            throw new UnsupportedSchemaVersionException(0, CurrentSchemaVersion);
        }

        if (version is not (1 or CurrentSchemaVersion))
        {
            throw new UnsupportedSchemaVersionException(version, CurrentSchemaVersion);
        }

        if (!document.RootElement.TryGetProperty("settings", out var settingsElement) ||
            settingsElement.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException("Recipe settings must be an object.");
        }

        foreach (var requiredSetting in new[] { "colorMode", "duplexMode", "orientation", "scalingMode" })
        {
            if (!settingsElement.TryGetProperty(requiredSetting, out var settingElement) ||
                settingElement.ValueKind != JsonValueKind.String)
            {
                throw new JsonException($"Recipe setting '{requiredSetting}' is required and must be a string.");
            }
        }

        var recipe = JsonSerializer.Deserialize<JobRecipe>(utf8Json, SerializerOptions)
            ?? throw new JsonException("Recipe JSON produced no value.");
        Validate(recipe);
        return recipe;
    }

    public static string Serialize(JobRecipe recipe)
    {
        ArgumentNullException.ThrowIfNull(recipe);
        Validate(recipe);
        var json = JsonSerializer.SerializeToUtf8Bytes(recipe, SerializerOptions);
        if (json.Length > MaximumRecipeBytes)
        {
            throw new InvalidDataException($"Recipe exceeds the {MaximumRecipeBytes}-byte limit.");
        }

        return Encoding.UTF8.GetString(json);
    }

    private static void Validate(JobRecipe recipe)
    {
        if (recipe.SchemaVersion is not (1 or CurrentSchemaVersion))
        {
            throw new UnsupportedSchemaVersionException(recipe.SchemaVersion, CurrentSchemaVersion);
        }

        ValidateText(recipe.RecipeId, 128, "recipeId");
        ValidateText(recipe.Name, 160, "name");
        if (recipe.Sources.IsDefaultOrEmpty || recipe.Sources.Length > 500)
        {
            throw new JsonException("A recipe must contain between 1 and 500 sources.");
        }

        var sourceIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var source in recipe.Sources)
        {
            if (source is null)
            {
                throw new JsonException("Recipe sources cannot be null.");
            }

            ValidateText(source.SourceId, 128, "sourceId");
            ValidateText(source.Location, 2048, "location");
            if (source.Location.Contains('\0', StringComparison.Ordinal))
            {
                throw new JsonException("Source locations cannot contain NUL characters.");
            }
            if (recipe.SchemaVersion == 1 && source.Override is not null)
                throw new JsonException("Recipe schema version 1 cannot contain per-source setting overrides.");

            if (!sourceIds.Add(source.SourceId))
            {
                throw new JsonException($"Duplicate source ID '{source.SourceId}'.");
            }

            ValidateOverride(source.Override);
        }

        if (recipe.Settings is null || recipe.Settings.Copies is < 1 or > 999 ||
            !Enum.IsDefined(recipe.Settings.ColorMode) || !Enum.IsDefined(recipe.Settings.DuplexMode) ||
            !Enum.IsDefined(recipe.Settings.Orientation) || !Enum.IsDefined(recipe.Settings.ScalingMode))
        {
            throw new JsonException("Recipe settings require valid enum values and a copy count between 1 and 999.");
        }

        ValidateText(recipe.Settings.Media, 128, "media");
        if (recipe.Settings.PrinterId is not null) ValidateText(recipe.Settings.PrinterId, 128, "printerId");
        if (recipe.Settings.TrayId is not null) ValidateText(recipe.Settings.TrayId, 128, "trayId");
        if (recipe.SchemaVersion == 1 && !recipe.Rules.IsDefaultOrEmpty)
            throw new JsonException("Recipe schema version 1 cannot contain setting rules.");
        if (!recipe.Rules.IsDefault && recipe.Rules.Length > 100)
            throw new JsonException("Recipe rules must contain at most 100 entries.");
        var ruleIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var rule in recipe.Rules.IsDefault ? [] : recipe.Rules)
        {
            if (rule is null) throw new JsonException("Recipe rules cannot contain null entries.");
            ValidateText(rule.RuleId, 128, "ruleId");
            if (!ruleIds.Add(rule.RuleId)) throw new JsonException($"Duplicate rule ID '{rule.RuleId}'.");
            ValidateText(rule.FileNameGlob, 128, "fileNameGlob");
            if (rule.FileNameGlob.Contains('/') || rule.FileNameGlob.Contains('\\') || rule.FileNameGlob.Contains('\0'))
                throw new JsonException("Rule globs may match filenames only.");
            if (rule.SourceType is not null && rule.SourceType is not ("pdf" or "jpeg" or "png"))
                throw new JsonException("Rule source type must be pdf, jpeg, or png.");
            ValidateOverride(rule.Values, required: true);
        }
    }

    private static void ValidateOverride(RecipeSettingsOverride? value, bool required = false)
    {
        if (value is null)
        {
            if (required) throw new JsonException("A rule must specify at least one requested setting.");
            return;
        }
        if (value.Copies is < 1 or > 999 || value.NUp is not (null or 1 or 2 or 4) ||
            value.ColorMode is { } color && !Enum.IsDefined(color) ||
            value.DuplexMode is { } duplex && !Enum.IsDefined(duplex) ||
            value.Orientation is { } orientation && !Enum.IsDefined(orientation) ||
            value.ScalingMode is { } scaling && !Enum.IsDefined(scaling))
            throw new JsonException("A rule contains an invalid requested setting.");
        foreach (var (field, text) in new[] { ("media", value.Media), ("printerId", value.PrinterId), ("trayId", value.TrayId), ("pageRange", value.PageRange) })
            if (text is not null) ValidateText(text, field == "pageRange" ? 32 : 128, field);
        if (value.PageRange is not null && !IsPageRange(value.PageRange))
            throw new JsonException("A page range must be a single page or a bounded inclusive range such as 1-5.");
        if (required && value == new RecipeSettingsOverride())
            throw new JsonException("A rule must specify at least one requested setting.");
    }

    private static bool IsPageRange(string value)
    {
        var parts = value.Split('-', StringSplitOptions.None);
        return parts.Length is 1 or 2 && parts.All(part => part.Length is > 0 and <= 7 &&
            part.All(char.IsAsciiDigit) && int.TryParse(part, out var page) && page > 0) &&
            (parts.Length == 1 || int.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture) >= int.Parse(parts[0], System.Globalization.CultureInfo.InvariantCulture));
    }

    private static void ValidateText(string? value, int maximumLength, string field)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maximumLength || value.Any(char.IsControl))
        {
            throw new JsonException($"Recipe field '{field}' must be nonempty, bounded, and contain no control characters.");
        }
    }

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = false,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            MaxDepth = 16,
            WriteIndented = true
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false));
        return options;
    }
}
