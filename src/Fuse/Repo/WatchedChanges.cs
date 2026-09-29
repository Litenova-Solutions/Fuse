namespace Fuse.Repo;

/// <summary>What the file watcher reported between two syncs, as <see cref="WatchedPaths.Drain"/> hands it over.</summary>
/// <param name="Sources">
///     Absolute paths of C# and Razor files that were written, created, deleted or renamed, and of the sources inside a
///     directory that was created or renamed into place.
/// </param>
/// <param name="VanishedDirectories">
///     Extensionless paths that were deleted or renamed away and are neither a file nor a directory now. Each may have
///     been a directory, whose files send no events of their own.
/// </param>
/// <param name="ProjectFiles">Project, props, targets, editorconfig and global.json files that changed, in the order their events arrived.</param>
/// <param name="WatcherErrors">
///     The errors the watcher reported, in order. After one, an unknown set of files may have changed without an event.
/// </param>
internal sealed record WatchedChanges(
    IReadOnlySet<string> Sources,
    IReadOnlyList<string> VanishedDirectories,
    IReadOnlyList<string> ProjectFiles,
    IReadOnlyList<string> WatcherErrors);
