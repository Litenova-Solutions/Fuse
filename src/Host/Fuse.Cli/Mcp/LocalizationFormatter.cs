using System.Text;
using Fuse.Retrieval;

namespace Fuse.Cli.Mcp;

/// <summary>
///     Renders a <see cref="LocalizationResult" /> as text for the CLI and the <c>fuse_localize</c> MCP tool,
///     including the graded signal-sufficiency state and, when the result is not confident, the navigation map
///     that lets a caller refine its query.
/// </summary>
public static class LocalizationFormatter
{
    /// <summary>
    ///     Formats a localization result, leading with its graded state and listing candidates and any
    ///     navigation map.
    /// </summary>
    /// <param name="result">The localization result to render.</param>
    /// <returns>A human- and agent-readable text block.</returns>
    public static string Format(LocalizationResult result)
    {
        var builder = new StringBuilder();
        var state = result.State.ToString().ToLowerInvariant();
        builder.AppendLine($"localize [{state}]: {result.Candidates.Count} candidates");
        foreach (var candidate in result.Candidates)
        {
            builder.AppendLine($"  {candidate.Score:F3}  {candidate.Path}  (~{candidate.EstimatedTokens} tokens)");
            foreach (var reason in candidate.Reasons)
                builder.AppendLine($"        {reason}");
        }

        if (result.Navigation is { } map)
        {
            builder.AppendLine("  navigation map (refine your query):");
            if (map.CandidateAreas.Count > 0)
                builder.AppendLine($"    areas: {string.Join(", ", map.CandidateAreas)}");
            if (map.EntryPoints.Count > 0)
                builder.AppendLine($"    entry points: {string.Join(", ", map.EntryPoints)}");
            if (map.NearestSymbols.Count > 0)
                builder.AppendLine($"    nearest symbols: {string.Join(", ", map.NearestSymbols)}");
            builder.AppendLine($"    ask: {map.Ask}");
        }

        foreach (var warning in result.Warnings)
            builder.AppendLine($"  ! {warning}");

        return builder.ToString();
    }
}
