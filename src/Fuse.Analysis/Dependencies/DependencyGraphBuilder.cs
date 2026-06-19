using System.Text.RegularExpressions;
using Fuse.Collection.FileSystem;
using Fuse.Collection.Models;

namespace Fuse.Analysis.Dependencies;

/// <summary>
///     Builds a dependency graph from collected source files. Produces a best-effort approximation;
///     may miss dynamically dispatched dependencies or produce false positives from type names in comments.
/// </summary>
public sealed class DependencyGraphBuilder
{
    private static readonly Regex TypeDefinitionRegex = new(
        @"\b(class|interface|record|struct|enum)\s+(\w+)\b",
        RegexOptions.Compiled);

    /// <summary>
    ///     Builds a dependency graph by reading each file and extracting referenced types.
    /// </summary>
    public async Task<DependencyGraph> BuildAsync(
        IReadOnlyList<SourceFile> files,
        IFileSystem fileSystem,
        IDependencyExtractor extractor,
        CancellationToken cancellationToken = default)
    {
        var fileReferences = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        var typeIndex = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);

        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!string.Equals(file.Extension, extractor.Extension, StringComparison.OrdinalIgnoreCase))
            {
                fileReferences[file.NormalizedRelativePath] = [];
                continue;
            }

            var content = await fileSystem.ReadAllTextAsync(file.FullPath, cancellationToken);
            fileReferences[file.NormalizedRelativePath] = extractor.ExtractReferencedTypes(content);

            foreach (Match match in TypeDefinitionRegex.Matches(content))
            {
                var typeName = match.Groups[2].Value;
                if (!typeIndex.TryGetValue(typeName, out var paths))
                {
                    paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    typeIndex[typeName] = paths;
                }

                paths.Add(file.NormalizedRelativePath);
            }
        }

        var readOnlyTypeIndex = typeIndex.ToDictionary(
            kvp => kvp.Key,
            kvp => (IReadOnlyList<string>)kvp.Value.ToArray(),
            StringComparer.Ordinal);

        return new DependencyGraph(fileReferences, readOnlyTypeIndex);
    }
}
