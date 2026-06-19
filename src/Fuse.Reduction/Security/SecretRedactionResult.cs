namespace Fuse.Reduction.Security;

/// <summary>
///     Result of a secret redaction pass.
/// </summary>
/// <param name="Content">The redacted content.</param>
/// <param name="CountsByKind">Occurrence counts keyed by secret kind.</param>
public sealed record SecretRedactionResult(string Content, IReadOnlyDictionary<string, int> CountsByKind)
{
    /// <summary>
    ///     Gets the total number of redactions applied.
    /// </summary>
    public int TotalCount => CountsByKind.Values.Sum();
}
