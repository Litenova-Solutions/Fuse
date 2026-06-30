using System.Text;

namespace Fuse.Indexing;

/// <summary>
///     Splits compound identifiers into their subword parts so a prose query word matches a compound name.
///     <c>ApplyRoundingMode</c> yields <c>apply</c>, <c>rounding</c>, <c>mode</c>; <c>order_total</c> yields
///     <c>order</c>, <c>total</c>; <c>base64Encode</c> yields <c>base</c>, <c>encode</c> (the lone digit run is
///     dropped). The split runs on the same boundaries at index time (to build the subtokens full-text field)
///     and at query time (to expand a query term), so the two stay aligned.
/// </summary>
/// <remarks>
///     The split is language-agnostic: it operates on character classes (letters, digits, separators) and the
///     conventional camelCase, snake_case, and acronym boundaries, so it carries to any indexed language without
///     a per-language rule. Parts shorter than <see cref="MinPartLength" /> are dropped to keep the field lean
///     and avoid flooding the candidate pool with single characters.
/// </remarks>
public static class IdentifierSplitter
{
    /// <summary>The shortest subword kept; shorter runs (single letters, lone digits) are dropped as noise.</summary>
    public const int MinPartLength = 2;

    /// <summary>
    ///     Splits text containing one or more identifiers into distinct, lowercased subword parts.
    /// </summary>
    /// <param name="text">The text to split (an identifier, a signature, or a run of several).</param>
    /// <returns>The distinct subword parts, lowercased, in first-seen order; empty when none qualify.</returns>
    public static IReadOnlyList<string> Split(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return [];

        var parts = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var current = new StringBuilder();

        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (!char.IsLetterOrDigit(ch))
            {
                Flush(current, parts, seen);
                continue;
            }

            if (current.Length > 0 && IsBoundary(text, i))
                Flush(current, parts, seen);

            current.Append(char.ToLowerInvariant(ch));
        }

        Flush(current, parts, seen);
        return parts;
    }

    /// <summary>
    ///     Expands one or more identifier-bearing fields into a single space-joined subtokens string for storage
    ///     in the full-text index.
    /// </summary>
    /// <param name="fields">The identifier-bearing fields (for example a chunk's name, symbols, and signature).</param>
    /// <returns>The distinct subword parts joined by a space; empty when none qualify.</returns>
    public static string Expand(params string?[] fields)
    {
        var parts = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var field in fields)
        {
            foreach (var part in Split(field))
            {
                if (seen.Add(part))
                    parts.Add(part);
            }
        }

        return string.Join(' ', parts);
    }

    // A boundary sits before the character at index i (the previous run should flush) when the character class
    // changes in one of the conventional ways: a letter/digit transition, a camelCase hump (lower then upper),
    // or the end of an acronym run (upper then upper-followed-by-lower, so "XMLParser" splits into XML, Parser).
    private static bool IsBoundary(string text, int i)
    {
        var ch = text[i];
        var prev = text[i - 1];

        if (char.IsDigit(ch) != char.IsDigit(prev))
            return true;
        if (char.IsUpper(ch) && char.IsLower(prev))
            return true;
        if (char.IsUpper(ch) && char.IsUpper(prev) && i + 1 < text.Length && char.IsLower(text[i + 1]))
            return true;

        return false;
    }

    private static void Flush(StringBuilder current, List<string> parts, HashSet<string> seen)
    {
        if (current.Length >= MinPartLength)
        {
            var part = current.ToString();
            if (seen.Add(part))
                parts.Add(part);
        }

        current.Clear();
    }
}
