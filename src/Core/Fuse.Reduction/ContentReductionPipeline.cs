using System.Collections.Concurrent;
using System.Text;
using Fuse.Collection.FileSystem;
using Fuse.Collection.Models;
using Fuse.Plugins.Abstractions;
using Fuse.Plugins.Abstractions.Markers;
using Fuse.Plugins.Abstractions.Options;
using Fuse.Plugins.Abstractions.Reducers;
using Fuse.Plugins.Abstractions.Skeleton;
using Fuse.Reduction.Caching;
using Fuse.Reduction.Models;
using Fuse.Reduction.Security;
using Fuse.Reduction.Tokenization;

namespace Fuse.Reduction;

/// <summary>
///     Reads source files, normalizes whitespace, applies extension-specific reducers,
///     and returns non-trivial fused content for emission.
/// </summary>
/// <remarks>
///     Each file passes through a fixed stage order: whitespace normalization, format reduction (gated by
///     <see cref="ReductionGate" /> and the resolved <see cref="IContentReducer" />), skeleton extraction,
///     semantic-marker prepending, and finally secret redaction when enabled. Stage behavior is delegated
///     to the registered capability plugins; this type only sequences them. Files are processed in
///     parallel but results are re-sorted into the input order, and content judged trivial by
///     <see cref="FusedContent.IsTrivial" /> is dropped. When a cache is supplied, the reducer stages are
///     skipped on a cache hit, but redaction still runs on the cached content.
/// </remarks>
public sealed class ContentReductionPipeline
{
    private readonly ISourceContentProvider _contentProvider;
    private readonly CapabilityRegistry<IContentReducer> _reducers;
    private readonly CapabilityRegistry<ISkeletonExtractor> _skeletonExtractors;
    private readonly CapabilityRegistry<ISemanticMarkerGenerator> _semanticMarkerGenerators;
    private readonly ITokenCounter _tokenCounter;
    private readonly ISecretRedactor _secretRedactor;

    /// <summary>
    ///     Initializes a new instance of the <see cref="ContentReductionPipeline" /> class.
    /// </summary>
    /// <param name="contentProvider">Provider used to read raw file content.</param>
    /// <param name="reducers">Registry of per-extension format reducers.</param>
    /// <param name="skeletonExtractors">Registry of per-extension skeleton extractors.</param>
    /// <param name="semanticMarkerGenerators">Registry of per-extension semantic-marker generators.</param>
    /// <param name="tokenCounter">Default token counter used to populate <see cref="FusedContent.TokenCount" />.</param>
    /// <param name="secretRedactor">Redactor applied to content when redaction is enabled.</param>
    public ContentReductionPipeline(
        ISourceContentProvider contentProvider,
        CapabilityRegistry<IContentReducer> reducers,
        CapabilityRegistry<ISkeletonExtractor> skeletonExtractors,
        CapabilityRegistry<ISemanticMarkerGenerator> semanticMarkerGenerators,
        ITokenCounter tokenCounter,
        ISecretRedactor secretRedactor)
    {
        _contentProvider = contentProvider;
        _reducers = reducers;
        _skeletonExtractors = skeletonExtractors;
        _semanticMarkerGenerators = semanticMarkerGenerators;
        _tokenCounter = tokenCounter;
        _secretRedactor = secretRedactor;
    }

    /// <summary>
    ///     Reduces the supplied source files and returns non-trivial fused content, using a default
    ///     parallelism of <see cref="Environment.ProcessorCount" /> and no cache.
    /// </summary>
    /// <param name="sourceFiles">The source files to reduce.</param>
    /// <param name="options">Options controlling which reduction stages run.</param>
    /// <param name="cancellationToken">Token used to cancel reads and reduction.</param>
    /// <returns>
    ///     The awaited result is the reduced content for each input file, in input order, with files whose
    ///     reduced content is trivial omitted.
    /// </returns>
    public Task<IReadOnlyList<FusedContent>> ReduceAsync(
        IReadOnlyList<SourceFile> sourceFiles,
        ReductionOptions options,
        CancellationToken cancellationToken = default) =>
        ReduceAsync(
            sourceFiles,
            options,
            Environment.ProcessorCount,
            null,
            tokenCounterOverride: null,
            cancellationToken);

    /// <summary>
    ///     Reduces the supplied source files and returns non-trivial fused content at the specified degree
    ///     of parallelism, optionally reusing a reduction cache.
    /// </summary>
    /// <param name="sourceFiles">The source files to reduce.</param>
    /// <param name="options">Options controlling which reduction stages run.</param>
    /// <param name="parallelism">Maximum number of files reduced concurrently.</param>
    /// <param name="reductionCache">Cache consulted before reducing and populated on a miss; <c>null</c> disables caching.</param>
    /// <param name="tokenCounterOverride">Token counter to use instead of the injected default; <c>null</c> uses the default.</param>
    /// <param name="cancellationToken">Token used to cancel reads and reduction.</param>
    /// <returns>
    ///     The awaited result is the reduced content for each input file, in input order, with files whose
    ///     reduced content is trivial omitted.
    /// </returns>
    public async Task<IReadOnlyList<FusedContent>> ReduceAsync(
        IReadOnlyList<SourceFile> sourceFiles,
        ReductionOptions options,
        int parallelism,
        IReductionCache? reductionCache,
        ITokenCounter? tokenCounterOverride = null,
        CancellationToken cancellationToken = default)
    {
        var tokenCounter = tokenCounterOverride ?? _tokenCounter;
        var indexedResults = new ConcurrentBag<(int Index, FusedContent? Item)>();
        var parallelOptions = new ParallelOptions
        {
            MaxDegreeOfParallelism = parallelism,
            CancellationToken = cancellationToken
        };

        await Parallel.ForEachAsync(
            sourceFiles.Select((file, index) => (file, index)),
            parallelOptions,
            async (item, ct) =>
            {
                var rawContent = await _contentProvider.GetContentAsync(item.file, ct);
                string content;

                if (reductionCache is not null)
                {
                    var contentHash = ReductionHasher.HashContent(rawContent);
                    var optionsHash = ReductionHasher.HashReductionOptions(item.file.Extension, options);

                    if (reductionCache.TryGet(contentHash, optionsHash, out var cachedContent))
                    {
                        content = cachedContent;
                    }
                    else
                    {
                        content = ReduceContent(rawContent, item.file.Extension, options);
                        reductionCache.Set(contentHash, optionsHash, content);
                    }
                }
                else
                {
                    content = ReduceContent(rawContent, item.file.Extension, options);
                }

                IReadOnlyDictionary<string, int>? redactionCounts = null;
                if (options.EnableRedaction)
                {
                    var redaction = _secretRedactor.Redact(content);
                    content = redaction.Content;
                    if (redaction.TotalCount > 0)
                        redactionCounts = redaction.CountsByKind;
                }

                var fused = new FusedContent(item.file, content, tokenCounter, redactionCounts);
                indexedResults.Add((item.index, fused.IsTrivial ? null : fused));
            });

        return indexedResults
            .OrderBy(r => r.Index)
            .Select(r => r.Item)
            .Where(f => f is not null)
            .Cast<FusedContent>()
            .ToArray();
    }

    private string ReduceContent(string rawContent, string extension, ReductionOptions options)
    {
        var content = rawContent;
        content = NormalizeWhitespace(content, options);
        content = ApplyReduction(content, extension, options);
        content = ApplySkeleton(content, extension, options);
        content = ApplySemanticMarkers(content, extension, options);
        return content;
    }

    private string ApplySkeleton(string content, string extension, ReductionOptions options)
    {
        if (!options.SkeletonMode && !options.PublicApiMode)
            return content;

        var extractor = _skeletonExtractors.TryResolve(extension);
        return extractor?.ExtractSkeleton(content, options.PublicApiMode) ?? content;
    }

    private string ApplySemanticMarkers(string content, string extension, ReductionOptions options)
    {
        if (!options.IncludeSemanticMarkers)
            return content;

        var generator = _semanticMarkerGenerators.TryResolve(extension);
        if (generator is null)
            return content;

        var markers = generator.GenerateMarkers(content);
        if (markers.Count == 0)
            return content;

        var sb = new StringBuilder();
        foreach (var marker in markers)
            sb.AppendLine(marker.ToComment());
        sb.Append(content);
        return sb.ToString();
    }

    private static string NormalizeWhitespace(string content, ReductionOptions options)
    {
        if (options.TrimContent)
        {
            content = System.Text.RegularExpressions.Regex.Replace(content, @"^[\s\t]+|[\s\t]+$", string.Empty,
                System.Text.RegularExpressions.RegexOptions.Multiline);
        }

        if (options.UseCondensing)
        {
            content = System.Text.RegularExpressions.Regex.Replace(content, @"^\s*$\r?\n", string.Empty,
                System.Text.RegularExpressions.RegexOptions.Multiline);
        }

        return content;
    }

    private string ApplyReduction(string content, string extension, ReductionOptions options)
    {
        var reducer = _reducers.TryResolve(extension);
        if (!ReductionGate.ShouldReduce(extension, options, reducer is not null))
            return content;

        return reducer!.Reduce(content, options);
    }
}
