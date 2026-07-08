using Fuse.Indexing;
using Fuse.Semantics;

namespace Fuse.Retrieval;

/// <summary>
///     The base and current content of one changed file, for the T2 public-API delta. Either side is <c>null</c>
///     when the file did not exist there: a file added on the current side has no base content (contributes only
///     additions), and a file deleted has no current content (contributes only removals).
/// </summary>
/// <param name="Path">The forward-slash relative path of the changed file.</param>
/// <param name="BaseContent">The file's content at the base ref, or <c>null</c> when absent there.</param>
/// <param name="CurrentContent">The file's content in the working tree, or <c>null</c> when deleted.</param>
public sealed record ChangedFileContent(string Path, string? BaseContent, string? CurrentContent);

/// <summary>
///     Computes the aggregate public-API delta across a set of changed files from their base and current content
///     (T2). Each file's public and protected symbols are extracted by syntax analysis on both sides - the base
///     content read from the git base ref, the current content from the working tree - so the surface is compared
///     symmetrically without loading either full checkout. Only C# files participate; non-C# changes contribute
///     nothing to the API surface.
/// </summary>
/// <remarks>
///     Extraction is syntax-only (<see cref="SyntaxSymbolExtractor" />), so this is the graph-grade path that works
///     from any checkout state; a resident workspace can confirm the same surface compilation-grade. Both sides run
///     through the identical extractor, so an extraction quirk cancels out rather than showing as a phantom delta.
/// </remarks>
public static class ChangedFileApiDelta
{
    /// <summary>
    ///     Computes the public-API delta across the changed files.
    /// </summary>
    /// <param name="files">The changed files with their base and current content.</param>
    /// <returns>The aggregate delta: breaking changes first, then additive; empty when no public surface changed.</returns>
    public static PublicApiDeltaResult Compute(IReadOnlyList<ChangedFileContent> files)
    {
        var baseSymbols = new List<SymbolRecord>();
        var currentSymbols = new List<SymbolRecord>();

        foreach (var file in files)
        {
            if (!IsCSharp(file.Path))
                continue;

            if (!string.IsNullOrEmpty(file.BaseContent))
                baseSymbols.AddRange(PublicSurfaceExtractor.Extract(file.Path, file.BaseContent));
            if (!string.IsNullOrEmpty(file.CurrentContent))
                currentSymbols.AddRange(PublicSurfaceExtractor.Extract(file.Path, file.CurrentContent));
        }

        return PublicApiDelta.Compute(baseSymbols, currentSymbols);
    }

    private static bool IsCSharp(string path) => path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase);
}
