using System.Text.RegularExpressions;

namespace Fuse.Cli.Mcp;

/// <summary>
///     The scoping strategy chosen for a <c>fuse_ask</c> call.
/// </summary>
internal enum AskMode
{
    /// <summary>Whole-codebase skeleton (signatures only), for broad architectural questions.</summary>
    Skeleton,

    /// <summary>Dependency-aware focus around a single named type.</summary>
    Focus,

    /// <summary>BM25 relevance search over the task text.</summary>
    Search,
}

/// <summary>
///     A scoping plan derived from a natural-language task and a token budget.
/// </summary>
/// <param name="Mode">The chosen scoping strategy.</param>
/// <param name="Seed">The focus seed when <see cref="Mode" /> is <see cref="AskMode.Focus" />; otherwise null.</param>
/// <param name="Depth">The dependency traversal depth to apply for focus or search.</param>
internal sealed record AskPlan(AskMode Mode, string? Seed, int Depth);

/// <summary>
///     Chooses a scoping strategy for <c>fuse_ask</c> from the task text and token budget, without calling a
///     model. The choice is deterministic so the same task and budget always resolve to the same plan.
/// </summary>
/// <remarks>
///     The heuristics are intentionally simple and explainable: a task asking about the codebase as a whole
///     maps to a skeleton; a task naming exactly one type maps to focus on that type; everything else maps to
///     relevance search. A larger budget allows one more hop of dependency expansion. The tool falls back from
///     focus to search when the named type cannot be resolved, so a wrong guess degrades rather than fails.
/// </remarks>
internal static partial class AskStrategySelector
{
    // Budget at or above which one extra hop of dependency expansion is affordable.
    private const int DeepExpansionBudget = 40_000;

    private static readonly string[] OverviewTerms =
    [
        "architecture", "overview", "structure", "organized", "organised", "organization", "organisation",
        "high level", "high-level", "overall", "whole codebase", "entire codebase", "big picture", "lay of the land",
        "what does this", "what is this", "how is the code", "how is this code", "project layout", "entry point",
    ];

    /// <summary>
    ///     Selects a scoping plan for the supplied task and token budget.
    /// </summary>
    /// <param name="task">The natural-language task description.</param>
    /// <param name="tokenBudget">The token budget the packed context must fit within.</param>
    /// <returns>The chosen <see cref="AskPlan" />.</returns>
    public static AskPlan Select(string task, int tokenBudget)
    {
        var text = task ?? string.Empty;
        var lower = text.ToLowerInvariant();
        var depth = tokenBudget >= DeepExpansionBudget ? 2 : 1;

        if (OverviewTerms.Any(term => lower.Contains(term, StringComparison.Ordinal)))
            return new AskPlan(AskMode.Skeleton, null, depth);

        var seed = ExtractTypeSeed(text);
        return seed is not null
            ? new AskPlan(AskMode.Focus, seed, depth)
            : new AskPlan(AskMode.Search, null, depth);
    }

    // A single type-like token (PascalCase with an internal lowercase, three or more characters) is treated as
    // a focus seed. Zero or several distinct candidates fall through to search, where BM25F still weights the
    // declared-symbol field highly.
    private static string? ExtractTypeSeed(string task)
    {
        string? candidate = null;
        foreach (Match match in TypeTokenRegex().Matches(task))
        {
            var token = match.Value;
            if (candidate is null)
                candidate = token;
            else if (!string.Equals(candidate, token, StringComparison.Ordinal))
                return null;
        }

        return candidate;
    }

    // A compound PascalCase identifier with two or more humps (for example OrderService, TokenBucketMiddleware).
    // Requiring a second hump excludes ordinary capitalized words such as "Which" or "How" that begin a
    // question, so a single-word match is a strong signal that the task names a specific type.
    [GeneratedRegex(@"\b[A-Z][a-z0-9]+(?:[A-Z][a-z0-9]*)+\b", RegexOptions.Compiled)]
    private static partial Regex TypeTokenRegex();
}
