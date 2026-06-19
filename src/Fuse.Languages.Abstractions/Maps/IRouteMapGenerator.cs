namespace Fuse.Languages.Abstractions.Maps;

/// <summary>
///     Generates a compact HTTP route map from ASP.NET source files.
/// </summary>
public interface IRouteMapGenerator
{
    /// <summary>
    ///     Gets file extensions this generator can scan.
    /// </summary>
    IReadOnlyCollection<string> SupportedExtensions { get; }

    /// <summary>
    ///     Builds a compact verb/path/handler table from the supplied file contents.
    /// </summary>
    /// <param name="fileContents">File paths mapped to raw source content.</param>
    string Generate(IReadOnlyDictionary<string, string> fileContents);
}
