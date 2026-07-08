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

    /// <summary>
    ///     Returns the unified diff hunks for files changed since the given ref.
    /// </summary>
    /// <param name="sourceDirectory">The repository root or working directory to diff against.</param>
    /// <param name="since">Git ref to compare against: a branch name, commit hash, or <c>HEAD~N</c> expression.</param>
    /// <param name="cancellationToken">Token used to cancel the diff.</param>
    /// <returns>
    ///     The awaited result is one <see cref="FileDiff" /> per changed file, with paths normalized to forward
    ///     slashes. The default implementation returns an empty list for detectors that do not produce diffs.
    /// </returns>
    /// <remarks>
    ///     Used by review-shaped change emission. Implementations that cannot produce hunks may return an empty
    ///     list rather than throwing.
    /// </remarks>
    /// <exception cref="ChangeDetectionException">
    ///     Thrown when git is unavailable, the directory is not a git repository, or the diff fails.
    /// </exception>
    Task<IReadOnlyList<FileDiff>> GetDiffsAsync(
        string sourceDirectory,
        string since,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<FileDiff>>([]);

    /// <summary>
    ///     Returns the content of a file as it existed at a given git ref, or <c>null</c> when the file did not
    ///     exist at that ref.
    /// </summary>
    /// <param name="sourceDirectory">The repository root or working directory.</param>
    /// <param name="reference">Git ref to read from: a branch name, commit hash, or <c>HEAD~N</c> expression.</param>
    /// <param name="relativePath">The path of the file relative to <paramref name="sourceDirectory" />.</param>
    /// <param name="cancellationToken">Token used to cancel the read.</param>
    /// <returns>
    ///     The awaited result is the file's content at <paramref name="reference" />, or <c>null</c> when the file
    ///     was absent at that ref (for example a newly added file has no base version).
    /// </returns>
    /// <remarks>
    ///     Backs the T2 public-API delta: the base-side symbols of a changed file are extracted from the content
    ///     this returns, so review can compare the pre-edit and post-edit public surface without rehydrating the
    ///     whole base checkout. The default implementation returns <c>null</c> for detectors that cannot read
    ///     historical content.
    /// </remarks>
    /// <exception cref="ChangeDetectionException">
    ///     Thrown when git is unavailable, the directory is not a git repository, or the read fails for a reason
    ///     other than the file being absent at the ref.
    /// </exception>
    Task<string?> GetFileContentAtAsync(
        string sourceDirectory,
        string reference,
        string relativePath,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<string?>(null);
}
