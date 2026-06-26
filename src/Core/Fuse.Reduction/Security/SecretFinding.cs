namespace Fuse.Reduction.Security;

/// <summary>
///     The location of a detected secret in the original, unredacted content: its kind and the character span of
///     the matched literal. Used to surface a precise editor diagnostic over the secret, separate from the
///     redaction itself, which always replaces the value in emitted output.
/// </summary>
/// <param name="Kind">The secret kind (for example <c>github-token</c>, <c>connection-string</c>, <c>high-entropy</c>).</param>
/// <param name="Start">The zero-based character offset of the match in the original content.</param>
/// <param name="Length">The character length of the matched span.</param>
public sealed record SecretFinding(string Kind, int Start, int Length);
