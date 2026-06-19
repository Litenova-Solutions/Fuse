namespace Fuse.Analysis.Git;

/// <summary>
///     Git statistics enrichment result for a set of files.
/// </summary>
/// <param name="IsAvailable">Whether git stats could be collected for the source directory.</param>
/// <param name="StatsByPath">Per-file stats keyed by normalized relative path.</param>
public sealed record GitStatsResult(
    bool IsAvailable,
    IReadOnlyDictionary<string, GitFileStats> StatsByPath);
