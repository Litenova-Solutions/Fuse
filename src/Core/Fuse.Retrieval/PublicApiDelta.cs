using Fuse.Indexing;

namespace Fuse.Retrieval;

/// <summary>
///     The kind of a public-API change between two symbol sets (T2).
/// </summary>
public enum ApiChangeKind
{
    /// <summary>A public or protected member present in the new set but not the old (additive).</summary>
    Added,

    /// <summary>A public or protected member present in the old set but not the new (breaking).</summary>
    Removed,

    /// <summary>A member whose signature changed between the sets (breaking).</summary>
    SignatureChanged,

    /// <summary>A member whose accessibility was reduced (for example public to internal; breaking).</summary>
    AccessibilityReduced,
}

/// <summary>
///     One public-API change between two symbol sets.
/// </summary>
/// <param name="Kind">The kind of change.</param>
/// <param name="Symbol">The member's fully qualified name.</param>
/// <param name="Before">The prior signature or accessibility, when relevant; otherwise null.</param>
/// <param name="After">The new signature or accessibility, when relevant; otherwise null.</param>
/// <param name="Breaking">Whether the change breaks the public contract (removal, signature change, accessibility reduction).</param>
public sealed record ApiChange(ApiChangeKind Kind, string Symbol, string? Before, string? After, bool Breaking);

/// <summary>
///     The public-API delta between two symbol sets (T2).
/// </summary>
/// <param name="Changes">The changes, breaking first, then additive; empty when the public surface is unchanged.</param>
public sealed record PublicApiDeltaResult(IReadOnlyList<ApiChange> Changes)
{
    /// <summary>Whether any change breaks the public contract.</summary>
    public bool HasBreaking => Changes.Any(c => c.Breaking);

    /// <summary>The breaking changes only.</summary>
    public IReadOnlyList<ApiChange> Breaking => Changes.Where(c => c.Breaking).ToList();
}

/// <summary>
///     Computes the public-API delta between a base and a current set of symbols (T2): added, removed, and
///     changed public and protected members, each flagged breaking (removal, signature change, accessibility
///     reduction) or additive. Pure and deterministic over the symbol sets, so it works graph-grade from the
///     store or compilation-confirmed from a resident workspace; the caller supplies the two sets.
/// </summary>
/// <remarks>
///     Flagging is conservative (the T2 kill-risk mitigation): only public and protected members participate, and
///     a change that cannot be classified is not flagged breaking. Binary-compatibility subtleties (default
///     parameter values, const inlining) are out of scope.
/// </remarks>
public static class PublicApiDelta
{
    /// <summary>
    ///     Computes the public-API delta between two symbol sets.
    /// </summary>
    /// <param name="baseSymbols">The symbols on the base side (before the change).</param>
    /// <param name="currentSymbols">The symbols on the current side (after the change).</param>
    /// <returns>The delta: breaking changes first, then additive; empty when the public surface is unchanged.</returns>
    public static PublicApiDeltaResult Compute(
        IReadOnlyList<SymbolRecord> baseSymbols, IReadOnlyList<SymbolRecord> currentSymbols)
    {
        var baseline = PublicByName(baseSymbols);
        var current = PublicByName(currentSymbols);

        var changes = new List<ApiChange>();

        // Removed: in the base public surface but gone from the current one. Breaking.
        foreach (var (name, symbol) in baseline)
        {
            if (!current.ContainsKey(name))
                changes.Add(new ApiChange(ApiChangeKind.Removed, name, symbol.Signature ?? symbol.FullyQualifiedName, null, Breaking: true));
        }

        foreach (var (name, symbol) in current)
        {
            if (!baseline.TryGetValue(name, out var before))
            {
                // Added: new public member. Additive, not breaking.
                changes.Add(new ApiChange(ApiChangeKind.Added, name, null, symbol.Signature ?? symbol.FullyQualifiedName, Breaking: false));
                continue;
            }

            // Present in both: a reduced accessibility is breaking, and takes precedence over a signature change
            // (the member is effectively removed from the surface). Otherwise a differing signature is breaking.
            if (AccessibilityRank(symbol.Accessibility) < AccessibilityRank(before.Accessibility))
                changes.Add(new ApiChange(ApiChangeKind.AccessibilityReduced, name, before.Accessibility, symbol.Accessibility, Breaking: true));
            else if (!string.Equals(before.Signature, symbol.Signature, StringComparison.Ordinal)
                     && before.Signature is not null && symbol.Signature is not null)
                changes.Add(new ApiChange(ApiChangeKind.SignatureChanged, name, before.Signature, symbol.Signature, Breaking: true));
        }

        // Breaking first (removals, signature changes, accessibility reductions), then additive; stable by name.
        var ordered = changes
            .OrderByDescending(c => c.Breaking)
            .ThenBy(c => c.Symbol, StringComparer.Ordinal)
            .ToList();
        return new PublicApiDeltaResult(ordered);
    }

    // Public and protected members keyed by fully qualified name. When a name appears more than once (partials,
    // overloads collapsed by FQN), the first wins; the delta is a surface check, not an overload-resolution one.
    private static Dictionary<string, SymbolRecord> PublicByName(IReadOnlyList<SymbolRecord> symbols)
    {
        var map = new Dictionary<string, SymbolRecord>(StringComparer.Ordinal);
        foreach (var symbol in symbols)
        {
            if (!IsPublicSurface(symbol))
                continue;
            map.TryAdd(symbol.FullyQualifiedName, symbol);
        }

        return map;
    }

    // A member is on the public surface when it is flagged public-api or its accessibility is public/protected.
    private static bool IsPublicSurface(SymbolRecord symbol) =>
        symbol.IsPublicApi
        || AccessibilityRank(symbol.Accessibility) >= AccessibilityRank("protected");

    // Ranks accessibility so a reduction (a lower rank) is detectable. Unknown accessibility ranks below private
    // so it is never treated as a reduction target spuriously.
    private static int AccessibilityRank(string? accessibility) => accessibility?.ToLowerInvariant() switch
    {
        "public" => 4,
        "protected" or "protected internal" => 3,
        "internal" => 2,
        "private protected" => 1,
        "private" => 0,
        _ => -1,
    };
}
