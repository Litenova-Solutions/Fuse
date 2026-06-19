using System.Text.RegularExpressions;

namespace Fuse.Reduction.Security;

/// <summary>
///     Default secret redactor using regex patterns and Shannon-entropy heuristics.
/// </summary>
public sealed class DefaultSecretRedactor : ISecretRedactor
{
    private static readonly (string Kind, Regex Pattern)[] Patterns =
    [
        ("aws-access-key", new Regex(@"\b(AKIA[0-9A-Z]{16})\b", RegexOptions.Compiled)),
        ("aws-secret-key", new Regex(@"(?:aws_secret_access_key|AWS_SECRET_ACCESS_KEY)\s*[=:]\s*['""]?([A-Za-z0-9/+=]{40})['""]?", RegexOptions.Compiled | RegexOptions.IgnoreCase)),
        ("jwt", new Regex(@"\beyJ[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}\b", RegexOptions.Compiled)),
        ("pem-private-key", new Regex(@"-----BEGIN (?:RSA |EC |OPENSSH )?PRIVATE KEY-----", RegexOptions.Compiled)),
        ("connection-string", new Regex(@"(?:Server|Data Source|Host|User ID|Password|Pwd)\s*=\s*[^;\s""']+", RegexOptions.Compiled | RegexOptions.IgnoreCase)),
        ("api-token", new Regex(@"(?:api[_-]?key|api[_-]?token|secret[_-]?key|access[_-]?token)\s*[=:]\s*['""]?([A-Za-z0-9_\-]{16,})['""]?", RegexOptions.Compiled | RegexOptions.IgnoreCase)),
    ];

    private static readonly Regex HighEntropyLiteral = new(
        @"(?<quote>['""])(?<value>[A-Za-z0-9+/=_\-]{32,})(?<quote2>\k<quote>)",
        RegexOptions.Compiled);

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

        content = HighEntropyLiteral.Replace(content, match =>
        {
            var value = match.Groups["value"].Value;
            if (!IsHighEntropy(value))
                return match.Value;

            Increment(counts, "high-entropy");
            return $"{match.Groups["quote"].Value}[REDACTED:high-entropy]{match.Groups["quote2"].Value}";
        });

        return new SecretRedactionResult(content, counts);
    }

    private static bool IsHighEntropy(string value)
    {
        if (value.Length < 32)
            return false;

        var entropy = ComputeShannonEntropy(value);
        var hasMixedCase = value.Any(char.IsUpper) && value.Any(char.IsLower);
        var hasDigit = value.Any(char.IsDigit);
        var hasSymbol = value.Any(c => !char.IsLetterOrDigit(c));

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
}
