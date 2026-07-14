namespace Fuse.Cli.Services;

/// <summary>
///     A source of coalesced workspace file-change batches for the resident workspace (S1 step 3). The production
///     implementation is <see cref="DebouncedFileWatcher" />; tests may supply a fake that raises
///     <see cref="BatchChanged" /> directly.
/// </summary>
public interface IResidentBatchWatcher : IDisposable
{
    /// <summary>
    ///     Raised after filesystem changes settle, carrying the coalesced net change per path over the debounce
    ///     window. The resident workspace applies one update per file from this list.
    /// </summary>
    event Func<IReadOnlyList<WorkspaceFileChange>, CancellationToken, Task>? BatchChanged;
}
