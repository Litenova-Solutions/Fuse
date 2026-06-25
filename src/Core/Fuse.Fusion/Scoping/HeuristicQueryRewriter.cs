using System.Text.RegularExpressions;

namespace Fuse.Fusion.Scoping;

/// <summary>
///     The rules-based, no-model half of query rewriting (item 12): it emphasizes the compound PascalCase
///     identifiers in a query (the strongest code signal that the query names a type) by weighting their tokens
///     above the rest, so a query that mentions a type leans toward the file that declares it. This is the
///     default-safe heuristic; the LLM-backed rewrite stays opt-in and off the no-model path.
/// </summary>
public static partial class HeuristicQueryRewriter
{
    // The extra weight a token derived from a compound PascalCase identifier carries over a plain query word.
    private const double IdentifierWeight = 2.0;

    // Compound PascalCase identifiers (two or more humps), for example PaymentService or HttpClientFactory.
    [GeneratedRegex(@"\b[A-Z][a-z0-9]+(?:[A-Z][a-z0-9]*)+\b", RegexOptions.Compiled)]
    private static partial Regex IdentifierPattern();

    /// <summary>
    ///     Rewrites a query into weighted terms: every query token starts at weight 1, and tokens that come from
    ///     a compound PascalCase identifier are raised to <see cref="IdentifierWeight" />. The result is suitable
    ///     for the weighted-terms ranking overload.
    /// </summary>
    /// <param name="query">The raw query text.</param>
    /// <returns>The term-to-weight map; empty when the query has no tokens.</returns>
    public static IReadOnlyDictionary<string, double> Rewrite(string query)
    {
        var weights = new Dictionary<string, double>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(query))
            return weights;

        foreach (var term in RelevanceTokenizer.Tokenize(query))
            weights[term] = 1.0;

        // Raise the tokens that come from a compound PascalCase identifier; max() so an identifier token never
        // ends up below a plain occurrence of the same term.
        foreach (Match match in IdentifierPattern().Matches(query))
            foreach (var term in RelevanceTokenizer.Tokenize(match.Value))
                weights[term] = Math.Max(weights.GetValueOrDefault(term), IdentifierWeight);

        return weights;
    }
}
