using Fuse.Paths;

namespace Fuse.Check.Model;

/// <summary>What a check covers: every change since HEAD, the files a client names, or the paths a scoped check names.</summary>
internal abstract record CheckScope
{
    private CheckScope()
    {
    }

    /// <summary>Every file in the working tree that differs from HEAD, as the change tracker has it after the sync.</summary>
    public sealed record AllChanges : CheckScope;

    /// <summary>The files a client names, usually the ones an agent has just edited.</summary>
    /// <param name="Paths">
    ///     The named files, in the order the client named them. The sync reads them from disk even before the file
    ///     watcher reports them, so a check that follows an edit at once still sees it.
    /// </param>
    public sealed record Files(IReadOnlyList<RepoPath> Paths) : CheckScope;

    /// <summary>The directories, files and glob patterns a scoped check names, exactly as the caller named them.</summary>
    /// <param name="Named">
    ///     Absolute file and directory paths and absolute glob patterns, in the order named. The check expands them
    ///     against the files that differ from HEAD after the sync.
    /// </param>
    public sealed record NamedPaths(IReadOnlyList<string> Named) : CheckScope;
}
