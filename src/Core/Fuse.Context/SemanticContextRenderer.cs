using Fuse.Collection.FileSystem;
using Fuse.Collection.Models;
using Fuse.Plugins.Abstractions.Options;
using Fuse.Reduction;
using Fuse.Retrieval;

namespace Fuse.Context;

/// <summary>
///     Renders a <see cref="ContextPlan" /> into per-file source payloads, each at the render tier the plan
///     assigned, by reusing the content reduction pipeline.
/// </summary>
/// <remarks>
///     Each render tier maps to a reduction level: full source to <see cref="ReductionLevel.None" />, reduced
///     to <see cref="ReductionLevel.Standard" />, skeleton and sketch to <see cref="ReductionLevel.Skeleton" />,
///     and public API to <see cref="ReductionLevel.PublicApi" />. Omitted items are listed by the plan but not
///     rendered. Redaction runs as part of the pipeline, so secrets never reach the payload. The whole plan is
///     reduced in a single pipeline pass using a per-file level selector.
/// </remarks>
public sealed class SemanticContextRenderer
{
    private readonly ContentReductionPipeline _reductionPipeline;
    private readonly ISourceContentProvider _contentProvider;

    /// <summary>
    ///     Initializes a new instance of the <see cref="SemanticContextRenderer" /> class.
    /// </summary>
    /// <param name="reductionPipeline">The content reduction pipeline that applies per-file tiers and redaction.</param>
    /// <param name="contentProvider">The provider used to read source files.</param>
    public SemanticContextRenderer(ContentReductionPipeline reductionPipeline, ISourceContentProvider contentProvider)
    {
        _reductionPipeline = reductionPipeline;
        _contentProvider = contentProvider;
    }

    /// <summary>
    ///     Renders the plan's files at their assigned tiers.
    /// </summary>
    /// <param name="plan">The context plan to render.</param>
    /// <param name="rootDirectory">The workspace root, used to resolve file paths.</param>
    /// <param name="cancellationToken">A token to cancel rendering.</param>
    /// <returns>The rendered files (Omitted and missing files excluded) and the total token count.</returns>
    public async Task<RenderedContext> RenderAsync(ContextPlan plan, string rootDirectory, CancellationToken cancellationToken)
    {
        var root = Path.GetFullPath(rootDirectory);
        var renderable = new List<(ContextPlanItem Item, SourceFile File)>();
        foreach (var item in plan.Items)
        {
            if (item.Tier == RenderTier.Omitted)
                continue;

            var absolutePath = Path.GetFullPath(Path.Combine(root, item.Path));
            var fileInfo = new FileInfo(absolutePath);
            if (!fileInfo.Exists)
                continue;

            renderable.Add((item, new SourceFile(new FileCandidate(absolutePath, item.Path, fileInfo))));
        }

        if (renderable.Count == 0)
            return new RenderedContext([], 0);

        var levelByPath = renderable.ToDictionary(
            r => r.File.NormalizedRelativePath,
            r => TierToLevel(r.Item.Tier),
            StringComparer.Ordinal);

        var options = new ReductionOptions
        {
            Level = ReductionLevel.Standard,
            TrimContent = true,
            UseCondensing = true,
            EnableRedaction = true,
        };

        var reduced = await _reductionPipeline.ReduceAsync(
            renderable.Select(r => r.File).ToList(),
            options,
            _contentProvider,
            Environment.ProcessorCount,
            reductionCache: null,
            tokenCounterOverride: null,
            perFileLevel: file => levelByPath.GetValueOrDefault(file.NormalizedRelativePath, ReductionLevel.Standard),
            cancellationToken);

        var itemByPath = renderable.ToDictionary(r => r.File.NormalizedRelativePath, r => r.Item, StringComparer.Ordinal);
        var files = new List<RenderedFile>(reduced.Count);
        foreach (var content in reduced)
        {
            if (!itemByPath.TryGetValue(content.SourceFile.NormalizedRelativePath, out var item))
                continue;

            files.Add(new RenderedFile(
                Path: item.Path,
                Role: item.Role,
                Tier: item.Tier,
                Score: item.Score,
                Content: content.Content,
                TokenCount: content.TokenCount,
                Provenance: item.ProvenanceChain));
        }

        // Preserve the plan's ranked order (the pipeline preserves input order, which is the plan order here).
        return new RenderedContext(files, files.Sum(f => f.TokenCount));
    }

    private static ReductionLevel TierToLevel(RenderTier tier) => tier switch
    {
        RenderTier.FullSource => ReductionLevel.None,
        RenderTier.Reduced => ReductionLevel.Standard,
        RenderTier.PublicApi => ReductionLevel.PublicApi,
        RenderTier.Skeleton or RenderTier.Sketch => ReductionLevel.Skeleton,
        _ => ReductionLevel.Standard,
    };
}

/// <summary>
///     The rendered context: one entry per included file plus the total token count.
/// </summary>
/// <param name="Files">The rendered files in plan order.</param>
/// <param name="TotalTokens">The total token count across the rendered files.</param>
public sealed record RenderedContext(IReadOnlyList<RenderedFile> Files, int TotalTokens);

/// <summary>
///     A single rendered file.
/// </summary>
/// <param name="Path">The file's normalized path.</param>
/// <param name="Role">The file's role in the plan.</param>
/// <param name="Tier">The render tier applied.</param>
/// <param name="Score">The retrieval score.</param>
/// <param name="Content">The rendered (reduced and redacted) content.</param>
/// <param name="TokenCount">The token count of the rendered content.</param>
/// <param name="Provenance">The provenance chain explaining the file's inclusion.</param>
public sealed record RenderedFile(
    string Path,
    string Role,
    RenderTier Tier,
    double Score,
    string Content,
    int TokenCount,
    IReadOnlyList<string> Provenance);
