using System.Text;

namespace Fuse.Retrieval;

/// <summary>
///     Renders a <see cref="PublicApiDeltaResult" /> into the human-readable API-delta section that
///     <c>fuse_review</c> and <c>fuse_impact</c> prepend (T2): breaking changes first (removals, signature
///     changes, accessibility reductions), then additions, or a one-line "no public API change" when the surface
///     is unchanged. Pure and side-effect-free.
/// </summary>
public static class ApiDeltaReport
{
    /// <summary>
    ///     Renders the API-delta section.
    /// </summary>
    /// <param name="delta">The computed public-API delta.</param>
    /// <returns>The section text; a single "no public API change" line when the surface is unchanged.</returns>
    public static string Render(PublicApiDeltaResult delta)
    {
        if (delta.Changes.Count == 0)
            return "public API delta: none (no public or protected surface change).";

        var builder = new StringBuilder();
        var breaking = delta.Breaking;
        builder.AppendLine(breaking.Count > 0
            ? $"public API delta: {breaking.Count} BREAKING change(s), {delta.Changes.Count - breaking.Count} additive."
            : $"public API delta: {delta.Changes.Count} additive change(s), none breaking.");

        foreach (var change in delta.Changes)
        {
            var flag = change.Breaking ? "BREAKING" : "additive";
            var detail = change.Kind switch
            {
                ApiChangeKind.Removed => "removed",
                ApiChangeKind.Added => "added",
                ApiChangeKind.SignatureChanged => $"signature changed ({change.Before} -> {change.After})",
                ApiChangeKind.AccessibilityReduced => $"accessibility reduced ({change.Before} -> {change.After})",
                _ => change.Kind.ToString(),
            };
            builder.AppendLine($"  [{flag}] {change.Symbol}: {detail}");
        }

        return builder.ToString().TrimEnd();
    }
}
