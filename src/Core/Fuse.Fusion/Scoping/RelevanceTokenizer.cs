using System.Text.RegularExpressions;

namespace Fuse.Fusion.Scoping;

/// <summary>
///     Normalizes text into lexical terms for relevance ranking. The same normalization is applied to
///     indexed content and to queries so they share a vocabulary.
/// </summary>
/// <remarks>
///     Normalization lowercases, splits on non-word characters, splits camelCase and snake_case identifiers
///     into their sub-words, drops a small set of English and code stopwords, and applies a conservative
///     suffix stemmer. Stemming is intentionally light: it folds common plural and verb endings so that, for
///     example, <c>caching</c> and <c>cached</c> both reduce to <c>cach</c> and <c>handlers</c> reduces to
///     <c>handler</c>, without the aggressive rewrites of a full Porter stemmer.
/// </remarks>
public static partial class RelevanceTokenizer
{
    private static readonly HashSet<string> Stopwords = new(StringComparer.OrdinalIgnoreCase)
    {
        // English function and question words.
        "a", "an", "and", "are", "as", "at", "be", "been", "but", "by", "can", "could", "did", "do",
        "does", "for", "from", "had", "has", "have", "how", "in", "into", "is", "it", "its", "of", "on",
        "or", "should", "than", "that", "the", "their", "them", "then", "there", "these", "this", "those",
        "to", "was", "were", "what", "when", "where", "which", "who", "whom", "why", "will", "with", "would",
        // Generic words that carry little signal in code search.
        "code", "contains", "file", "files", "implementation", "method", "methods", "class", "classes",
        "using", "return", "returns", "value", "values",
    };

    private static readonly Regex IdentifierSplitter = BuildIdentifierSplitter();
    private static readonly Regex NonWord = BuildNonWord();

    /// <summary>
    ///     Tokenizes and normalizes the supplied text.
    /// </summary>
    /// <param name="text">The raw text to tokenize.</param>
    /// <returns>
    ///     The normalized terms in encounter order, including identifier sub-words. Stopwords are dropped and
    ///     remaining terms are stemmed. May contain duplicates, which carry term-frequency signal.
    /// </returns>
    public static List<string> Tokenize(string? text)
    {
        var terms = new List<string>();
        if (string.IsNullOrEmpty(text))
            return terms;

        foreach (var raw in NonWord.Split(text))
        {
            if (string.IsNullOrWhiteSpace(raw))
                continue;

            AddTerm(terms, raw);

            foreach (var part in IdentifierSplitter.Split(raw))
            {
                if (string.IsNullOrWhiteSpace(part) || string.Equals(part, raw, StringComparison.Ordinal))
                    continue;

                AddTerm(terms, part);
            }
        }

        return terms;
    }

    private static void AddTerm(List<string> terms, string raw)
    {
        var lowered = raw.ToLowerInvariant();
        if (Stopwords.Contains(lowered))
            return;

        var stemmed = Stem(lowered);
        if (stemmed.Length == 0 || Stopwords.Contains(stemmed))
            return;

        terms.Add(stemmed);
    }

    /// <summary>
    ///     Applies a conservative suffix stemmer to a single lowercased term.
    /// </summary>
    /// <param name="term">The lowercased term to stem.</param>
    /// <returns>The stemmed term, or the original when no rule applies.</returns>
    public static string Stem(string term)
    {
        if (term.Length <= 3)
            return term;

        if (term.EndsWith("sses", StringComparison.Ordinal))
            return term[..^2]; // classes-style endings keep one s pair

        if (term.EndsWith("ies", StringComparison.Ordinal) && term.Length > 4)
            return term[..^3] + "y";

        if (term.EndsWith("ing", StringComparison.Ordinal) && term.Length > 5)
            return term[..^3];

        if (term.EndsWith("ers", StringComparison.Ordinal) && term.Length > 4)
            return term[..^1];

        if (term.EndsWith("ed", StringComparison.Ordinal) && term.Length > 4)
            return term[..^2];

        if (term.EndsWith("s", StringComparison.Ordinal) &&
            !term.EndsWith("ss", StringComparison.Ordinal) &&
            !term.EndsWith("us", StringComparison.Ordinal) &&
            term.Length > 3)
        {
            return term[..^1];
        }

        return term;
    }

    [GeneratedRegex(@"(?<=[a-z0-9])(?=[A-Z])|(?<=[A-Z])(?=[A-Z][a-z])|_+", RegexOptions.Compiled)]
    private static partial Regex BuildIdentifierSplitter();

    [GeneratedRegex(@"\W+", RegexOptions.Compiled)]
    private static partial Regex BuildNonWord();
}
