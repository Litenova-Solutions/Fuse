using Fuse.Collection;
using Fuse.Collection.Models;
using Fuse.Emission.Models;

namespace Fuse.Fusion;

/// <summary>
///     Unified explain service: runs scoped fusion for preview and plan surfaces without duplicating
///     collection and orchestration wiring in each host entry point.
/// </summary>
public interface IExplainService
{
    /// <summary>
    ///     Runs collection and fusion, returning structured data for a text explain preview.
    /// </summary>
    /// <param name="request">The fusion request describing scope, reduction, and emission settings.</param>
    /// <param name="cancellationToken">Token used to cancel collection and fusion.</param>
    /// <returns>The fusion result, every collected candidate path, and a scope description.</returns>
    Task<ExplainPreviewResult> PreviewAsync(FusionRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Runs fusion and returns the context plan without requiring a separate collection pass.
    /// </summary>
    /// <param name="request">The fusion request describing scope and reduction settings.</param>
    /// <param name="normalizedMode">The scoping mode label to attach to the result.</param>
    /// <param name="cancellationToken">Token used to cancel fusion.</param>
    /// <returns>The fusion result and the normalized scoping mode.</returns>
    Task<ExplainPlanResult> PlanAsync(
        FusionRequest request,
        string normalizedMode,
        CancellationToken cancellationToken = default);
}

/// <summary>
///     Structured output for text explain previews (CLI and MCP).
/// </summary>
/// <param name="FusionResult">The fusion result including emitted file token costs.</param>
/// <param name="CollectedPaths">Every candidate path from collection, used to derive excluded files.</param>
/// <param name="ScopeDescription">A short description of the active scope.</param>
public sealed record ExplainPreviewResult(
    FusionResult FusionResult,
    IReadOnlyList<string> CollectedPaths,
    string ScopeDescription);

/// <summary>
///     Structured output for plan-only explain surfaces (VS Code host RPC).
/// </summary>
/// <param name="FusionResult">The fusion result whose <see cref="FusionResult.Plan" /> lists planned files.</param>
/// <param name="NormalizedMode">The scoping mode applied to the request.</param>
public sealed record ExplainPlanResult(FusionResult FusionResult, string NormalizedMode);

/// <summary>
///     Default <see cref="IExplainService" /> implementation delegating to the collection pipeline and
///     fusion orchestrator.
/// </summary>
public sealed class ExplainService : IExplainService
{
    private readonly FileCollectionPipeline _collectionPipeline;
    private readonly FusionOrchestrator _orchestrator;

    /// <summary>
    ///     Initializes a new instance of the <see cref="ExplainService" /> class.
    /// </summary>
    /// <param name="collectionPipeline">The collection pipeline used to enumerate candidate files.</param>
    /// <param name="orchestrator">The fusion orchestrator that runs scoped fusion.</param>
    public ExplainService(FileCollectionPipeline collectionPipeline, FusionOrchestrator orchestrator)
    {
        _collectionPipeline = collectionPipeline;
        _orchestrator = orchestrator;
    }

    /// <inheritdoc />
    public async Task<ExplainPreviewResult> PreviewAsync(
        FusionRequest request,
        CancellationToken cancellationToken = default)
    {
        var collection = await _collectionPipeline.CollectAsync(
            request.Collection,
            request.Parallelism,
            cancellationToken);
        var result = await _orchestrator.FuseAsync(request, cancellationToken);
        var collectedPaths = collection.Files
            .Select(f => f.NormalizedRelativePath)
            .ToArray();

        return new ExplainPreviewResult(result, collectedPaths, FusionScopeDescriptor.Describe(request));
    }

    /// <inheritdoc />
    public async Task<ExplainPlanResult> PlanAsync(
        FusionRequest request,
        string normalizedMode,
        CancellationToken cancellationToken = default)
    {
        var result = await _orchestrator.FuseAsync(request, cancellationToken);
        return new ExplainPlanResult(result, normalizedMode);
    }
}
