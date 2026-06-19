using Fuse.Analysis.Changes;
using Fuse.Analysis.Dependencies;
using Fuse.Analysis.Git;
using Fuse.Analysis.Patterns;
using Fuse.Analysis.Search;
using Fuse.Collection;
using Fuse.Collection.FileSystem;
using Fuse.Emission;
using Fuse.Emission.Models;
using Fuse.Emission.Tokenization;
using Fuse.Emission.Writers;
using Fuse.Plugins.Abstractions;
using Fuse.Plugins.Abstractions.Dependencies;
using Fuse.Plugins.Abstractions.Maps;
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
    private readonly IRelevanceIndex _relevanceIndex;
    private readonly IRouteMapGenerator? _routeMapGenerator;
    private readonly IProjectGraphGenerator? _projectGraphGenerator;
    private readonly IReductionCacheFactory _reductionCacheFactory;
    private readonly IGitStatsProvider _gitStatsProvider;

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
        IRelevanceIndex relevanceIndex,
        IReductionCacheFactory reductionCacheFactory,
        IGitStatsProvider gitStatsProvider,
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
        _relevanceIndex = relevanceIndex;
        _reductionCacheFactory = reductionCacheFactory;
        _gitStatsProvider = gitStatsProvider;
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

        if (request.Emission.IncludeProvenance && filterResult.Provenance is not null)
            reducedContent = AttachProvenance(reducedContent, filterResult.Provenance);

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
            cache.Statistics.Misses);
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

        return new FilteredFileSet(files, null);
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

        var expansion = _focusSeedResolver.ExpandPaths(graph, seedPaths, request.Focus.Depth);
        var filtered = files.Where(f => expansion.IncludedPaths.Contains(f.NormalizedRelativePath)).ToArray();
        return new FilteredFileSet(filtered, expansion.ProvenanceChains);
    }

    private async Task<FilteredFileSet> FilterByQueryAsync(
        FusionRequest request,
        IReadOnlyList<Fuse.Collection.Models.SourceFile> files,
        int parallelism,
        CancellationToken cancellationToken)
    {
        var fileContents = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in files)
        {
            var content = await _contentProvider.GetContentAsync(file, cancellationToken);
            fileContents[file.NormalizedRelativePath] = content;
        }

        _relevanceIndex.Index(fileContents);
        var seedPaths = _relevanceIndex.Rank(request.Query!.Query, request.Query.TopFiles)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (seedPaths.Count == 0)
        {
            throw new FusionValidationException(
                $"Query '{request.Query.Query}' matched no collected files.");
        }

        var graph = await BuildGraphAsync(files, parallelism, cancellationToken);
        var expansion = _focusSeedResolver.ExpandPaths(graph, seedPaths, request.Query.Depth);
        var filtered = files.Where(f => expansion.IncludedPaths.Contains(f.NormalizedRelativePath)).ToArray();
        return new FilteredFileSet(filtered, expansion.ProvenanceChains);
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

        IReadOnlyDictionary<string, IReadOnlyList<string>>? provenance = null;
        if (request.Changes.IncludeDependents && matchedPaths.Count > 0)
        {
            var graph = await BuildGraphAsync(files, parallelism, cancellationToken);
            var expansion = _focusSeedResolver.ExpandPaths(graph, matchedPaths, depth: 1);
            matchedPaths = expansion.IncludedPaths;
            provenance = expansion.ProvenanceChains;
        }

        if (matchedPaths.Count == 0)
            return null;

        var filtered = files.Where(f => matchedPaths.Contains(f.NormalizedRelativePath)).ToArray();
        return new FilteredFileSet(filtered, provenance);
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
            emissionResult.PatternSummary);
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
            emissionResult.PatternSummary);
    }

    private sealed record FilteredFileSet(
        IReadOnlyList<Fuse.Collection.Models.SourceFile> Files,
        IReadOnlyDictionary<string, IReadOnlyList<string>>? Provenance);
}
