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

        // The code-literal classification is reported on its own line, not as a secret kind, and excluded
        // from the secret total. A non-zero value warns that redaction altered code bodies, not just config.
        var lines = CountsByKind
            .Where(kv => kv.Key != SecretRedactionResult.CodeLiteralKind)
            .OrderByDescending(kv => kv.Value)
            .Select(kv => $"{kv.Key}: {kv.Value}")
            .ToList();

        var body = string.Join("\n", lines) + $"\ntotal: {Total}";

        if (CountsByKind.TryGetValue(SecretRedactionResult.CodeLiteralKind, out var codeLiteral) && codeLiteral > 0)
            body += $"\ncode-literal-modifications: {codeLiteral}";

        return "<!-- fuse:redactions\n" + body + "\n-->";
    }
}
