namespace Fuse.Analysis.Git;

/// <summary>
///     Per-file git enrichment statistics for manifest output.
/// </summary>
/// <param name="RelativePath">Normalized relative path within the repository.</param>
/// <param name="CommitCount">Number of commits touching the file within the lookback window.</param>
/// <param name="LastModified">Last commit date for the file, or <c>null</c> when unknown.</param>
public sealed record GitFileStats(string RelativePath, int CommitCount, DateTimeOffset? LastModified);
