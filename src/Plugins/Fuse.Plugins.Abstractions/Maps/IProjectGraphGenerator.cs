namespace Fuse.Plugins.Abstractions.Maps;

/// <summary>
///     Generates a compact solution and project reference graph from .NET project files.
/// </summary>
/// <remarks>
///     Optional capability. When registered, the orchestrator prepends the generated graph to output only
///     for files whose extension is listed in <see cref="SupportedExtensions" />.
/// </remarks>
public interface IProjectGraphGenerator
{
    /// <summary>
    ///     The file extensions this generator can scan, each including the leading dot (for example, <c>.csproj</c>).
    /// </summary>
    IReadOnlyCollection<string> SupportedExtensions { get; }

    /// <summary>
    ///     Builds a compact project dependency table from the supplied file contents.
    /// </summary>
    /// <param name="fileContents">
    ///     Normalized file paths mapped to raw source content. Only entries with a supported extension are scanned.
    /// </param>
    /// <returns>
    ///     A rendered project dependency table ready to prepend to output. Empty when no project references are found.
    /// </returns>
    string Generate(IReadOnlyDictionary<string, string> fileContents);
}
