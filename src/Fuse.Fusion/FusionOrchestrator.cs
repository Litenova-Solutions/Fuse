using Fuse.Analysis.Changes;
using Fuse.Analysis.Dependencies;
using Fuse.Analysis.Patterns;
using Fuse.Collection;
using Fuse.Collection.FileSystem;
using Fuse.Emission;
using Fuse.Emission.Models;
using Fuse.Emission.Writers;
using Fuse.Reduction;
using Fuse.Reduction.Tokenization;
using Microsoft.Extensions.DependencyInjection;

namespace Fuse.Fusion;

/// <summary>
///     Orchestrates the full fusion pipeline: collection, reduction, and emission.
/// </summary>
public sealed class FusionOrchestrator
{
    private readonly IServiceProvider _serviceProvider;
    private readonly FusionValidator _validator;
    private readonly ITokenCounter _tokenCounter;
    private readonly DependencyGraphBuilder _dependencyGraphBuilder;
    private readonly FocusSeedResolver _focusSeedResolver;
    private readonly IChangeDetector _changeDetector;
    private readonly IFileSystem _fileSystem;
    private readonly IEnumerable<IPatternDetector> _patternDetectors;

    /// <summary>
    ///     Initializes a new instance of the <see cref="FusionOrchestrator" /> class.
    /// </summary>
    public FusionOrchestrator(
        IServiceProvider serviceProvider,
        FusionValidator validator,
        ITokenCounter tokenCounter,
        DependencyGraphBuilder dependencyGraphBuilder,
        FocusSeedResolver focusSeedResolver,
        IChangeDetector changeDetector,
        IFileSystem fileSystem,
        IEnumerable<IPatternDetector> patternDetectors)
    {
        _serviceProvider = serviceProvider;
        _validator = validator;
        _tokenCounter = tokenCounter;
        _dependencyGraphBuilder = dependencyGraphBuilder;
        _focusSeedResolver = focusSeedResolver;
        _changeDetector = changeDetector;
        _fileSystem = fileSystem;
        _patternDetectors = patternDetectors;
    }

    /// <summary>
    ///     Executes the full fusion pipeline for the specified request.
    /// </summary>
    public async Task<FusionResult> FuseAsync(FusionRequest request, CancellationToken cancellationToken = default)
    {
        _validator.ValidateOrThrow(request);

        var collectionPipeline = _serviceProvider.GetRequiredService<FileCollectionPipeline>();
        var reductionPipeline = _serviceProvider.GetRequiredService<ContentReductionPipeline>();
        var emissionPipeline = _serviceProvider.GetRequiredService<EmissionPipeline>();

        var collectionResult = await collectionPipeline.CollectAsync(request.Collection, cancellationToken);

        var filesToProcess = await FilterFilesAsync(request, collectionResult.Files, cancellationToken);
        if (filesToProcess is null)
        {
            return CreateEmptyChangeResult(request);
        }

        var reducedContent = await reductionPipeline.ReduceAsync(
            filesToProcess,
            request.Reduction,
            cancellationToken);

        IOutputWriter writer = request.InMemory
            ? new InMemoryOutputWriter(request.Emission, _tokenCounter)
            : new DiskOutputWriter(request.Emission, _tokenCounter);

        FusionResult emissionResult;
        try
        {
            emissionResult = await emissionPipeline.EmitAsync(
                reducedContent,
                request.Emission,
                writer,
                cancellationToken);
        }
        finally
        {
            if (writer is IAsyncDisposable disposable)
            {
                await disposable.DisposeAsync();
            }
        }

        if (!request.Reduction.IncludePatternSummary)
            return emissionResult;

        return await ApplyPatternSummaryAsync(emissionResult, reducedContent, request);
    }

    private async Task<IReadOnlyList<Fuse.Collection.Models.SourceFile>?> FilterFilesAsync(
        FusionRequest request,
        IReadOnlyList<Fuse.Collection.Models.SourceFile> files,
        CancellationToken cancellationToken)
    {
        if (request.Focus is not null)
        {
            return await FilterByFocusAsync(request, files, cancellationToken);
        }

        if (request.Changes is not null)
        {
            return await FilterByChangesAsync(request, files, cancellationToken);
        }

        return files;
    }

    private async Task<IReadOnlyList<Fuse.Collection.Models.SourceFile>?> FilterByFocusAsync(
        FusionRequest request,
        IReadOnlyList<Fuse.Collection.Models.SourceFile> files,
        CancellationToken cancellationToken)
    {
        var extractor = _serviceProvider.GetServices<IDependencyExtractor>().FirstOrDefault();
        if (extractor is null)
            return files;

        var graph = await _dependencyGraphBuilder.BuildAsync(
            files,
            _fileSystem,
            extractor,
            cancellationToken);

        var seedPaths = await _focusSeedResolver.ResolveSeedPathsAsync(
            request.Focus!.Seed,
            files,
            _fileSystem,
            cancellationToken);

        if (seedPaths.Count == 0)
        {
            throw new FusionValidationException(
                $"Focus seed '{request.Focus.Seed}' matched no collected files.");
        }

        var included = _focusSeedResolver.ExpandPaths(graph, seedPaths, request.Focus.Depth);
        return files.Where(f => included.Contains(f.NormalizedRelativePath)).ToArray();
    }

    private async Task<IReadOnlyList<Fuse.Collection.Models.SourceFile>?> FilterByChangesAsync(
        FusionRequest request,
        IReadOnlyList<Fuse.Collection.Models.SourceFile> files,
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

        if (request.Changes.IncludeDependents && matchedPaths.Count > 0)
        {
            var extractor = _serviceProvider.GetServices<IDependencyExtractor>().FirstOrDefault();
            if (extractor is not null)
            {
                var graph = await _dependencyGraphBuilder.BuildAsync(
                    files,
                    _fileSystem,
                    extractor,
                    cancellationToken);

                matchedPaths = _focusSeedResolver.ExpandPaths(graph, matchedPaths, depth: 1);
            }
        }

        if (matchedPaths.Count == 0)
            return null;

        return files.Where(f => matchedPaths.Contains(f.NormalizedRelativePath)).ToArray();
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

    private async Task<FusionResult> ApplyPatternSummaryAsync(
        FusionResult emissionResult,
        IReadOnlyList<Fuse.Reduction.Models.FusedContent> reducedContent,
        FusionRequest request)
    {
        var patterns = _patternDetectors
            .Select(d => d.Detect(reducedContent))
            .Where(p => p is not null)
            .Cast<DetectedPattern>()
            .ToArray();

        if (patterns.Length == 0)
            return emissionResult;

        var summary = new PatternSummary(patterns);
        var comment = summary.ToComment();

        var inMemoryContent = emissionResult.InMemoryContent;
        if (!string.IsNullOrEmpty(inMemoryContent))
            inMemoryContent += "\n" + comment;

        var generatedPaths = emissionResult.GeneratedPaths.ToList();
        if (generatedPaths.Count > 0)
        {
            var lastPath = generatedPaths[^1];
            var existing = await _fileSystem.ReadAllTextAsync(lastPath);
            await File.WriteAllTextAsync(lastPath, existing + "\n" + comment);
        }

        return new FusionResult(
            generatedPaths,
            inMemoryContent,
            emissionResult.TotalTokens,
            emissionResult.ProcessedFileCount,
            emissionResult.TotalFileCount,
            emissionResult.Duration,
            emissionResult.TopTokenFiles,
            summary);
    }
}
