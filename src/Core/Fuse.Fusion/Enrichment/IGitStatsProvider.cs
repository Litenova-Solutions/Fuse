using Fuse.Emission.Models;
namespace Fuse.Fusion.Enrichment;

/// <summary>
///     Provides optional per-file git churn and last-modified enrichment.
/// </summary>
public interface IGitStatsProvider
{
    /// <summary>
    ///     Collects git stats for the specified relative paths.
    /// </summary>
    /// <param name="sourceDirectory">The repository root or working directory.</param>
    /// <param name="relativePaths">Normalized relative paths to enrich.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>Git stats, or an unavailable result outside a git repository.</returns>
    Task<GitStatsResult> GetStatsAsync(
        string sourceDirectory,
        IReadOnlyList<string> relativePaths,
        CancellationToken cancellationToken = default);
}
