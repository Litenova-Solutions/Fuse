using System.Text.RegularExpressions;
using Fuse.Collection.FileSystem;
using Fuse.Collection.Models;

namespace Fuse.Analysis.Dependencies;

/// <summary>
///     Resolves focus seeds to file paths and expands dependency scopes.
/// </summary>
public sealed class FocusSeedResolver
{
    /// <summary>
    ///     Resolves a seed string to matching file paths using path, filename, type name, and directory prefix strategies.
    /// </summary>
    public async Task<HashSet<string>> ResolveSeedPathsAsync(
        string seed,
        IReadOnlyList<SourceFile> files,
        IFileSystem fileSystem,
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

        foreach (var file in files.Where(f => f.IsCSharp))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var content = await fileSystem.ReadAllTextAsync(file.FullPath, cancellationToken);
            if (Regex.IsMatch(content, $@"\b(class|interface|record|struct)\s+{Regex.Escape(normalizedSeed)}\b"))
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
    public HashSet<string> ExpandPaths(DependencyGraph graph, HashSet<string> seedPaths, int depth)
    {
        var included = new HashSet<string>(seedPaths, StringComparer.OrdinalIgnoreCase);
        var frontier = new HashSet<string>(seedPaths, StringComparer.OrdinalIgnoreCase);

        for (var hop = 0; hop < depth; hop++)
        {
            var nextFrontier = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var path in frontier)
            {
                if (!graph.FileReferences.TryGetValue(path, out var referencedTypes))
                    continue;

                foreach (var typeName in referencedTypes)
                {
                    if (!graph.TypeIndex.TryGetValue(typeName, out var definingPaths))
                        continue;

                    foreach (var definingPath in definingPaths)
                    {
                        if (included.Add(definingPath))
                            nextFrontier.Add(definingPath);
                    }
                }
            }

            frontier = nextFrontier;
            if (frontier.Count == 0)
                break;
        }

        return included;
    }
}
