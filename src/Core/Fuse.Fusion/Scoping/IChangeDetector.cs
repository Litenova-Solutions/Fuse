namespace Fuse.Fusion.Scoping;

/// <summary>
///     Detects files changed in a git repository since a given ref.
/// </summary>
public interface IChangeDetector
{
    /// <summary>
    ///     Returns normalized relative paths of files changed since the given ref.
    /// </summary>
    /// <param name="sourceDirectory">The repository root or working directory to diff against.</param>
    /// <param name="since">Git ref to compare against: a branch name, commit hash, or <c>HEAD~N</c> expression.</param>
    /// <param name="cancellationToken">Token used to cancel the change detection.</param>
    /// <returns>
    ///     The awaited result is the changed file paths, normalized to forward slashes and relative to
    ///     <paramref name="sourceDirectory" />. Empty when no files changed.
    /// </returns>
    /// <remarks>
    ///     Requires a git repository and a git executable; implementations signal unavailability or
    ///     diff failures by throwing rather than returning an empty list.
    /// </remarks>
    /// <exception cref="ChangeDetectionException">
    ///     Thrown when git is unavailable, the directory is not a git repository, or the diff fails.
    /// </exception>
    Task<IReadOnlyList<string>> GetChangedRelativePathsAsync(
        string sourceDirectory,
        string since,
        CancellationToken cancellationToken = default);
}
