namespace Fuse.Analysis.Changes;

/// <summary>
///     Detects files changed in a git repository since a given ref.
/// </summary>
public interface IChangeDetector
{
    /// <summary>
    ///     Returns normalized relative paths of files changed since the given ref.
    /// </summary>
    Task<IReadOnlyList<string>> GetChangedRelativePathsAsync(
        string sourceDirectory,
        string since,
        CancellationToken cancellationToken = default);
}
