using System.IO.Hashing;
using System.Text;
using Fuse.Fusion.Scoping;
using Fuse.Fusion.Enrichment;
using Fuse.Collection;
using Fuse.Collection.FileSystem;
using Fuse.Emission;
using Fuse.Emission.Manifest;
using Fuse.Emission.Models;
using Fuse.Emission.Tokenization;
using Fuse.Emission.Writers;
using Fuse.Plugins.Abstractions;
using Fuse.Plugins.Abstractions.Dependencies;
using Fuse.Plugins.Abstractions.Maps;
using Fuse.Plugins.Abstractions.Outline;
using Fuse.Plugins.Abstractions.Patterns;
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
///     the collection, reduction, and emission pipelines. See <see cref="FuseAsync" /> for the stage ordering.
/// </remarks>
public sealed class FusionOrchestrator
{
    private readonly FusionValidator _validator;
    private readonly TokenizerFactory _tokenizerFactory;
    private readonly FileCollectionPipeline _collectionPipeline;
    private readonly ContentReductionPipeline _reductionPipeline;
    private readonly EmissionPipeline _emissionPipeline;
    private readonly DependencyGraphBuilder _dependencyGraphBuilder;
    private readonly FocusSeedResolver _focusSeedResolver;
    private readonly IChangeDetector _changeDetector;
    private readonly IFileSystem _fileSystem;
    private readonly ISourceContentProvider _contentProvider;
    private readonly IEnumerable<PatternDetectorBase> _patternDetectors;
    private readonly CapabilityRegistry<IDependencyExtractor> _dependencyExtractors;
    private readonly CapabilityRegistry<ITypeNameLocator> _typeNameLocators;
    private readonly CapabilityRegistry<ISymbolOutlineExtractor> _outlineExtractors;
    private readonly IRelevanceIndex _relevanceIndex;
    private readonly IRouteMapGenerator? _routeMapGenerator;
    private readonly IProjectGraphGenerator? _projectGraphGenerator;
    private readonly IReductionCacheFactory _reductionCacheFactory;
    private readonly IGitStatsProvider _gitStatsProvider;
    private readonly Enrichment.BoilerplateDeduplicator _boilerplateDeduplicator;
    private readonly Session.ISessionTracker _sessionTracker;

    /// <summary>
    ///     Initializes a new instance of the <see cref="FusionOrchestrator" /> class.
    /// </summary>
    public FusionOrchestrator(
        FusionValidator validator,
        TokenizerFactory tokenizerFactory,
        FileCollectionPipeline collectionPipeline,
        ContentReductionPipeline reductionPipeline,
        EmissionPipeline emissionPipeline,
        DependencyGraphBuilder dependencyGraphBuilder,
        FocusSeedResolver focusSeedResolver,
        IChangeDetector changeDetector,
        IFileSystem fileSystem,
        ISourceContentProvider contentProvider,
        IEnumerable<PatternDetectorBase> patternDetectors,
        CapabilityRegistry<IDependencyExtractor> dependencyExtractors,
        CapabilityRegistry<ITypeNameLocator> typeNameLocators,
        CapabilityRegistry<ISymbolOutlineExtractor> outlineExtractors,
        IRelevanceIndex relevanceIndex,
        IReductionCacheFactory reductionCacheFactory,
        IGitStatsProvider gitStatsProvider,
        Enrichment.BoilerplateDeduplicator boilerplateDeduplicator,
        Session.ISessionTracker sessionTracker,
        IRouteMapGenerator? routeMapGenerator = null,
        IProjectGraphGenerator? projectGraphGenerator = null)
    {
        _validator = validator;
        _tokenizerFactory = tokenizerFactory;
        _collectionPipeline = collectionPipeline;
        _reductionPipeline = reductionPipeline;
        _emissionPipeline = emissionPipeline;
        _dependencyGraphBuilder = dependencyGraphBuilder;
        _focusSeedResolver = focusSeedResolver;
        _changeDetector = changeDetector;
        _fileSystem = fileSystem;
        _contentProvider = contentProvider;
        _patternDetectors = patternDetectors;
        _dependencyExtractors = dependencyExtractors;
        _typeNameLocators = typeNameLocators;
        _outlineExtractors = outlineExtractors;
        _relevanceIndex = relevanceIndex;
        _reductionCacheFactory = reductionCacheFactory;
        _gitStatsProvider = gitStatsProvider;
        _boilerplateDeduplicator = boilerplateDeduplicator;
        _sessionTracker = sessionTracker;
        _routeMapGenerator = routeMapGenerator;
        _projectGraphGenerator = projectGraphGenerator;
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
    /// </remarks>
    public async Task<FusionResult> FuseAsync(FusionRequest request, CancellationToken cancellationToken = default)
    {
        _validator.ValidateOrThrow(request);

        _contentProvider.Clear();

        var parallelism = request.Parallelism > 0 ? request.Parallelism : Environment.ProcessorCount;
        var tokenCounter = _tokenizerFactory.GetCounter(request.Emission.TokenizerModel);
        var entryFormatter = EntryFormatterFactory.Create(request.Emission.Format);

        var collectionResult = await _collectionPipeline.CollectAsync(
            request.Collection,
            parallelism,
            cancellationToken);

        var filterResult = await FilterFilesAsync(request, collectionResult.Files, parallelism, cancellationToken);
        if (filterResult is null)
            return CreateEmptyChangeResult(request);

        var reductionCache = request.UseReductionCache
            ? _reductionCacheFactory.Create(
                request.Collection.SourceDirectory,
                enabled: true,
                clearBeforeRun: request.ClearReductionCache)
            : null;

        var reducedContent = await _reductionPipeline.ReduceAsync(
            filterResult.Files,
            request.Reduction,
            parallelism,
            reductionCache,
            tokenCounter,
            cancellationToken);

        // Table-of-contents mode replaces body emission with a structural map. It runs after scoping and
        // reduction so the listed files and per-file token costs match what a full fetch would cost.
        if (request.Emission.TableOfContents)
        {
            var tocResult = await EmitTableOfContentsAsync(reducedContent, request, tokenCounter, cancellationToken);
            return WithReductionCacheStats(tocResult, reductionCache);
        }

        // Session-delta mode drops files whose identical content was already emitted earlier in the session, so
        // a multi-call task does not pay to resend material the agent already holds.
        string? sessionNote = null;
        if (!string.IsNullOrEmpty(request.Emission.SessionId))
            (reducedContent, sessionNote) = ApplySessionDelta(reducedContent, request.Emission.SessionId);

        string? headerPreamble = null;
        if (request.Emission.DeduplicateHeaders)
        {
            var dedup = _boilerplateDeduplicator.Deduplicate(reducedContent, tokenCounter);
            reducedContent = dedup.Content;
            headerPreamble = dedup.Preamble;
        }

        if (request.Emission.IncludeProvenance && filterResult.Provenance is not null)
            reducedContent = AttachProvenance(reducedContent, filterResult.Provenance);

        if (filterResult.Scores is not null)
            reducedContent = AttachRelevance(reducedContent, filterResult.Scores);

        PatternSummary? patternSummary = null;
        if (request.Reduction.IncludePatternSummary)
            patternSummary = DetectPatterns(reducedContent);

        GitStatsResult? gitStats = null;
        if (request.Emission.IncludeGitStats)
        {
            var paths = reducedContent
                .Where(c => !c.IsTrivial)
                .Select(c => c.NormalizedPath)
                .ToArray();
            gitStats = await _gitStatsProvider.GetStatsAsync(
                request.Collection.SourceDirectory,
                paths,
                cancellationToken);
        }

        IOutputWriter writer = request.InMemory
            ? new InMemoryOutputWriter(request.Emission, tokenCounter, entryFormatter)
            : new DiskOutputWriter(request.Emission, tokenCounter, entryFormatter);

        FusionResult emissionResult;
        try
        {
            emissionResult = await _emissionPipeline.EmitAsync(
                reducedContent,
                request.Emission,
                writer,
                request.Emission.IncludeManifest ? patternSummary : null,
                request.Emission.IncludeManifest ? gitStats : null,
                cancellationToken);
        }
        finally
        {
            if (writer is IAsyncDisposable disposable)
                await disposable.DisposeAsync();
        }

        emissionResult = await ApplyStructuralMapsAsync(emissionResult, collectionResult.Files, request, cancellationToken);

        if (request.Reduction.IncludeRedactReport)
            emissionResult = await ApplyRedactReportAsync(emissionResult, reducedContent);

        if (request.Reduction.IncludePatternSummary && !request.Emission.IncludeManifest)
            emissionResult = await ApplyPatternSummaryAsync(emissionResult, patternSummary);

        if (headerPreamble is not null)
            emissionResult = await PrependToDiskOrMemoryAsync(emissionResult, headerPreamble);

        // The review map goes to the very top so a reviewer sees what changed and who calls it before the
        // file bodies that follow.
        if (!string.IsNullOrEmpty(filterResult.Preamble))
            emissionResult = await PrependToDiskOrMemoryAsync(emissionResult, filterResult.Preamble);

        if (!string.IsNullOrEmpty(sessionNote))
            emissionResult = await PrependToDiskOrMemoryAsync(emissionResult, sessionNote);

        return WithReductionCacheStats(emissionResult, reductionCache);
    }

    private static IReadOnlyList<FusedContent> AttachProvenance(
        IReadOnlyList<FusedContent> content,
        IReadOnlyDictionary<string, IReadOnlyList<string>> provenance) =>
        content
            .Select(item =>
                provenance.TryGetValue(item.NormalizedPath, out var chain)
                    ? item.WithInclusionChain(chain)
                    : item)
            .ToArray();

    private static IReadOnlyList<FusedContent> AttachRelevance(
        IReadOnlyList<FusedContent> content,
        IReadOnlyDictionary<string, double> scores) =>
        content
            .Select(item =>
                scores.TryGetValue(item.NormalizedPath, out var score)
                    ? item.WithRelevanceScore(score)
                    : item)
            .ToArray();

    // Splits reduced content into the entries to emit (new or changed for the session) and a note listing the
    // entries omitted because they were already sent. Trivial entries pass through untouched.
    private (IReadOnlyList<FusedContent> Kept, string? Note) ApplySessionDelta(
        IReadOnlyList<FusedContent> reducedContent,
        string sessionId)
    {
        var kept = new List<FusedContent>(reducedContent.Count);
        var omitted = new List<string>();

        foreach (var entry in reducedContent)
        {
            if (entry.IsTrivial)
            {
                kept.Add(entry);
                continue;
            }

            var hash = XxHash64.HashToUInt64(Encoding.UTF8.GetBytes(entry.Content));
            if (_sessionTracker.TryClaim(sessionId, entry.NormalizedPath, hash))
                kept.Add(entry);
            else
                omitted.Add(entry.NormalizedPath);
        }

        return (kept, Session.SessionDeltaBuilder.BuildNote(sessionId, omitted));
    }

    private PatternSummary? DetectPatterns(IReadOnlyList<FusedContent> reducedContent)
    {
        var snapshots = reducedContent
            .Select(c => new FusedFileSnapshot(c.NormalizedPath, c.Content))
            .ToArray();

        var patterns = PatternDetectionBatch.Run(_patternDetectors, snapshots);
        return patterns.Count == 0 ? null : new PatternSummary(patterns);
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
        CancellationToken cancellationToken)
    {
        if (request.Focus is not null)
            return await FilterByFocusAsync(request, files, parallelism, cancellationToken);

        if (request.Changes is not null)
            return await FilterByChangesAsync(request, files, parallelism, cancellationToken);

        if (request.Query is not null)
            return await FilterByQueryAsync(request, files, parallelism, cancellationToken);

        return new FilteredFileSet(files, null, null);
    }

    private async Task<FilteredFileSet> FilterByFocusAsync(
        FusionRequest request,
        IReadOnlyList<Fuse.Collection.Models.SourceFile> files,
        int parallelism,
        CancellationToken cancellationToken)
    {
        var graph = await BuildGraphAsync(files, parallelism, cancellationToken);

        var seedPaths = await _focusSeedResolver.ResolveSeedPathsAsync(
            request.Focus!.Seed,
            files,
            _contentProvider,
            cancellationToken);

        if (seedPaths.Count == 0)
        {
            throw new FusionValidationException(
                $"Focus seed '{request.Focus.Seed}' matched no collected files.");
        }

        var seedScores = seedPaths.ToDictionary(p => p, _ => 1.0, StringComparer.OrdinalIgnoreCase);
        var options = new ExpansionOptions(
            request.Focus.Depth,
            FollowReferences: true,
            FollowDependents: true,
            TokenBudget: request.Emission.MaxTokens,
            TokenCosts: BuildTokenCosts(files, request.Emission.MaxTokens));

        var expansion = _focusSeedResolver.Expand(graph, seedScores, options);
        var filtered = files.Where(f => expansion.IncludedPaths.Contains(f.NormalizedRelativePath)).ToArray();
        return new FilteredFileSet(filtered, expansion.ProvenanceChains, expansion.Scores);
    }

    private async Task<FilteredFileSet> FilterByQueryAsync(
        FusionRequest request,
        IReadOnlyList<Fuse.Collection.Models.SourceFile> files,
        int parallelism,
        CancellationToken cancellationToken)
    {
        var documents = new Dictionary<string, IndexedDocument>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in files)
        {
            var content = await _contentProvider.GetContentAsync(file, cancellationToken);
            var locator = _typeNameLocators.TryResolve(file.Extension);
            var symbols = locator?.ExtractDefinedSymbols(content);
            documents[file.NormalizedRelativePath] = new IndexedDocument(content, file.NormalizedRelativePath, symbols);
        }

        _relevanceIndex.Index(documents);
        var ranked = _relevanceIndex.RankScored(request.Query!.Query, request.Query.TopFiles);

        if (ranked.Count == 0)
        {
            throw new FusionValidationException(
                $"Query '{request.Query.Query}' matched no collected files.");
        }

        var seedScores = ranked.ToDictionary(r => r.Path, r => r.Score, StringComparer.OrdinalIgnoreCase);
        var graph = await BuildGraphAsync(files, parallelism, cancellationToken);

        // Query seeds are already content-matched; expand forward to their dependencies for context, but do
        // not follow dependents, which would broaden the set with files that merely use a matched type.
        var options = new ExpansionOptions(
            request.Query.Depth,
            FollowReferences: true,
            FollowDependents: false,
            TokenBudget: request.Emission.MaxTokens,
            TokenCosts: BuildTokenCosts(files, request.Emission.MaxTokens));

        var expansion = _focusSeedResolver.Expand(graph, seedScores, options);
        var filtered = files.Where(f => expansion.IncludedPaths.Contains(f.NormalizedRelativePath)).ToArray();
        return new FilteredFileSet(filtered, expansion.ProvenanceChains, expansion.Scores);
    }

    private async Task<FilteredFileSet?> FilterByChangesAsync(
        FusionRequest request,
        IReadOnlyList<Fuse.Collection.Models.SourceFile> files,
        int parallelism,
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
            var graph = await BuildGraphAsync(files, parallelism, cancellationToken);

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

    // Diffs for the genuinely changed files that are also in the collected set. Failures to read diffs are
    // tolerated here: a missing diff degrades the review map rather than failing the whole run.
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
        catch (ChangeDetectionException)
        {
            return [];
        }
    }

    private static IReadOnlyDictionary<string, int>? BuildTokenCosts(
        IReadOnlyList<Fuse.Collection.Models.SourceFile> files,
        int? maxTokens)
    {
        if (maxTokens is null)
            return null;

        // Pre-reduction estimate: roughly four characters per token. Used only to bound expansion spread;
        // emission applies the exact, relevance-ordered budget cut.
        var costs = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in files)
            costs[file.NormalizedRelativePath] = (int)Math.Max(1, file.FileInfo.Length / 4);

        return costs;
    }

    private Task<DependencyGraph> BuildGraphAsync(
        IReadOnlyList<Fuse.Collection.Models.SourceFile> files,
        int parallelism,
        CancellationToken cancellationToken) =>
        _dependencyGraphBuilder.BuildAsync(
            files,
            _contentProvider,
            _dependencyExtractors,
            _typeNameLocators,
            parallelism,
            cancellationToken);

    // Builds the table-of-contents document and writes it as the sole output. Per-file token costs come from
    // the reduced content; the symbol outline is extracted from the original source so it is independent of
    // the reduction mode.
    private async Task<FusionResult> EmitTableOfContentsAsync(
        IReadOnlyList<FusedContent> reducedContent,
        FusionRequest request,
        Fuse.Reduction.Tokenization.ITokenCounter tokenCounter,
        CancellationToken cancellationToken)
    {
        var started = DateTime.Now;
        var entries = new List<TocFileEntry>(reducedContent.Count);
        foreach (var item in reducedContent)
        {
            if (item.IsTrivial)
                continue;

            IReadOnlyList<OutlineSymbol> symbols = [];
            var extractor = _outlineExtractors.TryResolve(item.SourceFile.Extension);
            if (extractor is not null)
            {
                var source = await _contentProvider.GetContentAsync(item.SourceFile, cancellationToken);
                symbols = extractor.ExtractOutline(source);
            }

            entries.Add(new TocFileEntry(item.NormalizedPath, item.TokenCount, symbols));
        }

        var document = TableOfContentsBuilder.Build(entries, request.Emission.Format);
        var totalTokens = tokenCounter.Count(document);
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

    private async Task<FusionResult> ApplyStructuralMapsAsync(
        FusionResult emissionResult,
        IReadOnlyList<Fuse.Collection.Models.SourceFile> collectedFiles,
        FusionRequest request,
        CancellationToken cancellationToken)
    {
        if (!request.Reduction.IncludeRouteMap && !request.Reduction.IncludeProjectGraph)
            return emissionResult;

        var fileContents = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in collectedFiles)
        {
            if (request.Reduction.IncludeRouteMap &&
                _routeMapGenerator?.SupportedExtensions.Contains(file.Extension) == true)
            {
                fileContents[file.NormalizedRelativePath] =
                    await _contentProvider.GetContentAsync(file, cancellationToken);
                continue;
            }

            if (request.Reduction.IncludeProjectGraph &&
                _projectGraphGenerator?.SupportedExtensions.Contains(file.Extension) == true)
            {
                fileContents[file.NormalizedRelativePath] =
                    await _contentProvider.GetContentAsync(file, cancellationToken);
            }
        }

        var prefix = string.Empty;
        if (request.Reduction.IncludeRouteMap && _routeMapGenerator is not null)
            prefix += _routeMapGenerator.Generate(fileContents) + "\n";

        if (request.Reduction.IncludeProjectGraph && _projectGraphGenerator is not null)
            prefix += _projectGraphGenerator.Generate(fileContents) + "\n";

        if (string.IsNullOrWhiteSpace(prefix))
            return emissionResult;

        prefix = prefix.TrimEnd();
        var inMemoryContent = emissionResult.InMemoryContent;
        if (!string.IsNullOrEmpty(inMemoryContent))
            inMemoryContent = prefix + "\n" + inMemoryContent;

        var generatedPaths = emissionResult.GeneratedPaths.ToList();
        if (generatedPaths.Count > 0)
        {
            var lastPath = generatedPaths[^1];
            var existing = await _fileSystem.ReadAllTextAsync(lastPath);
            await _fileSystem.WriteAllTextAsync(lastPath, prefix + "\n" + existing);
        }

        return new FusionResult(
            generatedPaths,
            inMemoryContent,
            emissionResult.TotalTokens,
            emissionResult.ProcessedFileCount,
            emissionResult.TotalFileCount,
            emissionResult.Duration,
            emissionResult.TopTokenFiles,
            emissionResult.PatternSummary,
            emissionResult.ReductionCacheHits,
            emissionResult.ReductionCacheMisses,
            emissionResult.EmittedFileTokens);
    }

    private async Task<FusionResult> ApplyRedactReportAsync(
        FusionResult emissionResult,
        IReadOnlyList<FusedContent> reducedContent)
    {
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in reducedContent)
        {
            if (entry.RedactionCounts is null)
                continue;

            foreach (var (kind, count) in entry.RedactionCounts)
            {
                counts.TryGetValue(kind, out var current);
                counts[kind] = current + count;
            }
        }

        var summary = new RedactionSummary(counts, counts.Values.Sum());
        var comment = summary.ToComment();
        if (string.IsNullOrEmpty(comment))
            return emissionResult;

        return await AppendToDiskOrMemoryAsync(emissionResult, comment);
    }

    private async Task<FusionResult> ApplyPatternSummaryAsync(
        FusionResult emissionResult,
        PatternSummary? patternSummary)
    {
        if (patternSummary is null)
            return emissionResult;

        var comment = patternSummary.ToComment();
        if (string.IsNullOrEmpty(comment))
            return emissionResult;

        return await AppendToDiskOrMemoryAsync(emissionResult, comment);
    }

    private async Task<FusionResult> AppendToDiskOrMemoryAsync(FusionResult emissionResult, string comment)
    {
        var inMemoryContent = emissionResult.InMemoryContent;
        if (!string.IsNullOrEmpty(inMemoryContent))
            inMemoryContent += "\n" + comment;

        var generatedPaths = emissionResult.GeneratedPaths.ToList();
        if (generatedPaths.Count > 0)
        {
            var lastPath = generatedPaths[^1];
            var existing = await _fileSystem.ReadAllTextAsync(lastPath);
            await _fileSystem.WriteAllTextAsync(lastPath, existing + "\n" + comment);
        }

        return new FusionResult(
            generatedPaths,
            inMemoryContent,
            emissionResult.TotalTokens,
            emissionResult.ProcessedFileCount,
            emissionResult.TotalFileCount,
            emissionResult.Duration,
            emissionResult.TopTokenFiles,
            emissionResult.PatternSummary,
            emissionResult.ReductionCacheHits,
            emissionResult.ReductionCacheMisses,
            emissionResult.EmittedFileTokens);
    }

    private async Task<FusionResult> PrependToDiskOrMemoryAsync(FusionResult emissionResult, string preamble)
    {
        var inMemoryContent = emissionResult.InMemoryContent;
        // Prepend even to empty in-memory content so a note survives when every entry was omitted (for example
        // a session-delta call where the agent already holds every file).
        if (inMemoryContent is not null)
            inMemoryContent = string.IsNullOrEmpty(inMemoryContent) ? preamble : preamble + "\n" + inMemoryContent;

        var generatedPaths = emissionResult.GeneratedPaths.ToList();
        if (generatedPaths.Count > 0)
        {
            var firstPath = generatedPaths[0];
            var existing = await _fileSystem.ReadAllTextAsync(firstPath);
            await _fileSystem.WriteAllTextAsync(firstPath, preamble + "\n" + existing);
        }

        return new FusionResult(
            generatedPaths,
            inMemoryContent,
            emissionResult.TotalTokens,
            emissionResult.ProcessedFileCount,
            emissionResult.TotalFileCount,
            emissionResult.Duration,
            emissionResult.TopTokenFiles,
            emissionResult.PatternSummary,
            emissionResult.ReductionCacheHits,
            emissionResult.ReductionCacheMisses,
            emissionResult.EmittedFileTokens);
    }

    private sealed record FilteredFileSet(
        IReadOnlyList<Fuse.Collection.Models.SourceFile> Files,
        IReadOnlyDictionary<string, IReadOnlyList<string>>? Provenance,
        IReadOnlyDictionary<string, double>? Scores,
        string? Preamble = null);
}
