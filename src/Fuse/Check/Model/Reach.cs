namespace Fuse.Check.Model;

/// <summary>
///     The files a declaration change in the targets can break. The check binds them as candidates, besides the targets
///     it has already bound.
/// </summary>
internal abstract record Reach
{
    private Reach()
    {
    }

    /// <summary>No target has a declaration change, so no other file can gain an error and nothing more is bound.</summary>
    public sealed record None : Reach;

    /// <summary>
    ///     A change that can break code that never names it: a type header, a delegate, a global using or an assembly
    ///     attribute. Every file in the reached projects is a candidate, and none gets a cause, because no single
    ///     declaration explains it.
    /// </summary>
    /// <param name="Files">
    ///     Absolute paths of every source file in the projects with declaration changes and in their dependents, compared
    ///     the way the file system compares paths.
    /// </param>
    public sealed record Broad(IReadOnlySet<string> Files) : Reach;

    /// <summary>The files that use a changed declaration, each with the change that made it a candidate.</summary>
    /// <param name="Causes">
    ///     Keyed by absolute path, compared the way the file system compares paths. A file several changes reach keeps the
    ///     first change that reached it, not the nearest one.
    /// </param>
    public sealed record Precise(IReadOnlyDictionary<string, Cause> Causes) : Reach;
}
