using System.Text;

namespace Fuse.Semantics.Remediation;

/// <summary>
///     Renders a <see cref="RemediationPlan" /> to the human-readable report <c>fuse up</c> prints (C1): the
///     achieved tier, the workable-subset line, and, per project, whether it loaded and which remedy (if any)
///     addresses its failure. This is the report half of <c>fuse up</c>; applying the remedies is a separate
///     step, so this renderer is pure and side-effect-free.
/// </summary>
public static class RemediationReport
{
    /// <summary>
    ///     Renders the plan to a multi-line report.
    /// </summary>
    /// <param name="plan">The remediation plan.</param>
    /// <returns>The report text.</returns>
    public static string Render(RemediationPlan plan)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"load tier: {plan.Tier}");
        builder.AppendLine(plan.WorkableSubsetLine);
        builder.AppendLine();

        builder.AppendLine("per project:");
        foreach (var item in plan.Items)
        {
            if (item.Loaded)
            {
                builder.AppendLine($"  [ok] {item.Project}");
                continue;
            }

            if (item.Signature is null)
            {
                builder.AppendLine($"  [blocked] {item.Project}: {item.Reason} -> no known remedy (unrecognized; classify-only)");
            }
            else if (item.Signature.Remedy == "classify-only")
            {
                builder.AppendLine($"  [blocked] {item.Project}: {item.Signature.Id} {item.Signature.Title} -> repository code, not fixable by fuse up");
            }
            else
            {
                var consent = item.Signature.RequiresConsent ? " (needs --allow-install)" : string.Empty;
                builder.AppendLine($"  [blocked] {item.Project}: {item.Signature.Id} {item.Signature.Title} -> remedy: {item.Signature.Remedy}{consent}");
            }
        }

        builder.AppendLine();
        builder.AppendLine($"remediable: {plan.Remediable.Count}; unfixable by fuse up: {plan.Unfixable.Count}");
        return builder.ToString().TrimEnd();
    }
}
