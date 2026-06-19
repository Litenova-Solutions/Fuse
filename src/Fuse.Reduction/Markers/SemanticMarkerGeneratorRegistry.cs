namespace Fuse.Reduction.Markers;

/// <summary>
///     Resolves semantic marker generators by file extension.
/// </summary>
public sealed class SemanticMarkerGeneratorRegistry
{
    private readonly Dictionary<string, ISemanticMarkerGenerator> _generators;

    /// <summary>
    ///     Initializes a new instance of the <see cref="SemanticMarkerGeneratorRegistry" /> class.
    /// </summary>
    /// <param name="generators">The registered semantic marker generators.</param>
    public SemanticMarkerGeneratorRegistry(IEnumerable<ISemanticMarkerGenerator> generators)
    {
        _generators = generators.ToDictionary(
            generator => generator.Extension,
            StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    ///     Attempts to resolve a semantic marker generator for the specified file extension.
    /// </summary>
    /// <param name="extension">The file extension, including the leading dot.</param>
    /// <returns>The matching generator, or <c>null</c> when none is registered.</returns>
    public ISemanticMarkerGenerator? TryGetGenerator(string extension) =>
        _generators.GetValueOrDefault(extension);
}
