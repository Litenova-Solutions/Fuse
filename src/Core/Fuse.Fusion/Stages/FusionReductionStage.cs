using Fuse.Collection.FileSystem;
using Fuse.Emission;
using Fuse.Fusion.Scoping;
using Fuse.Plugins.Abstractions;
using Fuse.Plugins.Abstractions.Outline;
using Fuse.Reduction;
using Fuse.Reduction.Caching;
using Fuse.Reduction.Models;
using Fuse.Reduction.Security;
using Fuse.Reduction.Tokenization;
using Fuse.Plugins.Abstractions.Options;

namespace Fuse.Fusion.Stages;

/// <summary>
///     The reduction stage of the fusion pipeline: applies per-file reduction and optional post-reduction
///     content transforms (symbol slice, thin skeleton, sketch, downgrade-before-drop).
/// </summary>
public sealed class FusionReductionStage
{
    private const int SketchTokenThreshold = 6000;

    private readonly ContentReductionPipeline _reductionPipeline;
    private readonly ISecretRedactor _secretRedactor;
    private readonly CapabilityRegistry<ISymbolOutlineExtractor> _outlineExtractors;
    private readonly CapabilityRegistry<ISymbolSliceExtractor> _sliceExtractors;
    private readonly CapabilityRegistry<ISymbolChunkExtractor> _chunkExtractors;

    /// <summary>
    ///     Initializes a new instance of the <see cref="FusionReductionStage" /> class.
    /// </summary>
    public FusionReductionStage(
        ContentReductionPipeline reductionPipeline,
        ISecretRedactor secretRedactor,
        CapabilityRegistry<ISymbolOutlineExtractor> outlineExtractors,
        CapabilityRegistry<ISymbolSliceExtractor> sliceExtractors,
        CapabilityRegistry<ISymbolChunkExtractor> chunkExtractors)
    {
        _reductionPipeline = reductionPipeline;
        _secretRedactor = secretRedactor;
        _outlineExtractors = outlineExtractors;
        _sliceExtractors = sliceExtractors;
        _chunkExtractors = chunkExtractors;
    }

    /// <summary>
    ///     Reduces the scoped file set and applies optional post-reduction transforms configured on the request.
    /// </summary>
    /// <param name="request">The fusion request supplying reduction and experimental options.</param>
    /// <param name="files">The scoped source files to reduce.</param>
    /// <param name="filterResult">Scoping metadata driving symbol slice and member packing.</param>
    /// <param name="contentProvider">Run-scoped content provider for source re-reads during transforms.</param>
    /// <param name="parallelism">Maximum degree of parallelism for reduction.</param>
    /// <param name="reductionCache">Optional per-file reduction cache.</param>
    /// <param name="tokenCounter">Token counter for rewritten content.</param>
    /// <param name="perFileLevel">Optional per-file reduction level from the context plan.</param>
    /// <param name="experimental">Resolved experimental options for the run.</param>
    /// <param name="cancellationToken">Token used to cancel reduction and transforms.</param>
    /// <returns>The reduced and optionally transformed content entries.</returns>
    public async Task<IReadOnlyList<FusedContent>> ReduceAsync(
        FusionRequest request,
        IReadOnlyList<Fuse.Collection.Models.SourceFile> files,
        FilteredFileSet filterResult,
        ISourceContentProvider contentProvider,
        int parallelism,
        IReductionCache? reductionCache,
        ITokenCounter tokenCounter,
        Func<Fuse.Collection.Models.SourceFile, ReductionLevel>? perFileLevel,
        ExperimentalOptions experimental,
        CancellationToken cancellationToken = default)
    {
        var reducedContent = await _reductionPipeline.ReduceAsync(
            files,
            request.Reduction,
            contentProvider,
            parallelism,
            reductionCache,
            tokenCounter,
            perFileLevel,
            cancellationToken);

        if (filterResult.Slice is not null)
        {
            reducedContent = await ApplySymbolSliceAsync(
                reducedContent,
                filterResult.Slice,
                request.Reduction.EnableRedaction,
                contentProvider,
                tokenCounter,
                cancellationToken);
        }

        if (filterResult.SelectedMembers is { Count: > 0 })
        {
            reducedContent = await ApplyThinSkeletonAsync(
                reducedContent,
                filterResult.SelectedMembers,
                request.Reduction.EnableRedaction,
                contentProvider,
                tokenCounter,
                cancellationToken);
        }

        if (experimental.SketchHugeFiles)
        {
            reducedContent = await ApplySketchAsync(
                reducedContent,
                request.Reduction.EnableRedaction,
                contentProvider,
                tokenCounter,
                cancellationToken);
        }

        if (experimental.DowngradeBeforeDrop &&
            request.Focus is not null &&
            request.Emission.MaxTokens is { } downgradeBudget)
        {
            reducedContent = await ApplyDowngradeBeforeDropAsync(
                reducedContent,
                downgradeBudget,
                request.Reduction.EnableRedaction,
                contentProvider,
                tokenCounter,
                cancellationToken);
        }

        return reducedContent;
    }

    private async Task<IReadOnlyList<FusedContent>> ApplySymbolSliceAsync(
        IReadOnlyList<FusedContent> reducedContent,
        SymbolSliceRequest slice,
        bool enableRedaction,
        ISourceContentProvider contentProvider,
        ITokenCounter tokenCounter,
        CancellationToken cancellationToken)
    {
        var result = new List<FusedContent>(reducedContent.Count);
        foreach (var entry in reducedContent)
        {
            if (!slice.Paths.Contains(entry.NormalizedPath))
            {
                result.Add(entry);
                continue;
            }

            var extractor = _sliceExtractors.TryResolve(entry.SourceFile.Extension);
            if (extractor is null)
            {
                result.Add(entry);
                continue;
            }

            var source = await contentProvider.GetContentAsync(entry.SourceFile, cancellationToken);
            var sliced = extractor.ExtractSlice(source, slice.Member);
            result.Add(sliced is null ? entry : RewriteRedacted(entry, sliced, enableRedaction, tokenCounter));
        }

        return result;
    }

    private async Task<IReadOnlyList<FusedContent>> ApplyThinSkeletonAsync(
        IReadOnlyList<FusedContent> reducedContent,
        IReadOnlyDictionary<string, IReadOnlySet<string>> selectedMembers,
        bool enableRedaction,
        ISourceContentProvider contentProvider,
        ITokenCounter tokenCounter,
        CancellationToken cancellationToken)
    {
        var result = new List<FusedContent>(reducedContent.Count);
        foreach (var entry in reducedContent)
        {
            if (!selectedMembers.TryGetValue(entry.NormalizedPath, out var members) || members.Count == 0)
            {
                result.Add(entry);
                continue;
            }

            var extractor = _chunkExtractors.TryResolve(entry.SourceFile.Extension);
            if (extractor is null)
            {
                result.Add(entry);
                continue;
            }

            var source = await contentProvider.GetContentAsync(entry.SourceFile, cancellationToken);
            var chunks = extractor.ExtractChunks(source);
            if (chunks.Count == 0)
            {
                result.Add(entry);
                continue;
            }

            var skeleton = ThinSkeletonAssembler.Assemble(source, chunks, members);
            result.Add(RewriteRedacted(entry, skeleton, enableRedaction, tokenCounter));
        }

        return result;
    }

    private async Task<IReadOnlyList<FusedContent>> ApplySketchAsync(
        IReadOnlyList<FusedContent> entries,
        bool enableRedaction,
        ISourceContentProvider contentProvider,
        ITokenCounter tokenCounter,
        CancellationToken cancellationToken)
    {
        var result = new List<FusedContent>(entries.Count);
        foreach (var entry in entries)
        {
            if (entry.TokenCount <= SketchTokenThreshold)
            {
                result.Add(entry);
                continue;
            }

            var extractor = _outlineExtractors.TryResolve(entry.SourceFile.Extension);
            if (extractor is null)
            {
                result.Add(entry);
                continue;
            }

            var source = await contentProvider.GetContentAsync(entry.SourceFile, cancellationToken);
            var outline = extractor.ExtractOutline(source);
            var sketch = FileSketchBuilder.Build(entry.NormalizedPath, outline);
            if (string.IsNullOrEmpty(sketch))
            {
                result.Add(entry);
                continue;
            }

            result.Add(RewriteRedacted(entry, sketch, enableRedaction, tokenCounter));
        }

        return result;
    }

    private async Task<IReadOnlyList<FusedContent>> ApplyDowngradeBeforeDropAsync(
        IReadOnlyList<FusedContent> entries,
        int maxTokens,
        bool enableRedaction,
        ISourceContentProvider contentProvider,
        ITokenCounter tokenCounter,
        CancellationToken cancellationToken)
    {
        var ordered = entries
            .Select((entry, index) => (entry, index))
            .OrderByDescending(x => x.entry.RelevanceScore ?? double.NegativeInfinity)
            .ThenBy(x => x.index)
            .ToList();

        var tail = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var used = 0;
        var budgetReached = false;
        foreach (var (entry, _) in ordered)
        {
            if (entry.IsTrivial)
                continue;

            var cost = entry.TokenCount + EmissionPipeline.MarkerOverheadTokens;
            if (!budgetReached && used + cost <= maxTokens)
            {
                used += cost;
            }
            else
            {
                budgetReached = true;
                tail.Add(entry.NormalizedPath);
            }
        }

        if (tail.Count == 0)
            return entries;

        var result = new List<FusedContent>(entries.Count);
        foreach (var entry in entries)
        {
            if (!tail.Contains(entry.NormalizedPath))
            {
                result.Add(entry);
                continue;
            }

            var extractor = _outlineExtractors.TryResolve(entry.SourceFile.Extension);
            if (extractor is null)
            {
                result.Add(entry);
                continue;
            }

            var source = await contentProvider.GetContentAsync(entry.SourceFile, cancellationToken);
            var sketch = FileSketchBuilder.Build(entry.NormalizedPath, extractor.ExtractOutline(source));
            if (string.IsNullOrEmpty(sketch))
            {
                result.Add(entry);
                continue;
            }

            var sketched = RewriteRedacted(entry, sketch, enableRedaction, tokenCounter);
            result.Add(sketched.TokenCount < entry.TokenCount ? sketched : entry);
        }

        return result;
    }

    private FusedContent RewriteRedacted(
        FusedContent entry,
        string rewritten,
        bool enableRedaction,
        ITokenCounter tokenCounter)
    {
        if (!enableRedaction)
            return entry.WithReducedContent(rewritten, tokenCounter);

        var isCSharp = string.Equals(entry.SourceFile.Extension, ".cs", StringComparison.OrdinalIgnoreCase);
        var redaction = _secretRedactor.Redact(rewritten, classifyCodeLiterals: isCSharp);
        var counts = redaction.CountsByKind.Count > 0 ? redaction.CountsByKind : null;
        return entry.WithReducedContent(redaction.Content, tokenCounter, counts);
    }
}
