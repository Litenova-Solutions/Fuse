using Fuse.Languages.Abstractions;
using Fuse.Languages.Abstractions.Dependencies;
using Fuse.Collection.FileSystem;
using Fuse.Collection.Models;

namespace Fuse.Analysis.Dependencies;

/// <summary>
///     Resolves focus seeds to file paths and expands dependency scopes.
/// </summary>
public sealed class FocusSeedResolver
{
    private readonly CapabilityRegistry<ITypeNameLocator> _typeLocators;

    /// <summary>
    ///     Initializes a new instance of the <see cref="FocusSeedResolver" /> class.
    /// </summary>
    public FocusSeedResolver(CapabilityRegistry<ITypeNameLocator> typeLocators)
    {
        _typeLocators = typeLocators;
    }

    /// <summary>
    ///     Resolves a seed string to matching file paths using path, filename, type name, and directory prefix strategies.
    /// </summary>
    public async Task<HashSet<string>> ResolveSeedPathsAsync(
        string seed,
        IReadOnlyList<SourceFile> files,
        ISourceContentProvider contentProvider,
        CancellationToken cancellationToken = default)
    {
        var normalizedSeed = seed.Replace('\\', '/').Trim('/');
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var file in files)
        {
            if (string.Equals(file.NormalizedRelativePath, normalizedSeed, StringComparison.OrdinalIgnoreCase))
                result.Add(file.NormalizedRelativePath);
        }

        if (result.Count > 0)
            return result;

        foreach (var file in files)
        {
            if (string.Equals(Path.GetFileName(file.NormalizedRelativePath), normalizedSeed, StringComparison.OrdinalIgnoreCase))
                result.Add(file.NormalizedRelativePath);
        }

        if (result.Count > 0)
            return result;

        foreach (var file in files)
        {
            var locator = _typeLocators.TryResolve(file.Extension);
            if (locator is null)
                continue;

            cancellationToken.ThrowIfCancellationRequested();
            var content = await contentProvider.GetContentAsync(file, cancellationToken);
            if (locator.ContainsTypeDefinition(content, normalizedSeed))
                result.Add(file.NormalizedRelativePath);
        }

        if (result.Count > 0)
            return result;

        var prefix = normalizedSeed.EndsWith('/') ? normalizedSeed : normalizedSeed + "/";
        foreach (var file in files)
        {
            if (file.NormalizedRelativePath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                result.Add(file.NormalizedRelativePath);
        }

        return result;
    }

    /// <summary>
    ///     Expands seed paths by BFS up to the specified depth using the dependency graph.
    /// </summary>
    public PathExpansionResult ExpandPaths(DependencyGraph graph, HashSet<string> seedPaths, int depth)
    {
        var included = new HashSet<string>(seedPaths, StringComparer.OrdinalIgnoreCase);
        var frontier = new HashSet<string>(seedPaths, StringComparer.OrdinalIgnoreCase);
        var provenance = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);

        foreach (var seed in seedPaths)
            provenance[seed] = [seed];

        for (var hop = 0; hop < depth; hop++)
        {
            var nextFrontier = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var path in frontier)
            {
                if (!graph.FileReferences.TryGetValue(path, out var referencedTypes))
                    continue;

                if (!provenance.TryGetValue(path, out var parentChain))
                    parentChain = [path];

                foreach (var typeName in referencedTypes)
                {
                    if (!graph.TypeIndex.TryGetValue(typeName, out var definingPaths))
                        continue;

                    foreach (var definingPath in definingPaths)
                    {
                        if (!included.Add(definingPath))
                            continue;

                        var chain = new List<string>(parentChain) { definingPath };
                        provenance[definingPath] = chain;
                        nextFrontier.Add(definingPath);
                    }
                }
            }

            frontier = nextFrontier;
            if (frontier.Count == 0)
                break;
        }

        return new PathExpansionResult(included, provenance);
    }
}
