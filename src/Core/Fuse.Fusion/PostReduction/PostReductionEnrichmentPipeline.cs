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
    private readonly Func<IReadOnlyList<PatternDetectorBase>> _patternDetectorFactory;
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
        Func<IReadOnlyList<PatternDetectorBase>> patternDetectorFactory,
        IRouteMapGenerator? routeMapGenerator = null,
        IProjectGraphGenerator? projectGraphGenerator = null)
    {
        _emissionPipeline = emissionPipeline;
        _boilerplateDeduplicator = boilerplateDeduplicator;
        _bodyDeduplicator = bodyDeduplicator;
        _sessionTracker = sessionTracker;
        _gitStatsProvider = gitStatsProvider;
        _fileSystem = fileSystem;
        _patternDetectorFactory = patternDetectorFactory;
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

        // The structural-map prefix and git stats depend on the collected/candidate set, not on which files
        // survive packing, so build them once here. They are reused for the framing reserve below and for the
        // actual emission, avoiding a second pass over the files.
        var structuralMapPrefix = await BuildStructuralMapPrefixAsync(
            context.CollectedFiles, request, contentProvider, cancellationToken);

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

        // C2 strict total-token accounting: when packing a scoped run to a budget, reserve room for every
        // output section the emission writer prepends or appends on top of the file bodies (manifest, route and
        // project maps, redaction report, pattern summary, and the session, header, and review preambles). The
        // packer then fits the file bodies into the remaining budget, so the complete payload an MCP client
        // receives fits MaxTokens rather than overrunning it by the size of the framing. The reserve uses the
        // pre-pack candidate set as an upper bound for sections whose size depends on the emitted files, so the
        // total never exceeds the budget; it can leave the body budget slightly under-filled, which is the safe
        // direction for a hard cap.
        if ((request.Focus is not null || request.Query is not null) &&
            request.Emission.MaxTokens is { } maxTokens)
        {
            // Two passes keep the guarantee tight and provable. Pass one packs the file bodies into the whole
            // budget; pass two measures the framing of that packed set and re-packs the SAME set into the
            // remaining budget. Because the second pass can only drop files, its framing can only shrink, so
            // (bodies of pass two) + (framing of pass two) <= maxTokens holds with no iteration. Measuring the
            // framing against the packed set rather than the full candidate set avoids reserving for a manifest
            // that lists far more files than survive the cut.
            var packed = ReductionAwarePacker.Pack(
                reducedContent, maxTokens, EmissionPipeline.MarkerOverheadTokens);
            var reserve = ComputeFramingReserveTokens(
                context, packed, sessionNote, headerPreamble, structuralMapPrefix, gitStats);
            reducedContent = reserve <= 0
                ? packed
                : ReductionAwarePacker.Pack(
                    packed, Math.Max(0, maxTokens - reserve), EmissionPipeline.MarkerOverheadTokens);
        }

        PatternSummary? patternSummary = null;
        if (request.Reduction.IncludePatternSummary)
            patternSummary = DetectPatterns(reducedContent);

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

        if (!string.IsNullOrEmpty(structuralMapPrefix))
            emissionResult = await ApplyStructuralMapPrefixAsync(emissionResult, structuralMapPrefix);

        if (request.Reduction.IncludeRedactReport)
            emissionResult = await ApplyRedactReportAsync(emissionResult, reducedContent);

        if (request.Reduction.IncludePatternSummary && !request.Emission.IncludeManifest)
            emissionResult = await ApplyPatternSummaryAsync(emissionResult, patternSummary);

        // Item 30: a next-best-action breadcrumb for files emitted as signatures only by tiered emission, so the
        // budget wall becomes a navigable step (call fuse_focus to expand a body) rather than a silent loss.
        var breadcrumb = BuildNavigationBreadcrumb(reducedContent, context);
        if (!string.IsNullOrEmpty(breadcrumb))
            emissionResult = await AppendToDiskOrMemoryAsync(emissionResult, breadcrumb);

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

        // Fresh detector instances per call: detectors accumulate mutable state, so a private batch keeps
        // concurrent runs (and the two detection passes within one run) from racing over shared instances.
        var patterns = PatternDetectionBatch.Run(_patternDetectorFactory(), snapshots);
        return patterns.Count == 0 ? null : new PatternSummary(patterns);
    }

    // Builds the route-map and project-graph prefix from the collected files, or null when neither is enabled
    // or both produce nothing. Separated from prepending so the prefix can be measured for the framing reserve
    // before packing and then prepended after emission without regenerating it.
    private async Task<string?> BuildStructuralMapPrefixAsync(
        IReadOnlyList<SourceFile> collectedFiles,
        FusionRequest request,
        ISourceContentProvider contentProvider,
        CancellationToken cancellationToken)
    {
        if (!request.Reduction.IncludeRouteMap && !request.Reduction.IncludeProjectGraph)
            return null;

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

        return string.IsNullOrWhiteSpace(prefix) ? null : prefix.TrimEnd();
    }

    private async Task<FusionResult> ApplyStructuralMapPrefixAsync(FusionResult emissionResult, string prefix)
    {
        var inMemoryContent = emissionResult.InMemoryContent;
        if (!string.IsNullOrEmpty(inMemoryContent))
            inMemoryContent = prefix + "\n" + inMemoryContent;

        var generatedPaths = emissionResult.GeneratedPaths.ToList();
        if (generatedPaths.Count > 0)
        {
            // Prepend to the FIRST part: the route and project maps are an overview header for the whole output,
            // so a reader meets them before any file body. Multipart output previously prepended them to the
            // last part, where they trailed the content (P2).
            var firstPath = generatedPaths[0];
            var existing = await _fileSystem.ReadAllTextAsync(firstPath);
            await _fileSystem.WriteAllTextAsync(firstPath, prefix + "\n" + existing);
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

    // Estimates the token cost of every output section emission prepends or appends on top of the file bodies,
    // so the packer can subtract it from MaxTokens and the complete payload fits the budget (C2). Sections
    // whose size depends on which files survive packing (manifest, redaction report, pattern summary) are
    // measured against the pre-pack candidate set, which lists at least as many files as the packed subset, so
    // the reserve is an upper bound and the total can only come in under the cap, never over.
    private int ComputeFramingReserveTokens(
        PostReductionContext context,
        IReadOnlyList<FusedContent> candidateContent,
        string? sessionNote,
        string? headerPreamble,
        string? structuralMapPrefix,
        GitStatsResult? gitStats)
    {
        var request = context.Request;
        var counter = context.TokenCounter;
        var reserve = 0;

        if (!string.IsNullOrEmpty(sessionNote))
            reserve += counter.Count(sessionNote);
        if (!string.IsNullOrEmpty(headerPreamble))
            reserve += counter.Count(headerPreamble);
        if (!string.IsNullOrEmpty(context.ReviewPreamble))
            reserve += counter.Count(context.ReviewPreamble);
        if (!string.IsNullOrEmpty(structuralMapPrefix))
            reserve += counter.Count(structuralMapPrefix);

        // Pattern summary appears inside the manifest when both are on, otherwise as an appended comment.
        var patterns = request.Reduction.IncludePatternSummary ? DetectPatterns(candidateContent) : null;

        if (request.Emission.IncludeManifest)
        {
            var files = candidateContent
                .Where(c => !c.IsTrivial)
                .Select(c => new FileTokenInfo(c.NormalizedPath, c.TokenCount))
                .ToList();
            var manifest = ManifestBuilder.Build(files, request.Emission.Format, gitStats, patterns);
            if (!string.IsNullOrEmpty(manifest))
                reserve += counter.Count(manifest);
        }
        else if (patterns is not null)
        {
            var comment = patterns.ToComment();
            if (!string.IsNullOrEmpty(comment))
                reserve += counter.Count(comment);
        }

        if (request.Reduction.IncludeRedactReport)
        {
            var comment = BuildRedactReportComment(candidateContent);
            if (!string.IsNullOrEmpty(comment))
                reserve += counter.Count(comment);
        }

        // The tiered-emission breadcrumb is appended after packing; reserve for it using the pre-pack candidate
        // set, an upper bound on the packed set it actually describes, so the total still fits the budget.
        var breadcrumb = BuildNavigationBreadcrumb(candidateContent, context);
        if (!string.IsNullOrEmpty(breadcrumb))
            reserve += counter.Count(breadcrumb);

        return reserve;
    }

    // Maximum skeletonized files listed individually in the breadcrumb; the rest are summarized as a count so a
    // wide neighbour set does not bloat the trailing hint.
    private const int MaxBreadcrumbEntries = 20;

    // Builds the next-best-action breadcrumb (item 30): when tiered emission skeletonized dependency-expanded
    // neighbours, list those that survived packing with the fuse_focus call that expands each one's body, or
    // null when tiering is off, the run is not query/focus, or nothing was skeletonized. Deterministic.
    private static string? BuildNavigationBreadcrumb(
        IReadOnlyList<FusedContent> emitted,
        PostReductionContext context)
    {
        if (!context.Experimental.TieredEmission)
            return null;
        if (context.Request.Focus is null && context.Request.Query is null)
            return null;
        if (context.Provenance is not { } provenance)
            return null;

        // Skeletonized neighbours are the emitted files at provenance hop two or deeper (BuildTieredLevelResolver
        // assigns them the skeleton tier); seeds (chain length one) keep their bodies.
        var neighbours = emitted
            .Where(e => !e.IsTrivial)
            .Where(e => provenance.TryGetValue(e.NormalizedPath, out var chain) && chain.Count > 1)
            .Select(e => e.NormalizedPath)
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (neighbours.Count == 0)
            return null;

        var sb = new StringBuilder();
        sb.Append("<!-- fuse:next ");
        sb.Append(neighbours.Count);
        sb.Append(neighbours.Count == 1 ? " file was" : " files were");
        sb.Append(" emitted as signatures only (tiered emission). Expand a body with fuse_focus:");
        foreach (var path in neighbours.Take(MaxBreadcrumbEntries))
        {
            var seed = Path.GetFileNameWithoutExtension(path);
            sb.Append("\n  fuse_focus \"").Append(seed).Append("\"   (").Append(path).Append(')');
        }

        if (neighbours.Count > MaxBreadcrumbEntries)
            sb.Append("\n  ... and ").Append(neighbours.Count - MaxBreadcrumbEntries).Append(" more");

        sb.Append(" -->");
        return sb.ToString();
    }

    private async Task<FusionResult> ApplyRedactReportAsync(
        FusionResult emissionResult,
        IReadOnlyList<FusedContent> reducedContent)
    {
        var comment = BuildRedactReportComment(reducedContent);
        if (string.IsNullOrEmpty(comment))
            return emissionResult;

        return await AppendToDiskOrMemoryAsync(emissionResult, comment);
    }

    // Aggregates per-file redaction counts into the redaction-report comment, or an empty string when nothing
    // was redacted. Shared by the appended report and the framing-reserve estimate (C2).
    private static string BuildRedactReportComment(IReadOnlyList<FusedContent> reducedContent)
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
        return summary.ToComment();
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
