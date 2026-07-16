namespace Fuse.Collection.FileSystem;

/// <summary>
///     Resolves the canonical repository identity used by workspace-scoped MCP operations.
/// </summary>
public static class WorkspaceIdentityResolver
{
    /// <summary>
    ///     Resolves <paramref name="requestedPath" /> to the nearest enclosing Git repository root.
    /// </summary>
    /// <param name="requestedPath">A repository directory, nested directory, or file path.</param>
    /// <param name="rootPath">The canonical repository root when resolution succeeds.</param>
    /// <returns><see langword="true" /> when the path belongs to a Git repository; otherwise <see langword="false" />.</returns>
    public static bool TryResolveRepositoryRoot(string requestedPath, out string rootPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(requestedPath);

        var fullPath = Path.GetFullPath(requestedPath);
        string startDirectory;
        if (Directory.Exists(fullPath))
        {
            startDirectory = fullPath;
        }
        else if (File.Exists(fullPath))
        {
            startDirectory = Path.GetDirectoryName(fullPath)!;
        }
        else
        {
            rootPath = string.Empty;
            return false;
        }

        var repositoryRoot = RepositoryRootResolver.TryFindRepositoryRoot(startDirectory);
        if (repositoryRoot is null)
        {
            rootPath = string.Empty;
            return false;
        }

        rootPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(repositoryRoot));
        return true;
    }

    /// <summary>
    ///     Returns a stable comparison key for a canonical repository root.
    /// </summary>
    /// <param name="rootPath">The canonical repository root.</param>
    /// <returns>A normalized key suitable for locks, registries, and index manifests.</returns>
    public static string NormalizeKey(string rootPath) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootPath)).ToLowerInvariant();
}
