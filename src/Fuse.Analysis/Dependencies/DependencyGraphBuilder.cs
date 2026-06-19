using System.Collections.Concurrent;
using Fuse.Languages.Abstractions;
using Fuse.Languages.Abstractions.Dependencies;
using Fuse.Collection.FileSystem;
using Fuse.Collection.Models;

namespace Fuse.Analysis.Dependencies;

/// <summary>
///     Builds a dependency graph from collected source files. Produces a best-effort approximation;
///     may miss dynamically dispatched dependencies or produce false positives from type names in comments.
/// </summary>
public sealed class DependencyGraphBuilder
{
    /// <summary>
    ///     Builds a dependency graph by reading each file and extracting referenced types.
    /// </summary>
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
    ///     Builds a dependency graph by reading each file and extracting referenced types.
    /// </summary>
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
