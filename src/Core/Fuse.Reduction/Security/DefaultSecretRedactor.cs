using System.Text.RegularExpressions;
using Fuse.Reduction.Security;

namespace Fuse.Reduction.Security;

/// <summary>
///     Default secret redactor using regex patterns and Shannon-entropy heuristics.
/// </summary>
/// <remarks>
///     Detection is best-effort and pattern-driven: known formats (AWS keys, JWTs, PEM keys, connection
///     strings, API tokens) are matched by regex, and a Shannon-entropy threshold catches high-entropy
///     quoted literals not covered by a named pattern. Both directions of error are possible: secrets in
///     unrecognized shapes can slip through, and high-entropy non-secrets such as hashes or base64 blobs
///     can be redacted as false positives.
/// </remarks>
/// <seealso cref="ISecretRedactor" />
public sealed partial class DefaultSecretRedactor : ISecretRedactor
{
    private static readonly (string Kind, Regex Pattern)[] Patterns =
    [
        ("aws-access-key", AwsAccessKeyRegex()),
        ("aws-secret-key", AwsSecretKeyRegex()),
        ("jwt", JwtRegex()),
        ("pem-private-key", PemPrivateKeyRegex()),
        ("api-token", ApiTokenRegex()),
    ];

    // Keys that, when present among the key=value pairs of a quoted literal, mark it as a real
    // ADO.NET / EF / storage connection string rather than an incidental run of assignments.
    private static readonly string[] ConnectionStringKeywords =
    [
        "server", "data source", "host", "initial catalog", "database",
        "user id", "uid", "password", "pwd", "integrated security",
        "trusted_connection", "port", "accountendpoint", "accountkey",
    ];

    /// <inheritdoc />
    public SecretRedactionResult Redact(string content)
    {
        if (string.IsNullOrEmpty(content))
            return new SecretRedactionResult(content, new Dictionary<string, int>());

        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var (kind, pattern) in Patterns)
        {
            content = pattern.Replace(content, match =>
            {
                Increment(counts, kind);
                return $"[REDACTED:{kind}]";
            });
        }

        content = RedactConnectionStrings(content, counts);

        content = HighEntropyLiteralRegex().Replace(content, match =>
        {
            var value = match.Groups["value"].Value;
            if (!IsHighEntropy(value))
                return match.Value;

            Increment(counts, "high-entropy");
            return $"{match.Groups["quote"].Value}[REDACTED:high-entropy]{match.Groups["quote2"].Value}";
        });

        return new SecretRedactionResult(content, counts);
    }

    // Connection strings are only redacted when they sit inside a quoted literal and carry the structure of a
    // real one: at least two semicolon-delimited key=value pairs, one of whose keys is a connection keyword.
    // This avoids redacting ordinary C# assignments such as `Server = GetServer();`, which the old single-pair
    // pattern matched, silently mutating code bodies that `verify` cannot detect.
    private static string RedactConnectionStrings(string content, Dictionary<string, int> counts)
    {
        return ConnectionStringLiteralRegex().Replace(content, match =>
        {
            if (!LooksLikeConnectionString(match.Groups["value"].Value))
                return match.Value;

            Increment(counts, "connection-string");
            return $"{match.Groups["quote"].Value}[REDACTED:connection-string]{match.Groups["quote2"].Value}";
        });
    }

    private static bool LooksLikeConnectionString(string value)
    {
        var pairs = 0;
        var hasKeyword = false;

        foreach (var segment in value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var equals = segment.IndexOf('=');
            if (equals <= 0 || equals == segment.Length - 1)
                continue;

            var key = segment[..equals].Trim();
            // A key is a single identifier-like token, optionally with spaces (for example "User ID").
            if (key.Length == 0 || !key.All(c => char.IsLetterOrDigit(c) || c is ' ' or '_'))
                continue;

            pairs++;
            if (!hasKeyword && Array.IndexOf(ConnectionStringKeywords, key.ToLowerInvariant()) >= 0)
                hasKeyword = true;
        }

        return pairs >= 2 && hasKeyword;
    }

    private static bool IsHighEntropy(string value)
    {
        if (value.Length < 32)
            return false;

        var entropy = ComputeShannonEntropy(value);
        var hasMixedCase = value.Any(char.IsUpper) && value.Any(char.IsLower);
        var hasDigit = value.Any(char.IsDigit);
        var hasSymbol = value.Any(c => !char.IsLetterOrDigit(c));

        // Catch API keys and tokens not matched by named regex patterns when entropy >= 4.5.
        return entropy >= 4.5 && hasMixedCase && hasDigit && hasSymbol;
    }

    private static double ComputeShannonEntropy(string value)
    {
        var frequencies = new Dictionary<char, int>();
        foreach (var ch in value)
        {
            frequencies.TryGetValue(ch, out var count);
            frequencies[ch] = count + 1;
        }

        var length = value.Length;
        var entropy = 0.0;
        foreach (var count in frequencies.Values)
        {
            var probability = (double)count / length;
            entropy -= probability * Math.Log2(probability);
        }

        return entropy;
    }

    private static void Increment(Dictionary<string, int> counts, string kind)
    {
        counts.TryGetValue(kind, out var current);
        counts[kind] = current + 1;
    }

    [GeneratedRegex(@"\b(AKIA[0-9A-Z]{16})\b", RegexOptions.Compiled)]
    private static partial Regex AwsAccessKeyRegex();

    [GeneratedRegex(@"(?:aws_secret_access_key|AWS_SECRET_ACCESS_KEY)\s*[=:]\s*['""]?([A-Za-z0-9/+=]{40})['""]?", RegexOptions.Compiled | RegexOptions.IgnoreCase)]
    private static partial Regex AwsSecretKeyRegex();

    [GeneratedRegex(@"\beyJ[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}\b", RegexOptions.Compiled)]
    private static partial Regex JwtRegex();

    [GeneratedRegex(@"-----BEGIN (?:RSA |EC |OPENSSH )?PRIVATE KEY-----", RegexOptions.Compiled)]
    private static partial Regex PemPrivateKeyRegex();

    // Matches a single- or double-quoted literal with no embedded quote or newline, bounded to a sane length.
    // Whether the captured value is actually a connection string is decided by LooksLikeConnectionString.
    [GeneratedRegex(@"(?<quote>['""])(?<value>[^'""\r\n]{0,2048})(?<quote2>\k<quote>)", RegexOptions.Compiled)]
    private static partial Regex ConnectionStringLiteralRegex();

    [GeneratedRegex(@"(?:api[_-]?key|api[_-]?token|secret[_-]?key|access[_-]?token)\s*[=:]\s*['""]?([A-Za-z0-9_\-]{16,})['""]?", RegexOptions.Compiled | RegexOptions.IgnoreCase)]
    private static partial Regex ApiTokenRegex();

    [GeneratedRegex(@"(?<quote>['""])(?<value>[A-Za-z0-9+/=_\-]{32,})(?<quote2>\k<quote>)", RegexOptions.Compiled)]
    private static partial Regex HighEntropyLiteralRegex();
}
