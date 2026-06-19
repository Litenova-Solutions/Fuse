namespace Fuse.Reduction.Security;

/// <summary>
///     Aggregated redaction counts across fused files.
/// </summary>
/// <param name="CountsByKind">Total redaction occurrences keyed by secret kind.</param>
/// <param name="Total">Sum of all redaction occurrences across every kind.</param>
public sealed record RedactionSummary(IReadOnlyDictionary<string, int> CountsByKind, int Total)
{
    /// <summary>
    ///     Renders the redaction summary as a <c>&lt;!-- fuse:redactions --&gt;</c> XML comment block.
    /// </summary>
    /// <returns>
    ///     A comment block listing each kind and count in descending count order followed by a total line,
    ///     or <see cref="string.Empty" /> when <see cref="Total" /> is <c>0</c>.
    /// </returns>
    public string ToComment()
    {
        if (Total == 0)
            return string.Empty;

        var lines = CountsByKind
            .OrderByDescending(kv => kv.Value)
            .Select(kv => $"{kv.Key}: {kv.Value}");

        return "<!-- fuse:redactions\n" + string.Join("\n", lines) + $"\ntotal: {Total}\n-->";
    }
}
