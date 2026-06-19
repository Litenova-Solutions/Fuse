using Fuse.Plugins.Abstractions;
using Fuse.Plugins.Abstractions.Dependencies;
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
    /// <param name="typeLocators">Registry of per-extension locators used to resolve a type-name seed to its defining files.</param>
    public FocusSeedResolver(CapabilityRegistry<ITypeNameLocator> typeLocators)
    {
        _typeLocators = typeLocators;
    }

    /// <summary>
    ///     Resolves a seed string to matching file paths using path, filename, type name, and directory
    ///     prefix strategies.
    /// </summary>
    /// <param name="seed">The seed to resolve: a relative path, filename, type name, or directory prefix.</param>
    /// <param name="files">The candidate source files to match against.</param>
    /// <param name="contentProvider">Provider used to read file content when resolving a type-name seed.</param>
    /// <param name="cancellationToken">Token used to cancel content reads.</param>
    /// <returns>
    ///     The awaited result is the set of normalized relative paths matched by the seed, using a
    ///     case-insensitive comparer. Empty when nothing matches.
    /// </returns>
    /// <remarks>
    ///     Strategies are tried in order and the first that yields any match wins: exact path, then exact
    ///     filename, then files defining a type of that name, then directory prefix. Only the type-name
    ///     strategy reads file content.
    /// </remarks>
    /// <exception cref="OperationCanceledException">Thrown when <paramref name="cancellationToken" /> is cancelled.</exception>
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
    ///     Expands seed paths by breadth-first traversal up to the specified depth using the dependency graph.
    /// </summary>
    /// <param name="graph">The dependency graph used to follow references from each file.</param>
    /// <param name="seedPaths">The starting set of normalized relative paths.</param>
    /// <param name="depth">The maximum number of hops to traverse from any seed; <c>0</c> returns only the seeds.</param>
    /// <returns>
    ///     The included paths plus a provenance chain for each path recording the hop sequence from a seed
    ///     to its inclusion (inclusive). See <see cref="PathExpansionResult" />.
    /// </returns>
    /// <remarks>
    ///     Traversal follows the best-effort references in <see cref="DependencyGraph.FileReferences" />, so
    ///     the expansion inherits the graph's false-positive and missed-edge characteristics. Each path is
    ///     included once, on the shortest hop on which it is first reached.
    /// </remarks>
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
