using System.Text.RegularExpressions;

namespace Fuse.Analysis.Dependencies;

/// <summary>
///     Extracts referenced type names from C# source content. Produces a best-effort approximation;
///     may miss dynamically dispatched dependencies or produce false positives from type names in comments.
/// </summary>
public interface IDependencyExtractor
{
    /// <summary>
    ///     Gets the file extension this extractor handles, including the leading dot.
    /// </summary>
    string Extension { get; }

    /// <summary>
    ///     Extracts referenced type names from the content.
    /// </summary>
    IReadOnlyList<string> ExtractReferencedTypes(string content);
}
