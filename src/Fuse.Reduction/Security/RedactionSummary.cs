namespace Fuse.Reduction.Security;

/// <summary>
///     Aggregated redaction counts across fused files.
/// </summary>
public sealed record RedactionSummary(IReadOnlyDictionary<string, int> CountsByKind, int Total)
{
    /// <summary>
    ///     Renders the redaction summary as an XML comment block.
    /// </summary>
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
