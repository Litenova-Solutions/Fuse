using Fuse.Fusion.Scoping;
using Fuse.Retrieval;

namespace Fuse.Cli.Services;

/// <summary>
///     Adapts the git-backed <see cref="IChangeDetector" /> to the retrieval layer's <see cref="IChangeSource" />,
///     so the retrieval engine resolves a git base ref without referencing the git plumbing directly.
/// </summary>
public sealed class GitChangeSource : IChangeSource
{
    private readonly IChangeDetector _changeDetector;

    /// <summary>
    ///     Initializes a new instance of the <see cref="GitChangeSource" /> class.
    /// </summary>
    /// <param name="changeDetector">The git change detector to delegate to.</param>
    public GitChangeSource(IChangeDetector changeDetector) => _changeDetector = changeDetector;

    /// <inheritdoc />
    public async Task<IReadOnlyList<string>> GetChangedFilesAsync(string rootDirectory, string since, CancellationToken cancellationToken)
    {
        try
        {
            return await _changeDetector.GetChangedRelativePathsAsync(rootDirectory, since, cancellationToken);
        }
        catch (ChangeDetectionException ex)
        {
            throw new ChangeSourceException(ex.Message);
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ChangedFile>> GetDiffsAsync(string rootDirectory, string since, CancellationToken cancellationToken)
    {
        try
        {
            var diffs = await _changeDetector.GetDiffsAsync(rootDirectory, since, cancellationToken);
            return diffs.Select(d => new ChangedFile(d.Path, d.Added, d.Removed, d.Hunks)).ToList();
        }
        catch (ChangeDetectionException ex)
        {
            throw new ChangeSourceException(ex.Message);
        }
    }
}
