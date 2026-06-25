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
    private readonly QueryScopingPipeline _queryScopingPipeline;
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
        _queryScopingPipeline = new QueryScopingPipeline(
            dependencyExtractors,
            typeNameLocators,
            chunkExtractors,
            relevanceIndexFactory,
            relevanceIndexCache,
            tokenCostModel,
            gitStatsProvider,
            focusSeedResolver,
            reranker,
            _logger);
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

            // Build the explicit context plan (A1): assign each selected file a role and reduction tier once,
            // instead of inferring seed versus neighbour from the provenance chain length downstream. The plan
            // drives the per-file reduction tier below. Tiered emission (query and focus only) reduces
            // dependency-expanded neighbours to signature skeletons so each costs fewer tokens and the
            // budget-aware packer fits more files; seeds keep the request's level. Redaction-correct because the
            // skeleton is produced inside the reduction stage, not by a post-reduction source re-read.
            var contextPlan = ContextPlanBuilder.Build(request, experimental, filterResult);
            var perFileLevel = contextPlan.AppliesTieredEmission
                ? contextPlan.TierFor
                : (Func<Fuse.Collection.Models.SourceFile, Fuse.Plugins.Abstractions.Options.ReductionLevel>?)null;

            // Project the internal plan to the public read-only view carried on the result, so explain surfaces
            // (the VS Code extension) can show each file's role, tier, and score without re-running scoping.
            var planProjection = contextPlan.Files
                .Select(p => new PlannedFileInfo(
                    p.File.NormalizedRelativePath, p.Role.ToString(), p.Tier.ToString(), p.Score))
                .ToList();

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
                return WithPlan(WithReductionCacheStats(tocResult, reductionCache), planProjection);
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

            return WithPlan(WithReductionCacheStats(emissionResult, reductionCache), planProjection);
        }
        finally
        {
            if (fuseStore is not null)
                await fuseStore.DisposeAsync();
        }
    }

    // Attaches the context-plan projection to a result, copying the other fields (FusionResult is immutable).
    // The plan is the same for every emission path of a run, so it is applied once at the return.
    private static FusionResult WithPlan(FusionResult result, IReadOnlyList<PlannedFileInfo> plan)
    {
        if (plan.Count == 0)
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
            result.ReductionCacheHits,
            result.ReductionCacheMisses,
            result.EmittedFileTokens,
            plan);
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
        // The dependency graph and proximity-edge adjacency are shared with the focus and changes paths and
        // are built here, then handed to the query pipeline (A2). Both are deterministic and independent of
        // ranking, so building them before the query pipeline is behavior-identical to the former inline build.
        var graph = await BuildGraphAsync(files, parallelism, index, contentProvider, cancellationToken);
        var proximity = ResolveProximity(experimental, files, request.Collection.SourceDirectory);
        return await _queryScopingPipeline.ScopeAsync(
            request, files, index, fuseStore, contentProvider, experimental, graph, proximity, cancellationToken);
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

}
