namespace Fuse.Plugins.Abstractions.Maps;

/// <summary>
///     Generates a compact HTTP route map from ASP.NET source files.
/// </summary>
/// <remarks>
///     Optional capability. When registered, the orchestrator prepends the generated map to output only for
///     files whose extension is listed in <see cref="SupportedExtensions" />.
/// </remarks>
public interface IRouteMapGenerator
{
    /// <summary>
    ///     The file extensions this generator can scan, each including the leading dot (for example, <c>.cs</c>).
    /// </summary>
    IReadOnlyCollection<string> SupportedExtensions { get; }

    /// <summary>
    ///     Builds a compact verb/path/handler table from the supplied file contents.
    /// </summary>
    /// <param name="fileContents">
    ///     Normalized file paths mapped to raw source content. Only entries with a supported extension are scanned.
    /// </param>
    /// <returns>
    ///     A rendered table of HTTP verb, path, and handler rows ready to prepend to output. Empty when no routes
    ///     are found.
    /// </returns>
    string Generate(IReadOnlyDictionary<string, string> fileContents);
}
