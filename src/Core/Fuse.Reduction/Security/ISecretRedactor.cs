namespace Fuse.Reduction.Security;

/// <summary>
///     Redacts sensitive values from source content before token counting.
/// </summary>
public interface ISecretRedactor
{
    /// <summary>
    ///     Replaces detected secrets in place with <c>[REDACTED:&lt;kind&gt;]</c> placeholders.
    /// </summary>
    /// <param name="content">The source content to scan.</param>
    /// <param name="classifyCodeLiterals">
    ///     When <see langword="true" /> (set for C# source), additionally counts how many redactions landed
    ///     inside a string literal, surfaced as <see cref="SecretRedactionResult.CodeLiteralRedactions" />.
    ///     A non-zero count flags a redaction that altered a code body rather than a configuration value.
    /// </param>
    /// <returns>The redacted content and per-kind occurrence counts.</returns>
    SecretRedactionResult Redact(string content, bool classifyCodeLiterals = false);
}
