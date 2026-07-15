using System.Text;
using Fuse.Context;
using Fuse.Retrieval;
using Fuse.Scoping;

namespace Fuse.Cli.Commands;

/// <summary>
///     Shared formatting helpers for the context and review commands: the plan-only summary and the output
///     format parser.
/// </summary>
internal static class PlanFormatter
{
    /// <summary>
    ///     Formats a plan as a one-line-per-file summary (no source bodies).
    /// </summary>
    /// <param name="plan">The plan.</param>
    /// <returns>The summary text.</returns>
    public static string Format(ContextPlan plan)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"{plan.Mode} plan: {plan.Items.Count} files, ~{plan.EstimatedTokens} tokens");
        foreach (var item in plan.Items)
        {
            var keep = item.MustKeep ? "*" : " ";
            builder.AppendLine($"  {keep} {item.Score:F3} [{item.Role}/{item.Tier}] {item.Path}  (~{item.EstimatedTokens} tokens)");
        }

        foreach (var warning in plan.Warnings)
            builder.AppendLine($"  ! {warning}");

        return builder.ToString();
    }

    /// <summary>
    ///     Parses an output format name, defaulting to XML for an unknown value.
    /// </summary>
    /// <param name="format">The format name.</param>
    /// <returns>The parsed <see cref="ContextOutputFormat" />.</returns>
    public static ContextOutputFormat ParseFormat(string format) => format.Trim().ToLowerInvariant() switch
    {
        "markdown" or "md" => ContextOutputFormat.Markdown,
        "json" => ContextOutputFormat.Json,
        _ => ContextOutputFormat.Xml,
    };
}
