using System.Collections.Concurrent;
using Fuse.Plugins.Abstractions;
using Fuse.Plugins.Abstractions.Dependencies;
using Fuse.Collection.FileSystem;
using Fuse.Collection.Models;

namespace Fuse.Fusion.Scoping;

/// <summary>
///     Builds a <see cref="DependencyGraph" /> from collected source files using per-language dependency
///     extractors and type-name locators.
/// </summary>
/// <remarks>
///     Files are read and analyzed in parallel; the resulting <see cref="DependencyGraph.FileReferences" />
///     map is reordered to follow the input <c>files</c> order, so output ordering is deterministic
///     regardless of parallelism. The graph is a best-effort approximation: it may miss dynamically
///     dispatched dependencies and may produce false positives from type names appearing in comments or
///     strings. Files whose extension has no registered extractor contribute an empty reference list.
/// </remarks>
public sealed class DependencyGraphBuilder
{
    /// <summary>
    ///     Builds a dependency graph by reading each file and extracting referenced types, using a default
    ///     parallelism of <see cref="Environment.ProcessorCount" />.
    /// </summary>
    /// <param name="files">The source files to analyze.</param>
    /// <param name="contentProvider">Provider used to read each file's content.</param>
    /// <param name="extractors">Registry of per-extension extractors that find referenced type names.</param>
    /// <param name="typeLocators">Registry of per-extension locators that find defined type names.</param>
    /// <param name="cancellationToken">Token used to cancel reads and analysis.</param>
    /// <returns>The awaited result is the populated dependency graph.</returns>
    public Task<DependencyGraph> BuildAsync(
        IReadOnlyList<SourceFile> files,
        ISourceContentProvider contentProvider,
        CapabilityRegistry<IDependencyExtractor> extractors,
        CapabilityRegistry<ITypeNameLocator> typeLocators,
        CancellationToken cancellationToken = default) =>
        BuildAsync(
            files,
            contentProvider,
            extractors,
            typeLocators,
            Environment.ProcessorCount,
            cancellationToken);

    /// <summary>
    ///     Builds a dependency graph by reading each file and extracting referenced types at the specified
    ///     degree of parallelism.
    /// </summary>
    /// <param name="files">The source files to analyze.</param>
    /// <param name="contentProvider">Provider used to read each file's content.</param>
    /// <param name="extractors">Registry of per-extension extractors that find referenced type names.</param>
    /// <param name="typeLocators">Registry of per-extension locators that find defined type names.</param>
    /// <param name="parallelism">Maximum number of files analyzed concurrently.</param>
    /// <param name="cancellationToken">Token used to cancel reads and analysis.</param>
    /// <returns>The awaited result is the populated dependency graph.</returns>
    public async Task<DependencyGraph> BuildAsync(
        IReadOnlyList<SourceFile> files,
        ISourceContentProvider contentProvider,
        CapabilityRegistry<IDependencyExtractor> extractors,
        CapabilityRegistry<ITypeNameLocator> typeLocators,
        int parallelism,
        CancellationToken cancellationToken = default)
    {
        var fileReferences = new ConcurrentDictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        var typeIndex = new ConcurrentDictionary<string, HashSet<string>>(StringComparer.Ordinal);

        var parallelOptions = new ParallelOptions
        {
            MaxDegreeOfParallelism = parallelism,
            CancellationToken = cancellationToken
        };

        await Parallel.ForEachAsync(files, parallelOptions, async (file, ct) =>
        {
            var extractor = extractors.TryResolve(file.Extension);
            if (extractor is null)
            {
                fileReferences[file.NormalizedRelativePath] = [];
                return;
            }

            var content = await contentProvider.GetContentAsync(file, ct);
            fileReferences[file.NormalizedRelativePath] = extractor.ExtractReferencedTypes(content);

            var locator = typeLocators.TryResolve(file.Extension);
            if (locator is null)
                return;

            foreach (var typeName in locator.ExtractDefinedTypes(content))
            {
                var paths = typeIndex.GetOrAdd(typeName, _ => new HashSet<string>(StringComparer.OrdinalIgnoreCase));
                lock (paths)
                {
                    paths.Add(file.NormalizedRelativePath);
                }
            }
        });

        var orderedReferences = files
            .Select(f => f.NormalizedRelativePath)
            .Where(fileReferences.ContainsKey)
            .ToDictionary(
                path => path,
                path => fileReferences[path],
                StringComparer.OrdinalIgnoreCase);

        var readOnlyTypeIndex = typeIndex.ToDictionary(
            kvp => kvp.Key,
            kvp => (IReadOnlyList<string>)kvp.Value.OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToArray(),
            StringComparer.Ordinal);

        return new DependencyGraph(orderedReferences, readOnlyTypeIndex);
    }
}
