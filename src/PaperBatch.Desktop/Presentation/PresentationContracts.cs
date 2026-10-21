using Avalonia.Media.Imaging;
using PaperBatch.Core.Domain;
using PaperBatch.Core.Layout;
using PaperBatch.Core.Preparation;

namespace PaperBatch.Desktop.Presentation;

internal enum ShellSection
{
    Queue,
    History,
    Templates
}

internal sealed record QueueItemSnapshot(string Id, string Title, string Details, string Status);

internal sealed record EffectiveSettingSnapshot(string Name, string Value, string Explanation);

internal sealed record PreviewSnapshot(string ItemId, string Description, int PageNumber, int PageCount,
    Bitmap? PageImage = null, string? ArtifactId = null, string? ArtifactSha256 = null);

internal sealed record PreflightFindingSnapshot(string Title, string Status, string Detail);

internal sealed record HistoryEntrySnapshot(string Title, string Summary, string Status);

internal sealed record TemplateSnapshot(string RecipeId, string Name, string Summary, string SettingsSummary);

internal sealed record WorkspaceStatusSnapshot(string Title, string Detail, bool IsDemo, bool InitializationFailed = false);

internal interface IQueuePresentationSource
{
    IReadOnlyList<QueueItemSnapshot> GetQueueItems();
}

internal interface IQueueEditingPresentationSource : IQueuePresentationSource
{
    void Move(string queueItemId, int delta);
    void Remove(string queueItemId);
}

internal interface IQueueImportPresentationSource : IQueuePresentationSource
{
    bool IsAvailable { get; }
    Task<IReadOnlyList<QueueItemSnapshot>> ImportAsync(IEnumerable<string> paths, CancellationToken cancellationToken = default);
}

internal interface IEffectiveSettingsPresentationSource
{
    IReadOnlyList<EffectiveSettingSnapshot> GetEffectiveSettings(string queueItemId);
}

internal interface IFinalPreviewPresentationSource
{
    Task<PreviewSnapshot> GetPreviewAsync(string queueItemId, CancellationToken cancellationToken = default);
}

internal interface IPreparedArtifactPresentationSource
{
    event Action<string>? PreparedArtifactChanged;
    void RegisterPreparedRun(string queueItemId, PreparedRun run);
    void InvalidatePreparedRun(string queueItemId);
}

internal interface IQueuePreparationPresentationSource
{
    bool CanPrepare { get; }
    IReadOnlyList<string> ResolutionSummaries { get; }
    Task<PreparedArtifactResult> PrepareQueueAsync(PrintSettings requestedSettings, LayoutOptions layoutOptions,
        PaperBatch.Core.Recipes.JobRecipe? recipe = null, CancellationToken cancellationToken = default);
}

internal interface IPreflightPresentationSource
{
    IReadOnlyList<PreflightFindingSnapshot> GetFindings(string queueItemId);
}

internal interface IHistoryPresentationSource
{
    IReadOnlyList<HistoryEntrySnapshot> GetHistoryEntries();
    string? HistoryReadError { get; }
}

internal interface ITemplatePresentationSource
{
    IReadOnlyList<TemplateSnapshot> GetTemplates(bool chinese = false);
    string? TemplateReadError { get; }
}

internal interface IRecipeManagementPresentationSource : ITemplatePresentationSource
{
    bool IsRecipeStoreAvailable { get; }
    JobRecipeSnapshot CreateRecipe(string name, IReadOnlyList<string> queueItemIds, RecipeDefaultsSnapshot defaults);
    void SaveRecipe(JobRecipeSnapshot recipe);
    JobRecipeSnapshot GetRecipe(string recipeId);
    bool DeleteRecipe(string recipeId);
    void ImportRecipe(string path);
    void ExportRecipe(string recipeId, string path);
    IReadOnlyList<string> GetRecipeSourceLocations(string recipeId);
}

internal sealed record JobRecipeSnapshot(int SchemaVersion, string RecipeId, string Name,
    System.Collections.Immutable.ImmutableArray<PaperBatch.Core.Recipes.RecipeSource> Sources,
    PaperBatch.Core.Recipes.RecipePrintSettings Settings,
    System.Collections.Immutable.ImmutableArray<PaperBatch.Core.Recipes.RecipeSettingRule> Rules = default);

internal sealed record RecipeDefaultsSnapshot(int Copies, PaperBatch.Core.Domain.ColorMode ColorMode,
    PaperBatch.Core.Domain.DuplexMode DuplexMode, PaperBatch.Core.Domain.Orientation Orientation,
    PaperBatch.Core.Domain.ScalingMode ScalingMode, string Media);
