using System.Text;

namespace Fuse.Cli.Mcp;

/// <summary>
///     Bounds <c>fuse_review</c> so a huge diff (a long-lived branch, an ancient base ref, or a mega-commit) returns
///     a fast partial result instead of running unbounded blast-radius resolution (R26). When the changed-file set
///     exceeds the cap, the review returns the changed-file list and a note steering the caller to narrow the base
///     ref, rather than paying the graph cost for hundreds of files. <c>maxTokens</c> still bounds output; this
///     bounds the graph work that precedes it.
/// </summary>
internal static class ReviewBounds
{
    /// <summary>The default maximum changed-file count before <c>fuse_review</c> returns a bounded partial.</summary>
    internal const int DefaultChangedFileCap = 150;

    /// <summary>The environment variable that overrides <see cref="DefaultChangedFileCap" />.</summary>
    internal const string CapEnvVar = "FUSE_REVIEW_MAX_CHANGED_FILES";

    /// <summary>
    ///     Resolves the changed-file cap: an explicit per-call value wins, then the environment override, then the
    ///     default. A non-positive value falls through to the next source.
    /// </summary>
    /// <param name="explicitCap">The per-call cap (0 or negative to defer to the environment or default).</param>
    /// <returns>The effective cap (always positive).</returns>
    internal static int ResolveCap(int explicitCap)
    {
        if (explicitCap > 0)
            return explicitCap;
        if (int.TryParse(Environment.GetEnvironmentVariable(CapEnvVar), out var fromEnv) && fromEnv > 0)
            return fromEnv;
        return DefaultChangedFileCap;
    }

    /// <summary>Whether a changed-file count exceeds the cap and the review should return a bounded partial.</summary>
    /// <param name="changedCount">The number of changed files in the diff.</param>
    /// <param name="cap">The effective cap.</param>
    /// <returns><see langword="true" /> when the review should be bounded.</returns>
    internal static bool ShouldBound(int changedCount, int cap) => changedCount > cap;

    /// <summary>
    ///     Formats the bounded-review partial: an optional availability header, a note naming the diff size and the
    ///     remedy, and the changed-file list (itself capped so the partial stays small).
    /// </summary>
    /// <param name="availabilityHeader">The ambient availability header, or null to omit it.</param>
    /// <param name="changedSince">The git base ref the diff was taken against.</param>
    /// <param name="changedFiles">The changed-file paths.</param>
    /// <param name="cap">The effective cap that was exceeded.</param>
    /// <returns>The bounded-review text.</returns>
    internal static string FormatBoundedReview(
        string? availabilityHeader, string changedSince, IReadOnlyList<string> changedFiles, int cap)
    {
        const int MaxListed = 200;
        var builder = new StringBuilder();
        if (!string.IsNullOrEmpty(availabilityHeader))
            builder.AppendLine(availabilityHeader);
        builder.AppendLine(
            $"fuse_review: the diff since {changedSince} spans {changedFiles.Count} changed files, above the review cap of {cap}. " +
            "Showing the changed-file list only; blast-radius resolution is skipped to keep the call bounded. " +
            $"Narrow the base ref (a nearer commit or branch point) to get the full semantic review, or raise the cap with {CapEnvVar}.");
        builder.AppendLine($"changed files ({changedFiles.Count}):");
        foreach (var file in changedFiles.Take(MaxListed))
            builder.AppendLine($"  {file}");
        if (changedFiles.Count > MaxListed)
            builder.AppendLine($"  ... and {changedFiles.Count - MaxListed} more");
        return builder.ToString().TrimEnd();
    }
}
