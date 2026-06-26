using System.Text;

namespace Fuse.Retrieval;

/// <summary>
///     Builds the human-readable review preamble from a review context plan: the changed files, the semantic
///     blast radius, and the reason every non-changed file was included.
/// </summary>
/// <remarks>
///     The preamble is the explanation surface of a review. Every non-changed file is listed with its role and
///     the edge chain that pulled it in, so a reviewer can see why each support file is present.
/// </remarks>
public static class ReviewPreambleBuilder
{
    /// <summary>
    ///     Builds the preamble text for a review plan.
    /// </summary>
    /// <param name="plan">The review context plan.</param>
    /// <param name="changedSince">The git base ref the review was computed against.</param>
    /// <returns>The preamble text.</returns>
    public static string Build(ContextPlan plan, string changedSince)
    {
        var changed = plan.Items.Where(i => i.Role == "changed").ToList();
        var impacted = plan.Items.Where(i => i.Role != "changed").ToList();

        var builder = new StringBuilder();
        builder.AppendLine("review");
        builder.AppendLine($"changedSince: {changedSince}");
        builder.AppendLine($"changed files: {changed.Count}");
        builder.AppendLine($"context files: {plan.Items.Count}  estimatedTokens: {plan.EstimatedTokens}");
        builder.AppendLine();

        builder.AppendLine("changed:");
        foreach (var item in changed)
            builder.AppendLine($"  {item.Path}");
        builder.AppendLine();

        builder.AppendLine("semantic impact:");
        if (impacted.Count == 0)
        {
            builder.AppendLine("  (none)");
        }
        else
        {
            foreach (var item in impacted)
                builder.AppendLine($"  {item.Path} [{item.Role}] {Explanation(item)}");
        }

        if (plan.Warnings.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine("notes:");
            foreach (var warning in plan.Warnings)
                builder.AppendLine($"  {warning}");
        }

        return builder.ToString();
    }

    // The explanation is the edge chain in the item's provenance; falls back to the role when none is present.
    private static string Explanation(ContextPlanItem item)
    {
        var edges = item.ProvenanceChain.Where(p => p.Contains("->", StringComparison.Ordinal) || p.Contains("<-", StringComparison.Ordinal)).ToList();
        return edges.Count > 0 ? string.Join(" ", edges) : "included via " + item.Role;
    }
}
