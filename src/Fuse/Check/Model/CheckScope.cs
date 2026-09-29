namespace Fuse.Check.Model;

/// <summary>What a check covers: every change since HEAD, or the files a client names.</summary>
internal abstract record CheckScope
{
    private CheckScope()
    {
    }

    /// <summary>Every file in the working tree that differs from HEAD, as the change tracker has it after the sync.</summary>
    public sealed record AllChanges : CheckScope;

    /// <summary>The files a client names, usually the ones an agent has just edited.</summary>
    /// <param name="Paths">
    ///     Absolute or repository-relative paths. The sync reads them from disk even before the file watcher reports them,
    ///     so a check that follows an edit at once still sees it.
    /// </param>
    public sealed record Files(IReadOnlyList<string> Paths) : CheckScope;
}
