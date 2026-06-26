namespace Fuse.Context;

/// <summary>
///     Formats a file's provenance chain (the reasons and edges that included it) for the manifest and for the
///     per-file comment attached to each rendered entry.
/// </summary>
public static class ProvenanceFormatter
{
    /// <summary>
    ///     Summarizes a provenance chain onto one line for the manifest impact list.
    /// </summary>
    /// <param name="provenance">The provenance chain.</param>
    /// <returns>A single-line summary, or "seed" when the chain has no edges.</returns>
    public static string Summarize(IReadOnlyList<string> provenance)
    {
        var edges = provenance
            .Where(p => p.Contains("->", StringComparison.Ordinal) || p.Contains("<-", StringComparison.Ordinal))
            .ToList();
        return edges.Count > 0 ? string.Join(" ", edges) : "seed";
    }

    /// <summary>
    ///     Formats a provenance chain as a multi-line block for a per-file comment.
    /// </summary>
    /// <param name="provenance">The provenance chain.</param>
    /// <returns>The block text (without comment delimiters); empty when the chain is empty.</returns>
    public static string Format(IReadOnlyList<string> provenance)
    {
        if (provenance.Count == 0)
            return string.Empty;

        var lines = new List<string> { "included via:" };
        lines.AddRange(provenance.Select(p => "  " + p));
        return string.Join("\n", lines);
    }
}
