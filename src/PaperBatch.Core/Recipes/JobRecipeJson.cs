using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using PaperBatch.Core.Domain;
using PaperBatch.Core.Serialization;

namespace PaperBatch.Core.Recipes;

public sealed record RecipeSource(string SourceId, string Location);

public sealed record RecipePrintSettings(
    int Copies,
    ColorMode ColorMode,
    DuplexMode DuplexMode,
    Orientation Orientation,
    ScalingMode ScalingMode,
    string Media,
    string? PrinterId = null,
    string? TrayId = null);

/// <summary>A local job recipe. Locations are references to user-selected inputs, never embedded document data.</summary>
public sealed record JobRecipe(
    int SchemaVersion,
    string RecipeId,
    string Name,
    ImmutableArray<RecipeSource> Sources,
    RecipePrintSettings Settings);

public sealed class UnsupportedSchemaVersionException(int requestedVersion, int supportedVersion)
    : NotSupportedException($"Recipe schema version {requestedVersion} is not supported; current version is {supportedVersion}.")
{
    public int RequestedVersion { get; } = requestedVersion;
    public int SupportedVersion { get; } = supportedVersion;
}

public static class JobRecipeJson
{
    public const int CurrentSchemaVersion = 1;
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

        if (version != CurrentSchemaVersion)
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
        if (recipe.SchemaVersion != CurrentSchemaVersion)
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

            if (!sourceIds.Add(source.SourceId))
            {
                throw new JsonException($"Duplicate source ID '{source.SourceId}'.");
            }
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
