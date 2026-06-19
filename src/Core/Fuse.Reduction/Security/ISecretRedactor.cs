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
    /// <returns>The redacted content and per-kind occurrence counts.</returns>
    SecretRedactionResult Redact(string content);
}
