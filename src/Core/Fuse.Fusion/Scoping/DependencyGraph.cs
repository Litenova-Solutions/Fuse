namespace Fuse.Fusion.Scoping;

/// <summary>
///     A dependency graph mapping file paths to referenced type names, with the reverse edges and declared
///     types needed to find a file's dependents.
/// </summary>
/// <remarks>
///     This graph is a best-effort approximation and may miss dynamically dispatched dependencies. Comments
///     and string literals are blanked by the language extractor before edges are derived, so type names in
///     prose do not create false edges. Forward edges (<see cref="FileReferences" /> joined through
///     <see cref="TypeIndex" />) reach the files a seed depends on; reverse edges
///     (<see cref="DeclaredTypes" /> joined through <see cref="TypeReferences" />) reach the files that depend
///     on a seed.
/// </remarks>
public sealed class DependencyGraph
{
    /// <summary>
    ///     Initializes a new instance of the <see cref="DependencyGraph" /> class. Normally produced by
    ///     <see cref="DependencyGraphBuilder" />; exposed for custom scoping and testing.
    /// </summary>
    /// <param name="fileReferences">Map of file paths to the type names each file references.</param>
    /// <param name="typeIndex">Map of type names to the files that define them.</param>
    /// <param name="declaredTypes">Map of file paths to the type names each file declares.</param>
    /// <param name="typeReferences">Map of type names to the files that reference them.</param>
    public DependencyGraph(
        IReadOnlyDictionary<string, IReadOnlyList<string>> fileReferences,
        IReadOnlyDictionary<string, IReadOnlyList<string>> typeIndex,
        IReadOnlyDictionary<string, IReadOnlyList<string>> declaredTypes,
        IReadOnlyDictionary<string, IReadOnlyList<string>> typeReferences)
    {
        FileReferences = fileReferences;
        TypeIndex = typeIndex;
        DeclaredTypes = declaredTypes;
        TypeReferences = typeReferences;
    }

    /// <summary>
    ///     Map of normalized relative file paths to the type names each file references.
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> FileReferences { get; }

    /// <summary>
    ///     Map of type names to the normalized relative paths of files that define them.
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> TypeIndex { get; }

    /// <summary>
    ///     Map of normalized relative file paths to the type names each file declares.
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> DeclaredTypes { get; }

    /// <summary>
    ///     Map of type names to the normalized relative paths of files that reference them. This is the
    ///     reverse of <see cref="FileReferences" /> and is used to find a file's dependents.
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> TypeReferences { get; }
}
