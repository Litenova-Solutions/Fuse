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
        // PEM must run before the high-entropy literal pass so the whole block is removed as one unit rather
        // than the base64 body being caught (or missed) line by line.
        ("pem-private-key", PemPrivateKeyRegex()),
        ("github-token", GitHubTokenRegex()),
        ("google-api-key", GoogleApiKeyRegex()),
        ("slack-token", SlackTokenRegex()),
        ("stripe-key", StripeKeyRegex()),
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
    public SecretRedactionResult Redact(string content, bool classifyCodeLiterals = false)
    {
        if (string.IsNullOrEmpty(content))
            return new SecretRedactionResult(content, new Dictionary<string, int>());

        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        // Classify against the ORIGINAL content's literal spans, computed before any replacement shifts offsets.
        var codeLiteralRedactions = classifyCodeLiterals ? CountCodeLiteralRedactions(content) : 0;

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

        if (codeLiteralRedactions > 0)
            counts[SecretRedactionResult.CodeLiteralKind] = codeLiteralRedactions;

        return new SecretRedactionResult(content, counts);
    }

    /// <inheritdoc />
    public IReadOnlyList<SecretFinding> FindSecretSpans(string content)
    {
        if (string.IsNullOrEmpty(content))
            return [];

        // Mirror the redaction decisions on the ORIGINAL content (named patterns always match-redact;
        // connection-string and high-entropy apply their predicates), recording each match's kind and span.
        // This enumerates the same patterns Redact uses but never replaces, so it is a pure read-only view that
        // cannot affect emitted output. Spans are offsets into the original content for in-place highlighting.
        var findings = new List<SecretFinding>();

        foreach (var (kind, pattern) in Patterns)
            foreach (Match match in pattern.Matches(content))
                findings.Add(new SecretFinding(kind, match.Index, match.Length));

        foreach (Match match in ConnectionStringLiteralRegex().Matches(content))
            if (LooksLikeConnectionString(match.Groups["value"].Value))
                findings.Add(new SecretFinding("connection-string", match.Index, match.Length));

        foreach (Match match in HighEntropyLiteralRegex().Matches(content))
            if (IsHighEntropy(match.Groups["value"].Value))
                findings.Add(new SecretFinding("high-entropy", match.Index, match.Length));

        findings.Sort((a, b) => a.Start.CompareTo(b.Start));
        return findings;
    }

    // Counts, on the original content, the secret matches that fall inside a C# string literal. This is an
    // additive diagnostic computed independently of the replacement chain (whose offsets shift), so it never
    // affects redaction output. It mirrors the redaction decisions: named patterns always redact on match;
    // connection-string and high-entropy apply their predicates.
    private int CountCodeLiteralRedactions(string content)
    {
        var spans = FindStringLiteralSpans(content);
        if (spans.Count == 0)
            return 0;

        var count = 0;

        foreach (var (_, pattern) in Patterns)
            foreach (Match match in pattern.Matches(content))
                if (IsInsideLiteral(spans, match.Index))
                    count++;

        foreach (Match match in ConnectionStringLiteralRegex().Matches(content))
            if (LooksLikeConnectionString(match.Groups["value"].Value) && IsInsideLiteral(spans, match.Index))
                count++;

        foreach (Match match in HighEntropyLiteralRegex().Matches(content))
            if (IsHighEntropy(match.Groups["value"].Value) && IsInsideLiteral(spans, match.Index))
                count++;

        return count;
    }

    private static bool IsInsideLiteral(List<(int Start, int End)> spans, int index)
    {
        foreach (var (start, end) in spans)
            if (index >= start && index < end)
                return true;
        return false;
    }

    // Minimal C# string-literal span scanner (regular, verbatim, and raw forms). Kept local to the reduction
    // layer to avoid a dependency on the C# language plugin; it only needs literal boundaries, not full lexing.
    private static List<(int Start, int End)> FindStringLiteralSpans(string content)
    {
        var spans = new List<(int, int)>();
        var i = 0;
        var length = content.Length;

        while (i < length)
        {
            var c = content[i];

            // Raw string literal: a run of three or more quotes (optionally $-prefixed).
            var rawStart = i;
            var dollars = 0;
            while (i < length && content[i] == '$') { i++; dollars++; }
            var quotes = 0;
            var q = i;
            while (q < length && content[q] == '"') { q++; quotes++; }
            if (quotes >= 3)
            {
                i = q;
                while (i < length)
                {
                    if (content[i] == '"')
                    {
                        var run = 0;
                        while (i + run < length && content[i + run] == '"') run++;
                        i += run;
                        if (run >= quotes) break;
                        continue;
                    }

                    i++;
                }

                spans.Add((rawStart, i));
                continue;
            }

            i = rawStart; // not a raw string; rewind the speculative $/quote scan
            c = content[i];

            var verbatim = false;
            if (c is '@' or '$')
            {
                var j = i + 1;
                if (j < length && (content[j] == '@' || content[j] == '$')) j++;
                if (j < length && content[j] == '"')
                {
                    verbatim = content.AsSpan(i, j - i).Contains('@');
                    var start = i;
                    i = j + 1; // past opening quote
                    while (i < length)
                    {
                        if (verbatim)
                        {
                            if (content[i] == '"')
                            {
                                if (i + 1 < length && content[i + 1] == '"') { i += 2; continue; }
                                i++;
                                break;
                            }

                            i++;
                            continue;
                        }

                        if (content[i] == '\\' && i + 1 < length) { i += 2; continue; }
                        if (content[i] == '"') { i++; break; }
                        if (content[i] == '\n') break;
                        i++;
                    }

                    spans.Add((start, i));
                    continue;
                }
            }

            if (c == '"')
            {
                var start = i;
                i++;
                while (i < length)
                {
                    if (content[i] == '\\' && i + 1 < length) { i += 2; continue; }
                    if (content[i] == '"') { i++; break; }
                    if (content[i] == '\n') break;
                    i++;
                }

                spans.Add((start, i));
                continue;
            }

            i++;
        }

        return spans;
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

    // Match the entire PEM block, BEGIN line through END line including the base64 body, so the key material is
    // removed rather than only its header. Singleline lets the body span newlines; the lazy quantifier stops at
    // the first END line.
    [GeneratedRegex(@"-----BEGIN (?:RSA |EC |DSA |OPENSSH |ENCRYPTED |PGP )?PRIVATE KEY-----.*?-----END (?:RSA |EC |DSA |OPENSSH |ENCRYPTED |PGP )?PRIVATE KEY-----", RegexOptions.Compiled | RegexOptions.Singleline)]
    private static partial Regex PemPrivateKeyRegex();

    // GitHub personal/OAuth/app tokens (ghp_, gho_, ghu_, ghs_, ghr_) and fine-grained PATs (github_pat_).
    [GeneratedRegex(@"\b(?:gh[pousr]_[A-Za-z0-9]{36,}|github_pat_[A-Za-z0-9_]{22,})\b", RegexOptions.Compiled)]
    private static partial Regex GitHubTokenRegex();

    // Google API keys: the fixed AIza prefix followed by 35 url-safe base64 characters.
    [GeneratedRegex(@"\bAIza[0-9A-Za-z\-_]{35}\b", RegexOptions.Compiled)]
    private static partial Regex GoogleApiKeyRegex();

    // Slack tokens: xoxb/xoxa/xoxp/xoxr/xoxs followed by the dash-delimited body.
    [GeneratedRegex(@"\bxox[baprs]-[A-Za-z0-9-]{10,}\b", RegexOptions.Compiled)]
    private static partial Regex SlackTokenRegex();

    // Stripe live secret and restricted keys.
    [GeneratedRegex(@"\b[sr]k_live_[0-9A-Za-z]{16,}\b", RegexOptions.Compiled)]
    private static partial Regex StripeKeyRegex();

    // Matches a single- or double-quoted literal with no embedded quote or newline, bounded to a sane length.
    // Whether the captured value is actually a connection string is decided by LooksLikeConnectionString.
    [GeneratedRegex(@"(?<quote>['""])(?<value>[^'""\r\n]{0,2048})(?<quote2>\k<quote>)", RegexOptions.Compiled)]
    private static partial Regex ConnectionStringLiteralRegex();

    [GeneratedRegex(@"(?:api[_-]?key|api[_-]?token|secret[_-]?key|access[_-]?token)\s*[=:]\s*['""]?([A-Za-z0-9_\-]{16,})['""]?", RegexOptions.Compiled | RegexOptions.IgnoreCase)]
    private static partial Regex ApiTokenRegex();

    [GeneratedRegex(@"(?<quote>['""])(?<value>[A-Za-z0-9+/=_\-]{32,})(?<quote2>\k<quote>)", RegexOptions.Compiled)]
    private static partial Regex HighEntropyLiteralRegex();
}
