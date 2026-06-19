namespace Fuse.Analysis.Dependencies;

/// <summary>
///     A dependency graph mapping file paths to referenced type names.
/// </summary>
/// <remarks>
///     This graph is a best-effort approximation and may miss dynamically dispatched dependencies
///     or produce false positives from type names in comments.
/// </remarks>
public sealed class DependencyGraph
{
    internal DependencyGraph(
        IReadOnlyDictionary<string, IReadOnlyList<string>> fileReferences,
        IReadOnlyDictionary<string, IReadOnlyList<string>> typeIndex)
    {
        FileReferences = fileReferences;
        TypeIndex = typeIndex;
    }

    /// <summary>
    ///     Map of normalized relative file paths to the type names each file references.
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> FileReferences { get; }

    /// <summary>
    ///     Map of type names to the normalized relative paths of files that define them.
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> TypeIndex { get; }
}
