namespace Fuse.Languages.Abstractions.Maps;

/// <summary>
///     Generates a solution and project reference graph from .NET project files.
/// </summary>
public interface IProjectGraphGenerator
{
    /// <summary>
    ///     Gets file extensions this generator can scan.
    /// </summary>
    IReadOnlyCollection<string> SupportedExtensions { get; }

    /// <summary>
    ///     Builds a compact project dependency table from the supplied file contents.
    /// </summary>
    /// <param name="fileContents">File paths mapped to raw source content.</param>
    string Generate(IReadOnlyDictionary<string, string> fileContents);
}
