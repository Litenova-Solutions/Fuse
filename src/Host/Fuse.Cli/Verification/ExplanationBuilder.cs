using Fuse.Emission.Models;

namespace Fuse.Cli.Verification;

/// <summary>
///     Builds the human-readable lines for the <c>fuse explain</c> dry run: which files are included, which
///     are excluded, and the estimated token total.
/// </summary>
public static class ExplanationBuilder
{
    /// <summary>
    ///     Builds the explanation lines.
    /// </summary>
    /// <param name="scope">A short description of the active scope.</param>
    /// <param name="tokenizer">The tokenizer model used for the estimate.</param>
    /// <param name="included">The included files with their token counts.</param>
    /// <param name="collectedPaths">Every candidate path from collection, used to derive the excluded set.</param>
    /// <returns>The lines to print, in order.</returns>
    public static IReadOnlyList<string> Build(
        string scope,
        string tokenizer,
        IReadOnlyList<FileTokenInfo> included,
        IEnumerable<string> collectedPaths)
    {
        var includedOrdered = included
            .OrderByDescending(f => f.Count)
            .ToList();
        var includedPaths = includedOrdered
            .Select(f => f.Path)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var excluded = collectedPaths
            .Where(p => !includedPaths.Contains(p))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var totalTokens = includedOrdered.Sum(f => f.Count);

        var lines = new List<string>
        {
            $"Scope: {scope}",
            $"Tokenizer: {tokenizer}",
            $"Included: {includedOrdered.Count} files, ~{totalTokens} tokens",
        };

        foreach (var file in includedOrdered)
            lines.Add($"  + {file.Path} (~{file.Count})");

        lines.Add($"Excluded: {excluded.Count} files");
        foreach (var path in excluded)
            lines.Add($"  - {path}");

        return lines;
    }
}
