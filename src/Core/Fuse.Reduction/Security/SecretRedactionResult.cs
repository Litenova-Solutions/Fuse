namespace Fuse.Reduction.Security;

/// <summary>
///     Result of a secret redaction pass.
/// </summary>
/// <param name="Content">The redacted content.</param>
/// <param name="CountsByKind">Occurrence counts keyed by secret kind.</param>
/// <remarks>
///     <see cref="CountsByKind" /> may carry the reserved <see cref="CodeLiteralKind" /> entry, which counts
///     how many redactions landed inside a C# string literal (a code body) rather than a configuration value.
///     That entry is a fidelity classification, not a secret kind, so it is excluded from
///     <see cref="TotalCount" /> and surfaced separately by <see cref="CodeLiteralRedactions" />.
/// </remarks>
public sealed record SecretRedactionResult(string Content, IReadOnlyDictionary<string, int> CountsByKind)
{
    /// <summary>
    ///     The reserved <see cref="CountsByKind" /> key recording redactions made inside a C# code literal.
    /// </summary>
    public const string CodeLiteralKind = "code-literal-modifications";

    /// <summary>
    ///     Total number of secret redactions applied across all kinds, excluding the code-literal classification.
    /// </summary>
    public int TotalCount => CountsByKind.Where(kv => kv.Key != CodeLiteralKind).Sum(kv => kv.Value);

    /// <summary>
    ///     Number of redactions that modified a C# string literal (a code body) rather than a configuration
    ///     value. A non-zero value on otherwise-code content signals a redaction false positive.
    /// </summary>
    public int CodeLiteralRedactions => CountsByKind.GetValueOrDefault(CodeLiteralKind);
}
