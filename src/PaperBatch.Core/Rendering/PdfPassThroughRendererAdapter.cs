using PaperBatch.Core.Intake;

namespace PaperBatch.Core.Rendering;

/// <summary>Persists exact PDF snapshots as artifacts without rendering or claiming a page count.</summary>
public sealed class PdfPassThroughRendererAdapter(PdfPassThroughArtifactService service) : IRendererAdapter
{
    public ValueTask<bool> CanRenderAsync(RenderRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(request.ExecutionContext == RendererExecutionContext.BackgroundWorker &&
            request.Format == SourceFormat.Pdf);
    }

    public async Task<RenderResult> RenderAsync(RenderRequest request, CancellationToken cancellationToken)
    {
        if (request.Format != SourceFormat.Pdf)
            return RenderResult.Unsupported("PDF pass-through accepts PDF snapshots only.");
        try
        {
            var stored = await service.PersistAsync(request.SnapshotId, cancellationToken).ConfigureAwait(false);
            return new RenderResult(RenderStatus.Succeeded,
                new RenderArtifact(stored.Artifact.ArtifactId, stored.Artifact.Sha256, stored.Artifact.LengthBytes, 0), null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new RenderResult(RenderStatus.Cancelled, null, "PDF artifact persistence was cancelled.");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException)
        {
            return RenderResult.Failed("The PDF snapshot could not be persisted safely.");
        }
    }
}
