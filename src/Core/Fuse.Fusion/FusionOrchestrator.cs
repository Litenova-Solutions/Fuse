using System.Diagnostics;
using Fuse.Fusion.Scoping;
using Fuse.Fusion.PostReduction;
using Fuse.Collection;
using Fuse.Collection.FileSystem;
using Fuse.Emission;
using Fuse.Emission.Manifest;
using Fuse.Emission.Models;
using Fuse.Emission.Tokenization;
using Fuse.Emission.Writers;
using Fuse.Plugins.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Fuse.Plugins.Abstractions.Dependencies;
using Fuse.Plugins.Abstractions.Outline;
using Fuse.Reduction;
using Fuse.Reduction.Caching;
using Fuse.Reduction.Models;
using Fuse.Reduction.Security;
using Fuse.Reduction.Tokenization;

namespace Fuse.Fusion;

/// <summary>
///     Orchestrates the full fusion pipeline: collection, optional scoping, reduction, and emission.
/// </summary>
/// <remarks>
///     This is the top-level entry point for the SDK. It validates the request, then delegates each stage to
///     the collection, reduction, and emission pipelines. Post-reduction enrichment and emission append paths
///     delegate to <see cref="PostReduction.PostReductionEnrichmentPipeline" />. See <see cref="FuseAsync" /> for
///     the stage ordering.
/// </remarks>
public sealed class FusionOrchestrator
{
    private readonly FusionValidator _validator;
    private readonly TokenizerFactory _tokenizerFactory;
    private readonly FileCollectionPipeline _collectionPipeline;
    private readonly ContentReductionPipeline _reductionPipeline;
    private readonly PostReductionEnrichmentPipeline _postReductionPipeline;
    private readonly DependencyGraphBuilder _dependencyGraphBuilder;
    private readonly FocusSeedResolver _focusSeedResolver;
    private readonly IChangeDetector _changeDetector;
    private readonly IFileSystem _fileSystem;
    private readonly Func<ISourceContentProvider> _contentProviderFactory;
    private readonly CapabilityRegistry<IDependencyExtractor> _dependencyExtractors;
    private readonly CapabilityRegistry<ITypeNameLocator> _typeNameLocators;
    private readonly CapabilityRegistry<ISymbolOutlineExtractor> _outlineExtractors;
    private readonly CapabilityRegistry<ISymbolSliceExtractor> _sliceExtractors;
    private readonly CapabilityRegistry<ISymbolChunkExtractor> _chunkExtractors;
    private readonly Func<IRelevanceIndex> _relevanceIndexFactory;
    private readonly IFuseStoreFactory _fuseStoreFactory;
    private readonly ISecretRedactor _secretRedactor;
    private readonly ITokenCostModel _tokenCostModel;
    private readonly IReranker? _reranker;
    private readonly Enrichment.IGitStatsProvider _gitStatsProvider;
    private readonly RelevanceIndexCache _relevanceIndexCache;
    private readonly ILogger<FusionOrchestrator> _logger;

    /// <summary>
    ///     Initializes a new instance of the <see cref="FusionOrchestrator" /> class.
    /// </summary>
    /// <param name="validator">Request validator.</param>
    /// <param name="tokenizerFactory">Tokenizer factory.</param>
    /// <param name="collectionPipeline">Collection stage pipeline.</param>
    /// <param name="reductionPipeline">Reduction stage pipeline.</param>
    /// <param name="postReductionPipeline">Post-reduction enrichment, packing, and emission pipeline.</param>
    /// <param name="dependencyGraphBuilder">Dependency graph builder for scoping.</param>
    /// <param name="focusSeedResolver">Focus seed resolver.</param>
    /// <param name="changeDetector">Git change detector.</param>
    /// <param name="fileSystem">File system abstraction.</param>
    /// <param name="contentProviderFactory">Per-run content provider factory.</param>
    /// <param name="dependencyExtractors">Dependency extractors by extension.</param>
    /// <param name="typeNameLocators">Type-name locators by extension.</param>
    /// <param name="outlineExtractors">Outline extractors by extension.</param>
    /// <param name="sliceExtractors">Symbol slice extractors by extension.</param>
    /// <param name="chunkExtractors">Symbol chunk extractors by extension.</param>
    /// <param name="relevanceIndexFactory">Per-run relevance index factory.</param>
    /// <param name="fuseStoreFactory">Persistent store factory.</param>
    /// <param name="secretRedactor">Redactor re-applied to content rewritten after reduction (thin skeleton, symbol slice, tiered emission), so a post-reduction rewrite cannot reintroduce a secret the reduction-stage redaction already removed.</param>
    /// <param name="tokenCostModel">Per-file token cost estimator used to make query-path dependency expansion budget-aware.</param>
    /// <param name="gitStatsProvider">Provides per-file git churn used by the optional churn ranking prior.</param>
    /// <param name="reranker">Optional dense reranker for the query candidate pool; <see langword="null" /> keeps the lexical BM25F ordering (the no-model floor).</param>
    /// <param name="relevanceIndexCache">Process-lifetime cache reusing a built relevance index across queries on an unchanged tree.</param>
    /// <param name="logger">Optional logger.</param>
    public FusionOrchestrator(
        FusionValidator validator,
        TokenizerFactory tokenizerFactory,
        FileCollectionPipeline collectionPipeline,
        ContentReductionPipeline reductionPipeline,
        PostReductionEnrichmentPipeline postReductionPipeline,
        DependencyGraphBuilder dependencyGraphBuilder,
        FocusSeedResolver focusSeedResolver,
        IChangeDetector changeDetector,
        IFileSystem fileSystem,
        Func<ISourceContentProvider> contentProviderFactory,
        CapabilityRegistry<IDependencyExtractor> dependencyExtractors,
        CapabilityRegistry<ITypeNameLocator> typeNameLocators,
        CapabilityRegistry<ISymbolOutlineExtractor> outlineExtractors,
        CapabilityRegistry<ISymbolSliceExtractor> sliceExtractors,
        CapabilityRegistry<ISymbolChunkExtractor> chunkExtractors,
        Func<IRelevanceIndex> relevanceIndexFactory,
        IFuseStoreFactory fuseStoreFactory,
        ISecretRedactor secretRedactor,
        ITokenCostModel tokenCostModel,
        Enrichment.IGitStatsProvider gitStatsProvider,
        RelevanceIndexCache relevanceIndexCache,
        IReranker? reranker = null,
        ILogger<FusionOrchestrator>? logger = null)
    {
        _validator = validator;
        _tokenizerFactory = tokenizerFactory;
        _collectionPipeline = collectionPipeline;
        _reductionPipeline = reductionPipeline;
        _postReductionPipeline = postReductionPipeline;
        _dependencyGraphBuilder = dependencyGraphBuilder;
        _focusSeedResolver = focusSeedResolver;
        _changeDetector = changeDetector;
        _fileSystem = fileSystem;
        _contentProviderFactory = contentProviderFactory;
        _dependencyExtractors = dependencyExtractors;
        _typeNameLocators = typeNameLocators;
        _outlineExtractors = outlineExtractors;
        _sliceExtractors = sliceExtractors;
        _chunkExtractors = chunkExtractors;
        _relevanceIndexFactory = relevanceIndexFactory;
        _fuseStoreFactory = fuseStoreFactory;
        _secretRedactor = secretRedactor;
        _tokenCostModel = tokenCostModel;
        _reranker = reranker;
        _gitStatsProvider = gitStatsProvider;
        _relevanceIndexCache = relevanceIndexCache;
        _logger = logger ?? NullLogger<FusionOrchestrator>.Instance;
    }

    /// <summary>
    ///     Executes the full fusion pipeline for the specified request and returns the fused result.
    /// </summary>
    /// <param name="request">The fusion request describing collection, scoping, reduction, and emission settings.</param>
    /// <param name="cancellationToken">Token used to cancel collection, reduction, and emission work.</param>
    /// <returns>
    ///     The awaited <see cref="FusionResult" /> describing generated output paths or in-memory content,
    ///     token totals, processed and total file counts, and any pattern summary. When no files match a
    ///     change scope, an empty result is returned rather than throwing.
    /// </returns>
    /// <exception cref="FusionValidationException">
    ///     Thrown by validation when the request is invalid, and at runtime when a focus seed or query matches
    ///     no collected files.
    /// </exception>
    /// <exception cref="FusionException">
    ///     Thrown when an unrecoverable runtime error occurs, such as a failure to detect git changes.
    /// </exception>
    /// <remarks>
    ///     Stages run in a fixed order:
    ///     <list type="number">
    ///         <item><description>Collection: discover and filter candidate files via the collection pipeline.</description></item>
    ///         <item><description>Optional filtering/scoping: narrow the set by focus, git changes, or BM25 query. These three scoping modes are mutually exclusive.</description></item>
    ///         <item><description>Reduction: apply per-file content reduction, optionally cached on disk.</description></item>
    ///         <item><description>Emission: format and write output, then append optional route maps, project graphs, redaction reports, and pattern summaries.</description></item>
    ///     </list>
    ///     The request is validated through <see cref="FusionValidator.ValidateOrThrow" /> before any stage runs.
    ///     Every run constructs its own content cache and relevance index, so concurrent runs against different
    ///     (or the same) directories are fully isolated and scale toward the core count with no process-wide gate.
    /// </remarks>
    public async Task<FusionResult> FuseAsync(FusionRequest request, CancellationToken cancellationToken = default)
    {
        _validator.ValidateOrThrow(request);

        // Run-scoped collaborators: a fresh content cache (read-once per run) and, when a query is present, a
        // fresh BM25 index. Holding no cross-run state is what lets fusion runs execute concurrently.
        var contentProvider = _contentProviderFactory();

        var parallelism = request.Parallelism > 0 ? request.Parallelism : Environment.ProcessorCount;
        var tokenCounter = _tokenizerFactory.GetCounter(request.Emission.TokenizerModel);
        var entryFormatter = EntryFormatterFactory.Create(request.Emission.Format);

        IKeyValueStore? fuseStore = null;
        IReductionCache? reductionCache = null;
        Indexing.IAnalysisIndex? analysisIndex = null;

        if (request.UseReductionCache || request.UsePersistentIndex)
        {
            fuseStore = _fuseStoreFactory.Open(request.Collection.SourceDirectory);

            if (request.UsePersistentIndex)
                analysisIndex = new Indexing.SqliteAnalysisIndex(fuseStore);

            if (request.UseReductionCache)
            {
                reductionCache = new SqliteReductionCache(fuseStore);
                if (request.ClearReductionCache)
                    reductionCache.Clear();
            }
        }

        try
        {
            var stageTimer = Stopwatch.StartNew();
            var collectionResult = await _collectionPipeline.CollectAsync(
                request.Collection,
                parallelism,
                cancellationToken);
            LogStageComplete("collection", stageTimer.ElapsedMilliseconds, collectionResult.Files.Count);

            // Resolve experimental knobs once: the configured request values with environment overrides applied.
            var experimental = ExperimentalOptions.ResolveFromEnvironment(request.Experimental);

            stageTimer.Restart();
            var filterResult = await FilterFilesAsync(
                request, collectionResult.Files, parallelism, analysisIndex, fuseStore, contentProvider, experimental, cancellationToken);
            if (filterResult is null)
            {
                LogStageComplete("scoping", stageTimer.ElapsedMilliseconds, 0, ResolveScopingMode(request));
                return CreateEmptyChangeResult(request);
            }

            LogAnalysisIndexStats(analysisIndex);
            LogStageComplete("scoping", stageTimer.ElapsedMilliseconds, filterResult.Files.Count, ResolveScopingMode(request));

            // Tiered emission (query and focus only): reduce dependency-expanded neighbours (provenance hop two
            // or deeper) to signature skeletons so each costs fewer tokens and the budget-aware packer fits more
            // files. Seeds (chain length one) keep the request's level. Redaction-correct because the skeleton is
            // produced inside the reduction stage, not by a post-reduction source re-read.
            var perFileLevel = BuildTieredLevelResolver(request, experimental, filterResult);

            stageTimer.Restart();
            var reducedContent = await _reductionPipeline.ReduceAsync(
                filterResult.Files,
                request.Reduction,
                contentProvider,
                parallelism,
                reductionCache,
                tokenCounter,
                perFileLevel,
                cancellationToken);
            LogReductionComplete(stageTimer.ElapsedMilliseconds, reducedContent.Count, reductionCache);

            // Symbol-level scoping: slice the focus seed file(s) to the requested member before any other emission
            // step, so token costs and downstream notes reflect the slice.
            if (filterResult.Slice is not null)
            {
                stageTimer.Restart();
                reducedContent = await ApplySymbolSliceAsync(reducedContent, filterResult.Slice, request.Reduction.EnableRedaction, contentProvider, tokenCounter, cancellationToken);
                LogStageComplete("symbol-slice", stageTimer.ElapsedMilliseconds, reducedContent.Count);
            }

            // Symbol-level packing (query path): for each query-matched file, keep the members that ranked in full
            // and collapse the rest of the host type to signatures.
            if (filterResult.SelectedMembers is { Count: > 0 })
            {
                stageTimer.Restart();
                reducedContent = await ApplyThinSkeletonAsync(reducedContent, filterResult.SelectedMembers, request.Reduction.EnableRedaction, contentProvider, tokenCounter, cancellationToken);
                LogStageComplete("symbol-packing", stageTimer.ElapsedMilliseconds, reducedContent.Count);
            }

            // Deterministic sketches (item 16): a file still very large after reduction is replaced with its
            // structural outline (type and member names only), so it keeps presence and navigation instead of
            // consuming the budget that several smaller files need, or being dropped outright. Opt-in, so the
            // default output is unchanged.
            if (experimental.SketchHugeFiles)
            {
                stageTimer.Restart();
                reducedContent = await ApplySketchAsync(reducedContent, request.Reduction.EnableRedaction, contentProvider, tokenCounter, cancellationToken);
                LogStageComplete("sketch", stageTimer.ElapsedMilliseconds, reducedContent.Count);
            }

            // Downgrade-before-drop (P1): under a token budget on query or focus, replace the lower-relevance
            // tail that would otherwise be cut with a compact sketch, so a would-be-dropped file stays present.
            // Recall counts file presence, so this targets multi-file truncation directly.
            if (experimental.DowngradeBeforeDrop &&
                (request.Query is not null || request.Focus is not null) &&
                request.Emission.MaxTokens is { } downgradeBudget)
            {
                stageTimer.Restart();
                reducedContent = await ApplyDowngradeBeforeDropAsync(
                    reducedContent, downgradeBudget, request.Reduction.EnableRedaction, contentProvider, tokenCounter, cancellationToken);
                LogStageComplete("downgrade-before-drop", stageTimer.ElapsedMilliseconds, reducedContent.Count);
            }

            // Table-of-contents mode replaces body emission with a structural map. It runs after scoping and
            // reduction so the listed files and per-file token costs match what a full fetch would cost.
            if (request.Emission.TableOfContents)
            {
                stageTimer.Restart();
                var tocResult = await EmitTableOfContentsAsync(reducedContent, request, contentProvider, tokenCounter, cancellationToken);
                LogStageComplete("table-of-contents", stageTimer.ElapsedMilliseconds, tocResult.ProcessedFileCount);
                return WithReductionCacheStats(tocResult, reductionCache);
            }

            stageTimer.Restart();
            var postReductionContext = new PostReductionContext(
                request,
                reducedContent,
                collectionResult.Files,
                filterResult.Provenance,
                filterResult.Scores,
                filterResult.SelectedMembers,
                filterResult.Preamble,
                tokenCounter,
                entryFormatter,
                experimental);
            var emissionResult = await _postReductionPipeline.ProcessAsync(
                postReductionContext,
                contentProvider,
                cancellationToken);
            LogStageComplete("post-reduction", stageTimer.ElapsedMilliseconds, emissionResult.ProcessedFileCount);

            return WithReductionCacheStats(emissionResult, reductionCache);
        }
        finally
        {
            if (fuseStore is not null)
                await fuseStore.DisposeAsync();
        }
    }

    private static string ResolveScopingMode(FusionRequest request)
    {
        if (request.Focus is not null)
            return "focus";
        if (request.Changes is not null)
            return "changes";
        if (request.Query is not null)
            return "query";
        return "none";
    }

    private void LogStageComplete(string stage, long elapsedMs, int fileCount, string? scopingMode = null)
    {
        if (scopingMode is not null)
        {
            _logger.LogInformation(
                "Fusion stage {Stage} complete: {FileCount} files, mode {ScopingMode}, {ElapsedMs} ms",
                stage,
                fileCount,
                scopingMode,
                elapsedMs);
            return;
        }

        _logger.LogInformation(
            "Fusion stage {Stage} complete: {FileCount} files, {ElapsedMs} ms",
            stage,
            fileCount,
            elapsedMs);
    }

    private void LogAnalysisIndexStats(Indexing.IAnalysisIndex? analysisIndex)
    {
        if (analysisIndex is null)
            return;

        _logger.LogInformation(
            "Analysis index during scoping: hits {IndexHits}, misses {IndexMisses}",
            analysisIndex.Statistics.Hits,
            analysisIndex.Statistics.Misses);
    }

    private void LogReductionComplete(long elapsedMs, int fileCount, IReductionCache? cache)
    {
        if (cache is null)
        {
            LogStageComplete("reduction", elapsedMs, fileCount);
            return;
        }

        _logger.LogInformation(
            "Fusion stage {Stage} complete: {FileCount} files, cache hits {CacheHits}, misses {CacheMisses}, {ElapsedMs} ms",
            "reduction",
            fileCount,
            cache.Statistics.Hits,
            cache.Statistics.Misses,
            elapsedMs);
    }

    // Replaces each seed file's content with a slice that keeps only the requested member in full and reduces
    // the rest of the file to signatures. Falls back to the original entry when no slice extractor handles the
    // extension or the member is not found.
    //
    // The slice is assembled from raw source (see ExtractSlice), AFTER ContentReductionPipeline.ReduceAsync has
    // already run redaction, so a kept member body could carry a secret the normal path would have removed.
    // Redaction is therefore re-run here (C1 invariant): any content rewritten from source after reduction must
    // pass the redactor before WithReducedContent.
    private async Task<IReadOnlyList<FusedContent>> ApplySymbolSliceAsync(
        IReadOnlyList<FusedContent> reducedContent,
        SymbolSliceRequest slice,
        bool enableRedaction,
        ISourceContentProvider contentProvider,
        Fuse.Reduction.Tokenization.ITokenCounter tokenCounter,
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

    // Rebuilds each query-matched file as a thin host skeleton: the members that ranked are kept verbatim and
    // the rest of the host type collapses to signatures. Files with no selected members, no chunk extractor,
    // or no chunks pass through unchanged, so dependency-expanded files keep their full reduced content.
    //
    // The skeleton is built from the original source, not the reduced content, so its member boundaries match
    // the chunks that were indexed and ranked: reduction can merge a member's closing brace onto the next
    // declaration's line, which would defeat the line-based chunker. This mirrors how the focus-seed symbol
    // slice (ApplySymbolSliceAsync) operates on source.
    private async Task<IReadOnlyList<FusedContent>> ApplyThinSkeletonAsync(
        IReadOnlyList<FusedContent> reducedContent,
        IReadOnlyDictionary<string, IReadOnlySet<string>> selectedMembers,
        bool enableRedaction,
        ISourceContentProvider contentProvider,
        Fuse.Reduction.Tokenization.ITokenCounter tokenCounter,
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

            // Assembled from raw source after redaction already ran in ReduceAsync, so re-run redaction on the
            // kept member bodies before emission (C1 invariant).
            var skeleton = ThinSkeletonAssembler.Assemble(source, chunks, members);
            result.Add(RewriteRedacted(entry, skeleton, enableRedaction, tokenCounter));
        }

        return result;
    }

    // A reduced entry larger than this many tokens is a candidate for the sketch fallback (item 16). A file
    // this large, even reduced, would consume a large share of a typical budget; its outline gives presence and
    // navigation at a fraction of the cost.
    private const int SketchTokenThreshold = 6000;

    // Replaces an over-large reduced entry with its structural outline (item 16). Files under the threshold,
    // and files with no outline extractor or no declared types, are left untouched. The sketch is assembled
    // from raw source after the reduction-stage redaction ran, so it is routed back through the redactor (C1).
    private async Task<IReadOnlyList<FusedContent>> ApplySketchAsync(
        IReadOnlyList<FusedContent> entries,
        bool enableRedaction,
        ISourceContentProvider contentProvider,
        Fuse.Reduction.Tokenization.ITokenCounter tokenCounter,
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

    // Downgrade-before-drop (P1). Orders entries by relevance, keeps the head full until the cumulative cost
    // reaches the budget, and replaces the lower-relevance tail (the entries the packer would otherwise drop)
    // with a compact sketch, so a would-be-dropped file stays present as a navigable outline. The sketch is a
    // post-reduction rewrite routed through the redactor (C1). Entries with no outline are left for the packer
    // to drop as before.
    private async Task<IReadOnlyList<FusedContent>> ApplyDowngradeBeforeDropAsync(
        IReadOnlyList<FusedContent> entries,
        int maxTokens,
        bool enableRedaction,
        ISourceContentProvider contentProvider,
        Fuse.Reduction.Tokenization.ITokenCounter tokenCounter,
        CancellationToken cancellationToken)
    {
        var ordered = entries
            .Select((entry, index) => (entry, index))
            .OrderByDescending(x => x.entry.RelevanceScore ?? double.NegativeInfinity)
            .ThenBy(x => x.index)
            .ToList();

        // Mark the relevance tail that does not fit at full size: keep admitting the head until the budget is
        // reached, then everything after is a downgrade candidate. Trivial entries are not charged or sketched.
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

            // Only downgrade when the sketch is actually smaller, so a tiny tail file is never inflated.
            var sketched = RewriteRedacted(entry, sketch, enableRedaction, tokenCounter);
            result.Add(sketched.TokenCount < entry.TokenCount ? sketched : entry);
        }

        return result;
    }

    // Re-runs secret redaction on content that was rebuilt from raw source after the reduction stage (thin
    // skeleton, symbol slice). Redaction normally runs inside ContentReductionPipeline.ReduceAsync, so any
    // content assembled from source afterward bypasses it and could reintroduce a secret a kept member body
    // carries. This restores the invariant that nothing emitted skips the redactor. When redaction is disabled
    // the content is rewritten verbatim with the original counts preserved.
    private FusedContent RewriteRedacted(
        FusedContent entry,
        string rewritten,
        bool enableRedaction,
        Fuse.Reduction.Tokenization.ITokenCounter tokenCounter)
    {
        if (!enableRedaction)
            return entry.WithReducedContent(rewritten, tokenCounter);

        var isCSharp = string.Equals(entry.SourceFile.Extension, ".cs", StringComparison.OrdinalIgnoreCase);
        var redaction = _secretRedactor.Redact(rewritten, classifyCodeLiterals: isCSharp);
        var counts = redaction.CountsByKind.Count > 0 ? redaction.CountsByKind : null;
        return entry.WithReducedContent(redaction.Content, tokenCounter, counts);
    }

    // Builds the per-file reduction-level selector for tiered emission, or null when it is off, the run is not
    // query/focus, or no provenance is available. Neighbours (provenance chain length greater than one) reduce
    // to a signature skeleton; seeds keep the request's level. Changes mode is intentionally excluded: its
    // recall rests on emitting the changed files in full.
    private static Func<Fuse.Collection.Models.SourceFile, Fuse.Plugins.Abstractions.Options.ReductionLevel>? BuildTieredLevelResolver(
        FusionRequest request,
        ExperimentalOptions experimental,
        FilteredFileSet filterResult)
    {
        if (!experimental.TieredEmission)
            return null;
        if (request.Focus is null && request.Query is null)
            return null;
        if (filterResult.Provenance is not { } provenance)
            return null;

        var seedLevel = request.Reduction.Level;
        return file =>
            provenance.TryGetValue(file.NormalizedRelativePath, out var chain) && chain.Count > 1
                ? Fuse.Plugins.Abstractions.Options.ReductionLevel.Skeleton
                : seedLevel;
    }

    private static FusionResult WithReductionCacheStats(FusionResult result, IReductionCache? cache)
    {
        if (cache is null)
            return result;

        return new FusionResult(
            result.GeneratedPaths,
            result.InMemoryContent,
            result.TotalTokens,
            result.ProcessedFileCount,
            result.TotalFileCount,
            result.Duration,
            result.TopTokenFiles,
            result.PatternSummary,
            cache.Statistics.Hits,
            cache.Statistics.Misses,
            result.EmittedFileTokens);
    }

    private async Task<FilteredFileSet?> FilterFilesAsync(
        FusionRequest request,
        IReadOnlyList<Fuse.Collection.Models.SourceFile> files,
        int parallelism,
        Indexing.IAnalysisIndex? index,
        IKeyValueStore? fuseStore,
        ISourceContentProvider contentProvider,
        ExperimentalOptions experimental,
        CancellationToken cancellationToken)
    {
        if (request.Focus is not null)
            return await FilterByFocusAsync(request, files, parallelism, index, contentProvider, experimental, cancellationToken);

        if (request.Changes is not null)
            return await FilterByChangesAsync(request, files, parallelism, index, contentProvider, cancellationToken);

        if (request.Query is not null)
            return await FilterByQueryAsync(request, files, parallelism, index, fuseStore, contentProvider, experimental, cancellationToken);

        return new FilteredFileSet(files, null, null);
    }

    private async Task<FilteredFileSet> FilterByFocusAsync(
        FusionRequest request,
        IReadOnlyList<Fuse.Collection.Models.SourceFile> files,
        int parallelism,
        Indexing.IAnalysisIndex? index,
        ISourceContentProvider contentProvider,
        ExperimentalOptions experimental,
        CancellationToken cancellationToken)
    {
        var graph = await BuildGraphAsync(files, parallelism, index, contentProvider, cancellationToken);

        var seed = request.Focus!.Seed;
        var seedPaths = await _focusSeedResolver.ResolveSeedPathsAsync(
            seed, files, contentProvider, cancellationToken);

        // Symbol-level scoping: when the seed is "Type.Member" and the type alone resolves, scope to that
        // member. Only attempted when a slice extractor is registered (the opt-in precision tier), so the regex
        // default keeps the whole-file behavior.
        string? sliceMember = null;
        if (seedPaths.Count == 0 && _sliceExtractors.TryResolve(".cs") is not null)
        {
            var dot = seed.LastIndexOf('.');
            if (dot > 0 && dot < seed.Length - 1)
            {
                var typeSeed = seed[..dot];
                var member = seed[(dot + 1)..];
                seedPaths = await _focusSeedResolver.ResolveSeedPathsAsync(
                    typeSeed, files, contentProvider, cancellationToken);
                if (seedPaths.Count > 0)
                    sliceMember = member;
            }
        }

        if (seedPaths.Count == 0)
        {
            throw new FusionValidationException(
                $"Focus seed '{request.Focus.Seed}' matched no collected files.");
        }

        var sliceRequest = sliceMember is null
            ? null
            : new SymbolSliceRequest(seedPaths.ToHashSet(StringComparer.OrdinalIgnoreCase), sliceMember);

        var seedScores = seedPaths.ToDictionary(p => p, _ => 1.0, StringComparer.OrdinalIgnoreCase);
        var (focusProximityEdges, focusProximityWeight) =
            ResolveProximity(experimental, files, request.Collection.SourceDirectory);
        // No byte-budget gate on expansion: the reduction-aware packer applies the budget after reduction on
        // the real reduced token cost (see ReductionAwarePacker), so expansion spreads by depth and relevance.
        var options = new ExpansionOptions(
            request.Focus.Depth,
            FollowReferences: true,
            FollowDependents: true,
            Centrality: GraphCentrality.Compute(graph),
            CentralityWeight: experimental.CentralityWeight,
            ProximityEdges: focusProximityEdges,
            ProximityWeight: focusProximityWeight);

        var expansion = _focusSeedResolver.Expand(graph, seedScores, options);
        var filtered = files.Where(f => expansion.IncludedPaths.Contains(f.NormalizedRelativePath)).ToArray();
        return new FilteredFileSet(filtered, expansion.ProvenanceChains, expansion.Scores, Slice: sliceRequest);
    }

    private async Task<FilteredFileSet> FilterByQueryAsync(
        FusionRequest request,
        IReadOnlyList<Fuse.Collection.Models.SourceFile> files,
        int parallelism,
        Indexing.IAnalysisIndex? index,
        IKeyValueStore? fuseStore,
        ISourceContentProvider contentProvider,
        ExperimentalOptions experimental,
        CancellationToken cancellationToken)
    {
        // File selection ranks at FILE granularity (whole-file BM25F), which preserves recall: a query term
        // anywhere in a file contributes with proper length normalization, so files whose match is spread
        // across members are not penalized. Member-level granularity is applied only to emission (the thin
        // skeleton below), where it improves precision without changing which files are included.
        var documents = new Dictionary<string, IndexedDocument>(StringComparer.OrdinalIgnoreCase);
        var fileContents = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        // Accumulate a content signature over (path, content) of every indexed file. The index is a pure
        // function of these (symbols are derived from content deterministically), so the signature keys the
        // process-lifetime index cache: a warm query on an unchanged tree reuses the built index. Files are
        // collected in a stable order, so the order-dependent mix is deterministic.
        var indexSignature = 0UL;
        foreach (var file in files)
        {
            var content = await contentProvider.GetContentAsync(file, cancellationToken);
            var locator = _typeNameLocators.TryResolve(file.Extension);
            var extractor = _dependencyExtractors.TryResolve(file.Extension);

            // Reuse the persistent index for the symbol field so the graph build later in this run also hits.
            var symbols = extractor is not null
                ? DependencyGraphBuilder.Analyze(content, extractor, locator, index).DeclaredSymbols
                : locator?.ExtractDefinedSymbols(content);

            documents[file.NormalizedRelativePath] = new IndexedDocument(content, file.NormalizedRelativePath, symbols);
            fileContents[file.NormalizedRelativePath] = content;
            indexSignature = MixSignature(indexSignature, file.NormalizedRelativePath, content);
        }

        // Reuse a built index across queries on an unchanged tree (item 24): the index rebuilds its
        // document-frequency and length statistics otherwise, which is the dominant warm-call cost once body
        // tokenization is cached. A built index is read-only, so sharing the cached instance across concurrent
        // queries is safe. On a miss, build a fresh per-run index (no cross-run state) and index it; when the
        // persistent index is enabled, body tokenization is cached on disk by content hash as well.
        var relevanceIndex = _relevanceIndexCache.GetOrBuild(indexSignature, () =>
        {
            var freshIndex = _relevanceIndexFactory();
            var postingsStore = request.UsePersistentIndex && fuseStore is not null
                ? new Indexing.SqliteRelevancePostingsStore(fuseStore)
                : null;
            freshIndex.Index(documents, postingsStore);
            return freshIndex;
        });
        // Rank a candidate pool (A4): wider than the seed set so a reranking stage has room to reorder. The
        // lexical default keeps the pool equal to the seed count, so behavior is unchanged.
        var candidateTopK = request.Query!.ResolvedCandidateTopK;
        var seedTopK = request.Query.ResolvedSeedTopK;
        // A candidate-pool prior (dense rerank or the git churn prior) only changes the seed set when the pool
        // is wider than the seed count, since it chooses which candidates become seeds. Widen the pool to
        // several times the seed count when one is active, so the prior can promote a file the lexical pass
        // ranked just outside the seeds.
        var poolPriorActive = (experimental.DenseRerank && _reranker is { IsAvailable: true })
                              || experimental.GitChurnWeight > 0;
        if (poolPriorActive)
            candidateTopK = Math.Max(candidateTopK, seedTopK * CandidatePoolWideningFactor);
        var ranked = relevanceIndex.RankScored(request.Query.Query, candidateTopK);

        if (ranked.Count == 0)
        {
            throw new FusionValidationException(
                $"Query '{request.Query.Query}' matched no collected files.");
        }

        if (experimental.MultiQueryFusion)
        {
            // Multi-query fusion: rank a few diverse query variants and combine with Reciprocal Rank Fusion, so
            // a file several variants agree on outranks one variant's lone top hit. Variants: the raw query, an
            // identifier-only subset (the compound type-like tokens, which carry the strongest code signal), and
            // the pseudo-relevance-expanded query. RRF needs no score calibration across the variants.
            var variants = new List<IReadOnlyList<RankedFile>> { ranked };

            var identifierTerms = ExtractIdentifierTerms(request.Query.Query);
            if (identifierTerms.Count > 0)
            {
                var identifierRanked = relevanceIndex.RankScored(identifierTerms, request.Query.TopFiles);
                if (identifierRanked.Count > 0)
                    variants.Add(identifierRanked);
            }

            if (experimental.QueryExpansion)
            {
                var expandedQuery = PseudoRelevanceExpander.Expand(
                    request.Query.Query, ranked, documents, new QueryExpansionOptions(), relevanceIndex.InverseDocumentFrequency);
                var prfRanked = relevanceIndex.RankScored(expandedQuery, request.Query.TopFiles);
                if (prfRanked.Count > 0)
                    variants.Add(prfRanked);
            }

            var fused = RankFusion.Fuse(variants, request.Query.TopFiles);
            if (fused.Count > 0)
                ranked = fused;
        }
        else if (experimental.QueryExpansion)
        {
            // Pseudo-relevance feedback: rewrite a sparse query in the codebase's own vocabulary by blending in
            // recurring declared-symbol terms from the first pass's top files, then re-rank. Conservative by
            // construction (symbol field only, multi-doc terms only, reduced weight); a no-op when disabled or
            // when no term qualifies, so the seed set then equals the single-pass ordering.
            var expandedQuery = PseudoRelevanceExpander.Expand(
                request.Query.Query, ranked, documents, new QueryExpansionOptions(), relevanceIndex.InverseDocumentFrequency);
            var reranked = relevanceIndex.RankScored(expandedQuery, request.Query.TopFiles);
            // Merge rather than replace: expansion adds the files it surfaces but never drops a first-pass
            // seed, so a misfiring expansion cannot lower recall below the single-pass result.
            if (reranked.Count > 0)
                ranked = PseudoRelevanceExpander.MergePreservingSeeds(ranked, reranked);
        }

        // Distributional thesaurus (Q4): expand the query with corpus identifiers that co-occur with its terms
        // (PMI over declared symbols), then re-rank and merge preserving seeds. This bridges to a related
        // vocabulary the pseudo-relevance feedback set never contained, fully lexically. A no-op when no
        // association clears the gates, so it cannot lower recall below the pre-expansion result.
        if (experimental.DistributionalThesaurus)
        {
            var queryTerms = RelevanceTokenizer.Tokenize(request.Query.Query);
            if (queryTerms.Count > 0)
            {
                var documentSymbolTerms = documents.Values
                    .Select(d => (IReadOnlySet<string>)new HashSet<string>(
                        TokenizeSymbolsForThesaurus(d.Symbols), StringComparer.Ordinal))
                    .ToList();

                var associates = DistributionalThesaurus.Expand(queryTerms, documentSymbolTerms);
                if (associates.Count > 0)
                {
                    var expanded = new Dictionary<string, double>(StringComparer.Ordinal);
                    foreach (var term in queryTerms)
                        expanded[term] = 1.0;
                    foreach (var (term, weight) in associates)
                        expanded.TryAdd(term, weight);

                    var thesaurusRanked = relevanceIndex.RankScored(expanded, request.Query.TopFiles);
                    if (thesaurusRanked.Count > 0)
                        ranked = PseudoRelevanceExpander.MergePreservingSeeds(ranked, thesaurusRanked);
                }
            }
        }

        // Member-level retrieval (Q5): index each declared member as its own document, roll the per-member
        // scores up to a file score (best member wins), and add any file the member pass surfaces that the
        // file-granular pass missed, as an extra seed. This reaches a file whose match is concentrated in one
        // member of an otherwise large file, which whole-file length normalization dilutes. Member-rollup
        // scores come from a separate index on a different scale, so the additions are placed below the
        // file-granular floor rather than interleaved by raw score, and the seed count is widened to admit them,
        // so the pass only adds files and never drops a first-pass seed.
        if (experimental.MemberLevelRetrieval)
        {
            // Rank a wider member pool than the seed count, so a file the member pass surfaces is captured even
            // when higher-density members of already-seeded files rank above it; then admit at most TopFiles of
            // the files not already present, so the extra seeds stay bounded.
            var memberRanked = RankByMembers(request.Query.Query, files, fileContents, request.Query.TopFiles * 4);
            var present = new HashSet<string>(ranked.Select(r => r.Path), StringComparer.OrdinalIgnoreCase);
            var additions = memberRanked
                .Where(r => !present.Contains(r.Path))
                .Take(request.Query.TopFiles)
                .ToList();
            if (additions.Count > 0)
            {
                var floor = ranked.Count > 0 ? ranked.Min(r => r.Score) : 1.0;
                var combined = ranked.ToList();
                foreach (var addition in additions)
                {
                    floor *= 0.999; // strictly below the file-granular floor, preserving member order
                    combined.Add(addition with { Score = floor });
                }

                ranked = combined;
                seedTopK += additions.Count; // admit the surfaced files as extra seeds
            }
        }

        // Git churn prior (Q6): nudge a recently and frequently changed candidate up, since work clusters where
        // code recently changed. A production-routing lever, off unless GitChurnWeight > 0. The pinned benchmark
        // cannot validate it: its worktrees are historical PR-head checkouts, so churn-from-now is uniformly
        // empty (a no-op), and a commit-date-relative churn would leak (the changed files are the most recently
        // changed by construction). It therefore stays off by default and is not a benchmark lever.
        if (experimental.GitChurnWeight > 0)
        {
            ranked = await ApplyGitChurnPriorAsync(
                ranked, request.Collection.SourceDirectory, experimental.GitChurnWeight, cancellationToken);
        }

        // Dense rerank (item 9): reorder the candidate pool by blending the lexical score with a model's
        // query-to-document similarity, so a semantically matching file is promoted to a seed even when it
        // shares fewer query words. Optional and gated: when no reranker is registered (no model, offline, or
        // the assembly is absent) or the flag is off, the pool keeps its lexical order, which is the guaranteed
        // no-model floor. The text embedded per candidate is its declared symbols, path, and a content sketch.
        if (experimental.DenseRerank && _reranker is { IsAvailable: true } reranker && ranked.Count > 1)
        {
            var rerankText = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var candidate in ranked)
            {
                if (documents.TryGetValue(candidate.Path, out var doc))
                    rerankText[candidate.Path] = BuildRerankText(doc);
            }

            ranked = reranker.Rerank(request.Query.Query, ranked, rerankText);
        }

        // Promote the top seedTopK of the (possibly reranked) candidate pool to expansion seeds. With the
        // lexical default the pool and seed count are equal, so every candidate is a seed as before.
        var seedRanked = ranked.Count > seedTopK ? ranked.Take(seedTopK).ToList() : ranked;
        var seedScores = seedRanked.ToDictionary(r => r.Path, r => r.Score, StringComparer.OrdinalIgnoreCase);

        // Symbol-level packing (precision only): pick, per matched file, the members the query is about so
        // emission can keep them in full and collapse the rest to signatures. This never changes file
        // selection, so recall is identical to the file-granular path.
        var selectedMembers = SelectQueryMembers(ranked, fileContents, request.Query.Query);
        var graph = await BuildGraphAsync(files, parallelism, index, contentProvider, cancellationToken);

        // Budget-aware expansion (item 4): when a token ceiling is set, gate neighbour admission by an
        // estimated reduced cost so the graph stops admitting once the budget is spent, instead of admitting
        // the whole neighbourhood and leaving the packer to cut it (which wastes reduction on files that never
        // emit, and can lose a truth file in the knapsack). Costs are estimated at the level each file will be
        // emitted at: a seed at the request level, a neighbour at the skeleton level when tiered emission is on
        // (matching BuildTieredLevelResolver), so a cheap skeletonized neighbour is not rejected as a full body.
        int? expansionBudget = null;
        IReadOnlyDictionary<string, int>? expansionCosts = null;
        if (experimental.BudgetAwareExpansion && request.Emission.MaxTokens is { } maxTokens && maxTokens > 0)
        {
            expansionBudget = maxTokens;
            var seedLevel = request.Reduction.Level;
            var neighbourLevel = experimental.TieredEmission
                ? Fuse.Plugins.Abstractions.Options.ReductionLevel.Skeleton
                : seedLevel;
            var costs = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in files)
            {
                if (!fileContents.TryGetValue(file.NormalizedRelativePath, out var content))
                    continue;

                var level = seedScores.ContainsKey(file.NormalizedRelativePath) ? seedLevel : neighbourLevel;
                costs[file.NormalizedRelativePath] = _tokenCostModel.EstimateReducedTokens(content, file.Extension, level);
            }

            expansionCosts = costs;
        }

        // Query seeds are already content-matched; expand forward to their dependencies for context, but do
        // not follow dependents, which would broaden the set with files that merely use a matched type. A
        // measured A/B over the pinned corpus confirmed this: enabling reverse hops dropped query recall 51 to
        // 45 percent at the headline budget (Newtonsoft.Json 30 to 13), as common-type dependents displaced
        // the real targets under the token budget.
        var (queryProximityEdges, queryProximityWeight) =
            ResolveProximity(experimental, files, request.Collection.SourceDirectory);
        var options = new ExpansionOptions(
            request.Query.Depth,
            FollowReferences: true,
            FollowDependents: false,
            TokenBudget: expansionBudget,
            TokenCosts: expansionCosts,
            Centrality: GraphCentrality.Compute(graph),
            CentralityWeight: experimental.CentralityWeight,
            ProximityEdges: queryProximityEdges,
            ProximityWeight: queryProximityWeight);

        var expansion = _focusSeedResolver.Expand(graph, seedScores, options);
        var filtered = files.Where(f => expansion.IncludedPaths.Contains(f.NormalizedRelativePath)).ToArray();
        return new FilteredFileSet(
            filtered, expansion.ProvenanceChains, expansion.Scores, SelectedMembers: selectedMembers);
    }

    // Compound PascalCase identifiers (two or more humps, for example OrderService, TokenBucketMiddleware): the
    // strongest code signal in a query. The multi-query-fusion variant ranks on just these so a file that
    // declares the named type is weighed independently of the surrounding prose words.
    private static readonly System.Text.RegularExpressions.Regex IdentifierTokenRegex =
        new(@"\b[A-Z][a-z0-9]+(?:[A-Z][a-z0-9]*)+\b", System.Text.RegularExpressions.RegexOptions.Compiled);

    // Characters of file content included in the dense-rerank text. The declared symbols and path carry most of
    // the signal; a short body sketch adds context. Capped so a large file does not dominate, and the model
    // truncates to its own window regardless.
    private const int RerankSketchChars = 2000;

    // When a candidate-pool prior is active (dense rerank or the git churn prior), the BM25 candidate pool is
    // widened to this multiple of the seed count so the prior selects the seeds from a pool several times
    // larger than it will keep.
    private const int CandidatePoolWideningFactor = 4;

    // Git churn prior (Q6). Multiplies each candidate's score by (1 + weight * normalized recent commit count),
    // so a frequently and recently changed file ranks slightly higher. Normalized by the pool's maximum churn
    // and held to a conservative weight, so it tilts ties rather than overruling a strong lexical match. A
    // no-op when git is unavailable or no candidate has recent churn (for example a historical checkout, which
    // is why the pinned benchmark cannot measure this).
    private async Task<IReadOnlyList<RankedFile>> ApplyGitChurnPriorAsync(
        IReadOnlyList<RankedFile> ranked,
        string sourceDirectory,
        double weight,
        CancellationToken cancellationToken)
    {
        var paths = ranked.Select(r => r.Path).ToList();
        var stats = await _gitStatsProvider.GetStatsAsync(sourceDirectory, paths, cancellationToken);
        if (!stats.IsAvailable || stats.StatsByPath.Count == 0)
            return ranked;

        var maxChurn = stats.StatsByPath.Values.Max(s => s.CommitCount);
        if (maxChurn <= 0)
            return ranked;

        var boosted = ranked.Select(r =>
        {
            var churn = stats.StatsByPath.TryGetValue(r.Path, out var s) ? s.CommitCount : 0;
            var churnNorm = (double)churn / maxChurn;
            return r with { Score = r.Score * (1 + weight * churnNorm) };
        });

        return boosted
            .OrderByDescending(r => r.Score)
            .ThenBy(r => r.Path, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    // The factor applied to a structural proximity neighbour's score, so it enters below a real type reference.
    private const double ProximityExpansionWeight = 0.5;

    // Builds the expansion adjacency from the optional structural-proximity (item 7) and coarse project-graph
    // (item 8) experiments. Both feed the resolver's proximity-edge channel at the same decayed weight; when
    // both are on their neighbour lists are merged per file. Returns no edges and a zero weight when neither is
    // on, which disables proximity expansion in the resolver.
    private static (IReadOnlyDictionary<string, IReadOnlyList<string>>? Edges, double Weight) ResolveProximity(
        ExperimentalOptions experimental,
        IReadOnlyList<Fuse.Collection.Models.SourceFile> files,
        string sourceRoot)
    {
        if (!experimental.ProximityEdges && !experimental.ProjectGraph)
            return (null, 0.0);

        var paths = files.Select(f => f.NormalizedRelativePath).ToList();
        IReadOnlyDictionary<string, IReadOnlyList<string>>? proximity =
            experimental.ProximityEdges ? ProximityEdgeBuilder.Build(paths) : null;
        IReadOnlyDictionary<string, IReadOnlyList<string>>? project =
            experimental.ProjectGraph ? ProjectGraphEdgeBuilder.Build(sourceRoot, paths) : null;

        if (project is null || project.Count == 0)
            return (proximity, ProximityExpansionWeight);
        if (proximity is null || proximity.Count == 0)
            return (project, ProximityExpansionWeight);

        // Both on: union the neighbour lists per file, de-duplicated and ordered for determinism.
        var merged = new Dictionary<string, IReadOnlyList<string>>(proximity, StringComparer.OrdinalIgnoreCase);
        foreach (var (path, projectNeighbours) in project)
        {
            if (merged.TryGetValue(path, out var existing))
            {
                var union = new SortedSet<string>(existing, StringComparer.OrdinalIgnoreCase);
                union.UnionWith(projectNeighbours);
                merged[path] = union.ToList();
            }
            else
            {
                merged[path] = projectNeighbours;
            }
        }

        return (merged, ProximityExpansionWeight);
    }

    // Member-level retrieval (Q5). Indexes each declared member of each file as its own document, ranks the
    // query over members, and rolls the per-member scores up to a file score (the file's best member). Returns
    // the top files by member score, to be merged with the file-granular ranking. A fresh per-call index over
    // member chunks; empty when no file has a chunk extractor or no member matches.
    private IReadOnlyList<RankedFile> RankByMembers(
        string query,
        IReadOnlyList<Fuse.Collection.Models.SourceFile> files,
        IReadOnlyDictionary<string, string> fileContents,
        int topFiles)
    {
        var chunkDocuments = new Dictionary<string, IndexedDocument>(StringComparer.Ordinal);
        var chunkToFile = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var file in files)
        {
            var extractor = _chunkExtractors.TryResolve(file.Extension);
            if (extractor is null || !fileContents.TryGetValue(file.NormalizedRelativePath, out var content))
                continue;

            var chunks = extractor.ExtractChunks(content);
            for (var i = 0; i < chunks.Count; i++)
            {
                var key = $"{file.NormalizedRelativePath}{i}";
                chunkDocuments[key] = new IndexedDocument(chunks[i].Content, key, [chunks[i].SymbolName]);
                chunkToFile[key] = file.NormalizedRelativePath;
            }
        }

        if (chunkDocuments.Count == 0)
            return [];

        var chunkIndex = _relevanceIndexFactory();
        chunkIndex.Index(chunkDocuments);
        var chunkRanked = chunkIndex.RankScored(query, chunkDocuments.Count);

        // Roll up: each file takes its best-scoring member.
        var fileScores = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var chunk in chunkRanked)
        {
            var path = chunkToFile[chunk.Path];
            if (!fileScores.TryGetValue(path, out var best) || chunk.Score > best)
                fileScores[path] = chunk.Score;
        }

        return fileScores
            .OrderByDescending(kv => kv.Value)
            .ThenBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
            .Take(topFiles)
            .Select(kv => new RankedFile(kv.Key, kv.Value))
            .ToList();
    }

    // Folds one file's path and content into the running index signature with an FNV-style mix over their
    // 64-bit hashes. Order-dependent, but files are collected in a stable order, so the signature is
    // deterministic for a given tree and changes whenever any file's path or content changes.
    private static ulong MixSignature(ulong accumulator, string path, string content)
    {
        const ulong prime = 1099511628211UL;
        var pathHash = System.IO.Hashing.XxHash64.HashToUInt64(System.Text.Encoding.UTF8.GetBytes(path));
        var contentHash = System.IO.Hashing.XxHash64.HashToUInt64(System.Text.Encoding.UTF8.GetBytes(content));
        accumulator = (accumulator ^ pathHash) * prime;
        accumulator = (accumulator ^ contentHash) * prime;
        return accumulator;
    }

    // Tokenizes a file's declared symbols into the term set the distributional thesaurus co-occurs over, using
    // the same tokenizer as the index so the terms match the query terms.
    private static IEnumerable<string> TokenizeSymbolsForThesaurus(IReadOnlyList<string>? symbols)
    {
        if (symbols is null)
            yield break;

        foreach (var symbol in symbols)
            foreach (var term in RelevanceTokenizer.Tokenize(symbol))
                yield return term;
    }

    // Builds the text a candidate file is embedded as for dense reranking: its declared symbols first (the
    // strongest concept signal), then its path, then a short content sketch.
    private static string BuildRerankText(IndexedDocument document)
    {
        var symbols = document.Symbols is { Count: > 0 } declared
            ? string.Join(' ', declared)
            : string.Empty;
        var content = document.Content;
        var sketch = content.Length > RerankSketchChars ? content[..RerankSketchChars] : content;
        return $"{symbols}\n{document.Path ?? string.Empty}\n{sketch}";
    }

    // Extracts the compound-identifier tokens from a query as a weighted-term set for an identifier-only ranking
    // variant. Empty when the query names no compound identifier, in which case the variant is skipped.
    private static Dictionary<string, double> ExtractIdentifierTerms(string query)
    {
        var terms = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (System.Text.RegularExpressions.Match match in IdentifierTokenRegex.Matches(query))
            foreach (var term in RelevanceTokenizer.Tokenize(match.Value))
                terms[term] = 1.0;

        return terms;
    }

    // A member is kept verbatim only when its query-overlap score is at least this fraction of the file's best
    // member. The floor separates the members the query is genuinely about from siblings that merely share the
    // type, which collapse to signatures.
    private const double MemberSelectionRatio = 0.4;

    // A query-term hit in a member's name counts for more than one in its body, mirroring the symbol-field
    // boost the file index uses.
    private const double MemberSymbolWeight = 4.0;

    // For each query-matched file, scores its members by query-term overlap and returns the qualified names of
    // the members the query is about (those scoring near the file's best). Files with no chunk extractor, no
    // chunks, or no matching member are omitted, so they keep their full reduced content; only files with a
    // clear member match are trimmed to a thin skeleton. This is emission-only and never affects file
    // selection, so query recall is identical to the file-granular path.
    private Dictionary<string, IReadOnlySet<string>> SelectQueryMembers(
        IReadOnlyList<RankedFile> ranked,
        IReadOnlyDictionary<string, string> fileContents,
        string query)
    {
        var queryTerms = RelevanceTokenizer.Tokenize(query)
            .ToHashSet(StringComparer.Ordinal);
        var selected = new Dictionary<string, IReadOnlySet<string>>(StringComparer.OrdinalIgnoreCase);
        if (queryTerms.Count == 0)
            return selected;

        foreach (var candidate in ranked)
        {
            if (!fileContents.TryGetValue(candidate.Path, out var content))
                continue;

            var extractor = _chunkExtractors.TryResolve(Path.GetExtension(candidate.Path));
            var chunks = extractor?.ExtractChunks(content);
            if (chunks is null || chunks.Count == 0)
                continue;

            var best = 0.0;
            var scores = new List<(string Qualified, double Score)>(chunks.Count);
            foreach (var chunk in chunks)
            {
                var score = MemberQueryScore(chunk, queryTerms);
                if (score > 0)
                {
                    scores.Add((chunk.Identity, score));
                    if (score > best)
                        best = score;
                }
            }

            if (best <= 0)
                continue; // No member matched the query: keep the whole (reduced) file.

            var floor = best * MemberSelectionRatio;
            var kept = scores
                .Where(s => s.Score >= floor)
                .Select(s => s.Qualified)
                .ToHashSet(StringComparer.Ordinal);
            if (kept.Count > 0)
                selected[candidate.Path] = kept;
        }

        return selected;
    }

    private static double MemberQueryScore(
        SymbolChunk chunk,
        IReadOnlySet<string> queryTerms)
    {
        var score = 0.0;
        foreach (var term in RelevanceTokenizer.Tokenize(chunk.SymbolName))
        {
            if (queryTerms.Contains(term))
                score += MemberSymbolWeight;
        }

        foreach (var term in RelevanceTokenizer.Tokenize(chunk.Content))
        {
            if (queryTerms.Contains(term))
                score += 1.0;
        }

        return score;
    }

    private async Task<FilteredFileSet?> FilterByChangesAsync(
        FusionRequest request,
        IReadOnlyList<Fuse.Collection.Models.SourceFile> files,
        int parallelism,
        Indexing.IAnalysisIndex? index,
        ISourceContentProvider contentProvider,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<string> changedPaths;
        try
        {
            changedPaths = await _changeDetector.GetChangedRelativePathsAsync(
                request.Collection.SourceDirectory,
                request.Changes!.Since,
                cancellationToken);
        }
        catch (ChangeDetectionException ex)
        {
            throw new FusionException(ex.Message);
        }

        var filePathSet = files
            .Select(f => f.NormalizedRelativePath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var matchedPaths = changedPaths
            .Where(filePathSet.Contains)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // The genuinely changed files, before dependents are pulled in. Review hunks and caller pairing key off
        // this set, not the expanded one.
        var changedOnly = matchedPaths.ToArray();

        IReadOnlyDictionary<string, IReadOnlyList<string>>? provenance = null;
        IReadOnlyDictionary<string, double>? scores = null;
        string? reviewPreamble = null;
        var review = request.Changes.Review;

        if ((request.Changes.IncludeDependents || review) && matchedPaths.Count > 0)
        {
            var graph = await BuildGraphAsync(files, parallelism, index, contentProvider, cancellationToken);

            if (review)
            {
                var diffs = await GetReviewDiffsAsync(request, filePathSet, cancellationToken);
                var callers = ChangeReviewBuilder.ComputeCallers(changedOnly, graph);
                reviewPreamble = ChangeReviewBuilder.Build(diffs, callers);
            }

            var seedScores = matchedPaths.ToDictionary(p => p, _ => 1.0, StringComparer.OrdinalIgnoreCase);

            // Dependents are files that reference the changed files (reverse edges), one hop out.
            var options = new ExpansionOptions(
                Depth: 1,
                FollowReferences: false,
                FollowDependents: true);

            var expansion = _focusSeedResolver.Expand(graph, seedScores, options);
            matchedPaths = expansion.IncludedPaths;
            provenance = expansion.ProvenanceChains;
            scores = expansion.Scores;
        }

        if (matchedPaths.Count == 0)
            return null;

        var filtered = files.Where(f => matchedPaths.Contains(f.NormalizedRelativePath)).ToArray();
        return new FilteredFileSet(filtered, provenance, scores, reviewPreamble);
    }

    // Diffs for the genuinely changed files that are also in the collected set.
    private async Task<IReadOnlyList<FileDiff>> GetReviewDiffsAsync(
        FusionRequest request,
        HashSet<string> collectedPaths,
        CancellationToken cancellationToken)
    {
        try
        {
            var diffs = await _changeDetector.GetDiffsAsync(
                request.Collection.SourceDirectory,
                request.Changes!.Since,
                cancellationToken);
            return diffs.Where(d => collectedPaths.Contains(d.Path)).ToArray();
        }
        catch (ChangeDetectionException ex)
        {
            throw new FusionException(ex.Message);
        }
    }

    private Task<DependencyGraph> BuildGraphAsync(
        IReadOnlyList<Fuse.Collection.Models.SourceFile> files,
        int parallelism,
        Indexing.IAnalysisIndex? index,
        ISourceContentProvider contentProvider,
        CancellationToken cancellationToken) =>
        _dependencyGraphBuilder.BuildAsync(
            files,
            contentProvider,
            _dependencyExtractors,
            _typeNameLocators,
            parallelism,
            cancellationToken,
            index);

    // Builds the table-of-contents document and writes it as the sole output. Per-file token costs come from
    // the reduced content; the symbol outline is extracted from the original source so it is independent of
    // the reduction mode.
    private async Task<FusionResult> EmitTableOfContentsAsync(
        IReadOnlyList<FusedContent> reducedContent,
        FusionRequest request,
        ISourceContentProvider contentProvider,
        Fuse.Reduction.Tokenization.ITokenCounter tokenCounter,
        CancellationToken cancellationToken)
    {
        var started = DateTime.Now;
        var entries = new List<TableOfContentsFileEntry>(reducedContent.Count);
        foreach (var item in reducedContent)
        {
            if (item.IsTrivial)
                continue;

            IReadOnlyList<OutlineSymbol> symbols = [];
            var extractor = _outlineExtractors.TryResolve(item.SourceFile.Extension);
            if (extractor is not null)
            {
                var source = await contentProvider.GetContentAsync(item.SourceFile, cancellationToken);
                symbols = extractor.ExtractOutline(source);
            }

            entries.Add(new TableOfContentsFileEntry(item.NormalizedPath, item.TokenCount, symbols));
        }

        var (document, totalTokens) = BuildTocWithinBudget(entries, request.Emission, tokenCounter);
        var emittedFileTokens = entries
            .Select(e => new FileTokenInfo(e.Path, e.Tokens))
            .ToArray();

        IReadOnlyList<string> generatedPaths = [];
        string? inMemoryContent = null;
        if (request.InMemory)
        {
            inMemoryContent = document;
        }
        else
        {
            var naming = new OutputNamingService();
            var baseName = naming.GetBaseFileName(request.Emission);
            var fileName = OutputNamingService.BuildPartFileName(baseName, 1, totalTokens, isMultiPart: false);
            Directory.CreateDirectory(request.Emission.OutputDirectory);
            var path = Path.Combine(request.Emission.OutputDirectory, fileName);
            await _fileSystem.WriteAllTextAsync(path, document);
            generatedPaths = [path];
        }

        return new FusionResult(
            generatedPaths,
            inMemoryContent,
            totalTokens,
            entries.Count,
            reducedContent.Count,
            DateTime.Now - started,
            [],
            emittedFileTokens: emittedFileTokens);
    }

    // Renders the table of contents at the highest detail level that fits the configured token budget. With no
    // budget the full document is returned unchanged; otherwise detail degrades in steps (drop symbol outlines,
    // then collapse to directory aggregates) so a large codebase produces a usable map rather than a payload a
    // size-capped consumer rejects.
    private static (string Document, int Tokens) BuildTocWithinBudget(
        IReadOnlyList<TableOfContentsFileEntry> entries,
        EmissionOptions emission,
        Fuse.Reduction.Tokenization.ITokenCounter tokenCounter)
    {
        var document = TableOfContentsBuilder.Build(entries, emission.Format, TableOfContentsDetail.Full);
        var tokens = tokenCounter.Count(document);

        if (emission.TableOfContentsMaxTokens is not int budget || tokens <= budget)
            return (document, tokens);

        foreach (var detail in (ReadOnlySpan<TableOfContentsDetail>)[TableOfContentsDetail.PathsOnly, TableOfContentsDetail.Directories])
        {
            document = TableOfContentsBuilder.Build(entries, emission.Format, detail);
            tokens = tokenCounter.Count(document);
            if (tokens <= budget)
                break;
        }

        return (document, tokens);
    }

    private FusionResult CreateEmptyChangeResult(FusionRequest request)
    {
        var since = request.Changes?.Since ?? "ref";
        var diagnostic = $"<!-- fuse: no files changed since {since} -->";

        return new FusionResult(
            [],
            request.InMemory ? diagnostic : null,
            0,
            0,
            0,
            TimeSpan.Zero,
            []);
    }

    private sealed record FilteredFileSet(
        IReadOnlyList<Fuse.Collection.Models.SourceFile> Files,
        IReadOnlyDictionary<string, IReadOnlyList<string>>? Provenance,
        IReadOnlyDictionary<string, double>? Scores,
        string? Preamble = null,
        SymbolSliceRequest? Slice = null,
        IReadOnlyDictionary<string, IReadOnlySet<string>>? SelectedMembers = null);

    private sealed record SymbolSliceRequest(IReadOnlySet<string> Paths, string Member);
}
