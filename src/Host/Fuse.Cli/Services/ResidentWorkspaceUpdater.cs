using Fuse.Workspace;

namespace Fuse.Cli.Services;

/// <summary>
///     The result of applying a watcher batch to a resident workspace: how many changes were applied, added,
///     removed, or skipped (S1 step 3).
/// </summary>
/// <param name="Applied">Edits applied to existing documents.</param>
/// <param name="Added">New documents added.</param>
/// <param name="Removed">Documents removed for deleted files.</param>
/// <param name="Skipped">Changes not applied (not a C# file, not in a held project, or the file vanished).</param>
public sealed record ResidentUpdateResult(int Applied, int Added, int Removed, int Skipped);

/// <summary>
///     Applies a coalesced watcher batch to a resident workspace (S1 step 3): the glue between the file watcher's
///     <see cref="WorkspaceFileChange" /> batch and the resident compilations. For a created or changed C# file it
///     reads the new content from disk and edits the held document (adding it when the file is new to the project);
///     for a deleted file it removes the document. It reads file content but never writes the tree.
/// </summary>
/// <remarks>
///     Only C# files participate: the resident workspace holds C# compilations, so a change to a non-<c>.cs</c>
///     file is skipped. A created or changed file that is not under any held project is skipped too (the resident
///     workspace does not own it). This type performs the update against a supplied workspace; constructing the
///     resident workspace and subscribing it to the watcher is the serve/host wiring step.
/// </remarks>
public sealed class ResidentWorkspaceUpdater
{
    /// <summary>
    ///     Applies a batch of file changes to a resident workspace.
    /// </summary>
    /// <param name="workspace">The resident workspace to update.</param>
    /// <param name="batch">The coalesced file changes from the watcher.</param>
    /// <param name="cancellationToken">A token to cancel the update.</param>
    /// <returns>The counts of applied, added, removed, and skipped changes.</returns>
    public ResidentUpdateResult Apply(
        ResidentWorkspace workspace, IReadOnlyList<WorkspaceFileChange> batch, CancellationToken cancellationToken)
    {
        int applied = 0, added = 0, removed = 0, skipped = 0;
        foreach (var change in batch)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!change.FullPath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            {
                skipped++;
                continue;
            }

            switch (change.Kind)
            {
                case FileChangeKind.Deleted:
                    if (workspace.RemoveDocument(change.FullPath, cancellationToken))
                        removed++;
                    else
                        skipped++;
                    break;

                case FileChangeKind.Created:
                case FileChangeKind.Changed:
                    string content;
                    try
                    {
                        content = File.ReadAllText(change.FullPath);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        skipped++; // The file vanished or is locked between the event and the read.
                        break;
                    }

                    // Edit the existing document; if it is new to the project, add it instead.
                    if (workspace.ApplyEdit(change.FullPath, content, cancellationToken))
                        applied++;
                    else if (workspace.AddDocument(change.FullPath, content, cancellationToken))
                        added++;
                    else
                        skipped++;
                    break;

                default:
                    skipped++;
                    break;
            }
        }

        return new ResidentUpdateResult(applied, added, removed, skipped);
    }
}
