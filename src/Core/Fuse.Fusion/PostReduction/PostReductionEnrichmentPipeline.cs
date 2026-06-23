using System.IO.Hashing;
using System.Text;
using Fuse.Collection.FileSystem;
using Fuse.Collection.Models;
using Fuse.Emission;
using Fuse.Emission.Manifest;
using Fuse.Emission.Models;
using Fuse.Emission.Writers;
using Fuse.Fusion.Enrichment;
using Fuse.Fusion.Scoping;
using Fuse.Plugins.Abstractions;
using Fuse.Plugins.Abstractions.Maps;
using Fuse.Plugins.Abstractions.Patterns;
using Fuse.Reduction.Models;
using Fuse.Reduction.Security;

namespace Fuse.Fusion.PostReduction;

/// <summary>
///     Runs post-reduction enrichment, token-budget packing, emission, and output append/prepend stages.
/// </summary>
/// <remarks>
///     Invoked by <see cref="FusionOrchestrator" /> after collection, scoping, reduction, and optional
///     symbol-level transforms. Table-of-contents mode is handled separately by the orchestrator.
/// </remarks>
public sealed class PostReductionEnrichmentPipeline
{
    private readonly EmissionPipeline _emissionPipeline;
    private readonly BoilerplateDeduplicator _boilerplateDeduplicator;
    private readonly BodyDeduplicator _bodyDeduplicator;
    private readonly Session.ISessionTracker _sessionTracker;
    private readonly IGitStatsProvider _gitStatsProvider;
    private readonly IFileSystem _fileSystem;
    private readonly IEnumerable<PatternDetectorBase> _patternDetectors;
    private readonly IRouteMapGenerator? _routeMapGenerator;
    private readonly IProjectGraphGenerator? _projectGraphGenerator;

    /// <summary>
    ///     Initializes a new instance of the <see cref="PostReductionEnrichmentPipeline" /> class.
    /// </summary>
    public PostReductionEnrichmentPipeline(
        EmissionPipeline emissionPipeline,
        BoilerplateDeduplicator boilerplateDeduplicator,
        BodyDeduplicator bodyDeduplicator,
        Session.ISessionTracker sessionTracker,
        IGitStatsProvider gitStatsProvider,
        IFileSystem fileSystem,
        IEnumerable<PatternDetectorBase> patternDetectors,
        IRouteMapGenerator? routeMapGenerator = null,
        IProjectGraphGenerator? projectGraphGenerator = null)
    {
        _emissionPipeline = emissionPipeline;
        _boilerplateDeduplicator = boilerplateDeduplicator;
        _bodyDeduplicator = bodyDeduplicator;
        _sessionTracker = sessionTracker;
        _gitStatsProvider = gitStatsProvider;
        _fileSystem = fileSystem;
        _patternDetectors = patternDetectors;
        _routeMapGenerator = routeMapGenerator;
        _projectGraphGenerator = projectGraphGenerator;
    }

    /// <summary>
    ///     Applies enrichment, packs to budget when configured, emits output, and prepends or appends optional
    ///     sections.
    /// </summary>
    /// <param name="context">Post-reduction inputs including reduced content and scoping metadata.</param>
    /// <param name="contentProvider">The run-scoped content provider for structural map generation.</param>
    /// <param name="cancellationToken">Token used to cancel enrichment and emission work.</param>
    /// <returns>The completed fusion result before reduction-cache statistics are merged.</returns>
    public async Task<FusionResult> ProcessAsync(
        PostReductionContext context,
        ISourceContentProvider contentProvider,
        CancellationToken cancellationToken)
    {
        var request = context.Request;
        var reducedContent = context.ReducedContent;

        string? sessionNote = null;
        if (!string.IsNullOrEmpty(request.Emission.SessionId))
            (reducedContent, sessionNote) = ApplySessionDelta(reducedContent, request.Emission.SessionId);

        string? headerPreamble = null;
        if (request.Emission.DeduplicateHeaders)
        {
            var dedup = _boilerplateDeduplicator.Deduplicate(reducedContent, context.TokenCounter);
            reducedContent = dedup.Content;
            headerPreamble = dedup.Preamble;
        }

        if (request.Emission.DeduplicateBodies)
            reducedContent = _bodyDeduplicator.Deduplicate(reducedContent, context.TokenCounter).Content;

        if (request.Emission.IncludeProvenance && context.Provenance is not null)
            reducedContent = AttachProvenance(reducedContent, context.Provenance, context.SelectedMembers);

        if (context.Scores is not null)
            reducedContent = AttachRelevance(reducedContent, context.Scores);

        if ((request.Focus is not null || request.Query is not null) &&
            request.Emission.MaxTokens is { } maxTokens)
        {
            reducedContent = ReductionAwarePacker.Pack(
                reducedContent, maxTokens, EmissionPipeline.MarkerOverheadTokens);
        }

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
            ? new InMemoryOutputWriter(request.Emission, context.TokenCounter, context.EntryFormatter)
            : new DiskOutputWriter(request.Emission, context.TokenCounter, context.EntryFormatter);

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

        emissionResult = await ApplyStructuralMapsAsync(
            emissionResult, context.CollectedFiles, request, contentProvider, cancellationToken);

        if (request.Reduction.IncludeRedactReport)
            emissionResult = await ApplyRedactReportAsync(emissionResult, reducedContent);

        if (request.Reduction.IncludePatternSummary && !request.Emission.IncludeManifest)
            emissionResult = await ApplyPatternSummaryAsync(emissionResult, patternSummary);

        if (headerPreamble is not null)
            emissionResult = await PrependToDiskOrMemoryAsync(emissionResult, headerPreamble);

        if (!string.IsNullOrEmpty(context.ReviewPreamble))
            emissionResult = await PrependToDiskOrMemoryAsync(emissionResult, context.ReviewPreamble);

        if (!string.IsNullOrEmpty(sessionNote))
            emissionResult = await PrependToDiskOrMemoryAsync(emissionResult, sessionNote);

        return emissionResult;
    }

    private static IReadOnlyList<FusedContent> AttachProvenance(
        IReadOnlyList<FusedContent> content,
        IReadOnlyDictionary<string, IReadOnlyList<string>> provenance,
        IReadOnlyDictionary<string, IReadOnlySet<string>>? selectedMembers) =>
        content
            .Select(item =>
            {
                if (!provenance.TryGetValue(item.NormalizedPath, out var chain))
                    return item;

                if (selectedMembers is not null &&
                    selectedMembers.TryGetValue(item.NormalizedPath, out var members) &&
                    members.Count > 0)
                {
                    chain = [.. chain, .. members.OrderBy(m => m, StringComparer.Ordinal)];
                }

                return item.WithInclusionChain(chain);
            })
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

    private async Task<FusionResult> ApplyStructuralMapsAsync(
        FusionResult emissionResult,
        IReadOnlyList<SourceFile> collectedFiles,
        FusionRequest request,
        ISourceContentProvider contentProvider,
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
                    await contentProvider.GetContentAsync(file, cancellationToken);
                continue;
            }

            if (request.Reduction.IncludeProjectGraph &&
                _projectGraphGenerator?.SupportedExtensions.Contains(file.Extension) == true)
            {
                fileContents[file.NormalizedRelativePath] =
                    await contentProvider.GetContentAsync(file, cancellationToken);
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

        var secretTotal = counts.Where(kv => kv.Key != SecretRedactionResult.CodeLiteralKind).Sum(kv => kv.Value);
        var summary = new RedactionSummary(counts, secretTotal);
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
}
