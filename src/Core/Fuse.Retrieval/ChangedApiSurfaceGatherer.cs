namespace Fuse.Retrieval;

/// <summary>
///     Gathers the base and current content of the changed C# files and computes their public-API delta (T2).
///     Base content comes from the <see cref="IChangeSource" /> (the git base ref); current content comes from an
///     injected reader over the working tree, which returns <c>null</c> for a file that no longer exists (a
///     deletion). Keeping both sources injected leaves the retrieval engine filesystem-free and this orchestration
///     testable without git or disk.
/// </summary>
/// <remarks>
///     A file absent on both sides is skipped (nothing to compare). A non-C# path never contributes to the API
///     surface, so only <c>.cs</c> paths are read - the base-content read (a git-show subprocess) is skipped for
///     the rest.
/// </remarks>
public static class ChangedApiSurfaceGatherer
{
    /// <summary>
    ///     Gathers the changed C# files' surfaces and computes the aggregate public-API delta.
    /// </summary>
    /// <param name="changeSource">The change source that reads base-ref content.</param>
    /// <param name="rootDirectory">The repository root the paths are relative to.</param>
    /// <param name="since">The base ref (branch, commit, or <c>HEAD~N</c>).</param>
    /// <param name="changedFiles">The relative paths of the changed files (forward slashes).</param>
    /// <param name="currentContentReader">
    ///     Reads the current working-tree content of a relative path, or returns <c>null</c> when the file no
    ///     longer exists.
    /// </param>
    /// <param name="cancellationToken">A token to cancel the reads.</param>
    /// <returns>The aggregate public-API delta; empty when no public surface changed.</returns>
    public static async Task<PublicApiDeltaResult> GatherAsync(
        IChangeSource changeSource,
        string rootDirectory,
        string since,
        IReadOnlyList<string> changedFiles,
        Func<string, CancellationToken, Task<string?>> currentContentReader,
        CancellationToken cancellationToken)
    {
        var files = new List<ChangedFileContent>();
        foreach (var path in changedFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
                continue;

            var baseContent = await changeSource.GetFileContentAtBaseAsync(rootDirectory, since, path, cancellationToken);
            var currentContent = await currentContentReader(path, cancellationToken);
            if (baseContent is null && currentContent is null)
                continue;

            files.Add(new ChangedFileContent(path, baseContent, currentContent));
        }

        return ChangedFileApiDelta.Compute(files);
    }
}
