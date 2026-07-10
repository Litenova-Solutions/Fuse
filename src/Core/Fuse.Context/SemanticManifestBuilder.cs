using System.Text;
using Fuse.Retrieval;

namespace Fuse.Context;

/// <summary>
///     Builds the semantic-context manifest preamble: a header summarizing the plan (mode, root, changed base,
///     file count, token estimate), the seed files, the semantic impact with the edge that pulled each
///     non-seed file in, and any notes.
/// </summary>
/// <remarks>
///     The manifest is the agent-facing explanation of a context payload. It is emitted as a comment block
///     ahead of the file entries; the comment delimiters are added by the format-specific emitter.
/// </remarks>
public static class SemanticManifestBuilder
{
    /// <summary>
    ///     Builds the manifest body (without comment delimiters) for a plan.
    /// </summary>
    /// <param name="plan">The context plan.</param>
    /// <param name="root">The workspace root, or null to omit.</param>
    /// <param name="changedSince">The git base ref for review plans, or null to omit.</param>
    /// <param name="apiDeltaSection">
    ///     The rendered public-API delta section (T2) for a review plan, or null to omit. Emitted ahead of the
    ///     seeds so a breaking change is the first thing the agent reads.
    /// </param>
    /// <param name="claimsSection">
    ///     The rendered graded-claims block (U2) for the answer, or null to omit. Emitted ahead of the seeds,
    ///     after the API delta, so the graded evidence trail is read before the source.
    /// </param>
    /// <returns>The manifest body text.</returns>
    public static string Build(
        ContextPlan plan, string? root = null, string? changedSince = null, string? apiDeltaSection = null, string? claimsSection = null)
    {
        var seeds = plan.Items.Where(i => i.MustKeep || i.Role is "changed" or "exact-seed").ToList();
        var impact = plan.Items.Where(i => !seeds.Contains(i)).ToList();

        var builder = new StringBuilder();
        builder.AppendLine($"fuse:semantic-context");
        builder.AppendLine($"mode: {plan.Mode}");
        if (root is not null)
            builder.AppendLine($"root: {root}");
        if (changedSince is not null)
            builder.AppendLine($"changedSince: {changedSince}");
        builder.AppendLine($"files: {plan.Items.Count}");
        builder.AppendLine($"estimatedTokens: {plan.EstimatedTokens}");
        builder.AppendLine();

        if (!string.IsNullOrWhiteSpace(apiDeltaSection))
        {
            builder.AppendLine(apiDeltaSection.TrimEnd());
            builder.AppendLine();
        }

        if (!string.IsNullOrWhiteSpace(claimsSection))
        {
            builder.AppendLine(claimsSection.TrimEnd());
            builder.AppendLine();
        }

        builder.AppendLine("seeds:");
        foreach (var seed in seeds)
            builder.AppendLine($"  - {seed.Path} ({seed.Role})");
        builder.AppendLine();

        builder.AppendLine("semantic impact:");
        if (impact.Count == 0)
        {
            builder.AppendLine("  (none)");
        }
        else
        {
            foreach (var item in impact)
                builder.AppendLine($"  {item.Path} [{item.Role}] {ProvenanceFormatter.Summarize(item.ProvenanceChain)}");
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
}
