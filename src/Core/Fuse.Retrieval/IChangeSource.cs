namespace Fuse.Retrieval;

/// <summary>
///     Supplies the files changed since a git base ref, and their diffs, to the retrieval engine. Implemented
///     by an adapter over the git change detector so the retrieval layer stays decoupled from the git plumbing.
/// </summary>
public interface IChangeSource
{
    /// <summary>
    ///     Returns the normalized relative paths of files changed since a base ref.
    /// </summary>
    /// <param name="rootDirectory">The repository root or working directory.</param>
    /// <param name="since">The base ref (branch, commit, or <c>HEAD~N</c>).</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The changed file paths (forward slashes), or empty when none.</returns>
    /// <exception cref="ChangeSourceException">Thrown when git is unavailable or the diff fails.</exception>
    Task<IReadOnlyList<string>> GetChangedFilesAsync(string rootDirectory, string since, CancellationToken cancellationToken);

    /// <summary>
    ///     Returns the per-file diffs for files changed since a base ref.
    /// </summary>
    /// <param name="rootDirectory">The repository root or working directory.</param>
    /// <param name="since">The base ref (branch, commit, or <c>HEAD~N</c>).</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>One <see cref="ChangedFile" /> per changed file.</returns>
    /// <exception cref="ChangeSourceException">Thrown when git is unavailable or the diff fails.</exception>
    Task<IReadOnlyList<ChangedFile>> GetDiffsAsync(string rootDirectory, string since, CancellationToken cancellationToken);

    /// <summary>
    ///     Returns the content of a file as it existed at the base ref, or <c>null</c> when the file did not exist
    ///     there.
    /// </summary>
    /// <param name="rootDirectory">The repository root or working directory.</param>
    /// <param name="since">The base ref (branch, commit, or <c>HEAD~N</c>).</param>
    /// <param name="relativePath">The path of the file relative to <paramref name="rootDirectory" />.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The file's content at <paramref name="since" />, or <c>null</c> when it was absent at that ref.</returns>
    /// <remarks>
    ///     Backs the T2 public-API delta: review extracts the base-side public symbols of a changed file from this
    ///     content and diffs them against the current surface, without rehydrating the whole base checkout. The
    ///     default implementation returns <c>null</c> for sources that cannot read historical content.
    /// </remarks>
    /// <exception cref="ChangeSourceException">Thrown when git is unavailable or the read fails.</exception>
    Task<string?> GetFileContentAtBaseAsync(
        string rootDirectory, string since, string relativePath, CancellationToken cancellationToken) =>
        Task.FromResult<string?>(null);
}

/// <summary>
///     A changed file and its diff summary.
/// </summary>
/// <param name="Path">The normalized relative path.</param>
/// <param name="Added">The number of added lines.</param>
/// <param name="Removed">The number of removed lines.</param>
/// <param name="Hunks">The unified-diff hunk text, or empty.</param>
public sealed record ChangedFile(string Path, int Added, int Removed, string Hunks);

/// <summary>
///     Thrown when the change source cannot determine changes (git unavailable, not a repository, diff failed).
/// </summary>
public sealed class ChangeSourceException : Exception
{
    /// <summary>
    ///     Initializes a new instance of the <see cref="ChangeSourceException" /> class.
    /// </summary>
    /// <param name="message">The error message.</param>
    public ChangeSourceException(string message) : base(message)
    {
    }
}
