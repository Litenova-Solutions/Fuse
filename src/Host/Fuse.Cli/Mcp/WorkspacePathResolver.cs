using Fuse.Collection.FileSystem;

namespace Fuse.Cli.Mcp;

/// <summary>
///     Resolves repo-relative and absolute file paths under a workspace root and refuses paths that escape the root
///     after normalization (for example via <c>../</c> segments or an absolute path on another branch of the tree).
/// </summary>
internal static class WorkspacePathResolver
{
    /// <summary>
    ///     Normalizes a workspace root directory to its full path.
    /// </summary>
    /// <param name="path">An absolute or relative path to the workspace directory.</param>
    /// <returns>The full path of the workspace root.</returns>
    internal static string ResolveRoot(string path) => Path.GetFullPath(path);

    /// <summary>
    ///     Resolves a workspace-scoped MCP operation to its canonical Git repository root.
    /// </summary>
    /// <param name="path">An absolute or relative path inside the repository.</param>
    /// <returns>The canonical repository root.</returns>
    /// <exception cref="WorkspaceIdentityException">The path does not resolve to a Git repository.</exception>
    internal static string ResolveRepositoryRoot(string path)
    {
        var fullPath = Path.GetFullPath(path);
        if (!Directory.Exists(fullPath))
            throw new DirectoryNotFoundException(fullPath);
        if (!WorkspaceIdentityResolver.TryResolveRepositoryRoot(fullPath, out var repositoryRoot))
        {
            throw new WorkspaceIdentityException(
                $"'{fullPath}' is not inside a Git repository. Workspace-scoped Fuse MCP tools are disabled for this folder; use native file tools or fuse_reduce.");
        }

        return repositoryRoot;
    }

    /// <summary>
    ///     Resolves <paramref name="relativeOrAbsolute" /> under <paramref name="root" /> and refuses paths that
    ///     normalize outside the root.
    /// </summary>
    /// <param name="root">The workspace root directory.</param>
    /// <param name="relativeOrAbsolute">A repo-relative or absolute path that must stay under <paramref name="root" />.</param>
    /// <param name="action">The verb used in refusal messages (for example <c>write</c>, <c>check</c>, <c>reduce</c>).</param>
    /// <returns>
    ///     <see langword="true" /> and the confined absolute path when the path stays inside the root; otherwise
    ///     <see langword="false" /> and an actionable error string.
    /// </returns>
    internal static (bool Success, string? AbsolutePath, string? Error) ResolveWorkspacePath(
        string root,
        string relativeOrAbsolute,
        string action = "use")
    {
        if (string.IsNullOrWhiteSpace(relativeOrAbsolute))
            return (false, null, "Error: a file path is required.");

        var fullRoot = ResolveRoot(root);
        var absolute = Path.IsPathRooted(relativeOrAbsolute)
            ? Path.GetFullPath(relativeOrAbsolute)
            : Path.GetFullPath(Path.Combine(fullRoot, relativeOrAbsolute));

        if (!IsUnderRoot(fullRoot, absolute))
        {
            return (false, null,
                $"Error: refusing to {action} '{relativeOrAbsolute}': it resolves outside the workspace root. "
                + $"Paths must stay inside {fullRoot}.");
        }

        return (true, absolute, null);
    }

    /// <summary>
    ///     Validates every path in <paramref name="paths" /> against <paramref name="root" />.
    /// </summary>
    /// <param name="root">The workspace root directory.</param>
    /// <param name="paths">The paths to validate.</param>
    /// <param name="action">The verb used in refusal messages.</param>
    /// <returns>An error string when any path escapes the root; otherwise <see langword="null" />.</returns>
    internal static string? ValidateWorkspacePaths(string root, IEnumerable<string> paths, string action)
    {
        foreach (var path in paths)
        {
            var (_, _, error) = ResolveWorkspacePath(root, path, action);
            if (error is not null)
                return error;
        }

        return null;
    }

    /// <summary>
    ///     Returns the repo-relative form of an absolute path under <paramref name="root" />.
    /// </summary>
    /// <param name="root">The workspace root directory.</param>
    /// <param name="absolutePath">An absolute path already known to be under the root.</param>
    /// <returns>The path relative to the workspace root.</returns>
    internal static string ToRepoRelative(string root, string absolutePath) =>
        Path.GetRelativePath(ResolveRoot(root), absolutePath);

    // A normalized absolute path must start with the root directory prefix (case-insensitive on Windows).
    internal static bool IsUnderRoot(string fullRoot, string absolutePath)
    {
        var rootPrefix = fullRoot.EndsWith(Path.DirectorySeparatorChar)
            ? fullRoot
            : fullRoot + Path.DirectorySeparatorChar;
        return absolutePath.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase)
               || string.Equals(absolutePath, fullRoot, StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>Raised when a workspace-scoped MCP operation cannot resolve a repository identity.</summary>
internal sealed class WorkspaceIdentityException : Exception
{
    /// <summary>Initializes an identity-resolution failure with an actionable message.</summary>
    /// <param name="message">The resolution failure.</param>
    internal WorkspaceIdentityException(string message)
        : base(message)
    {
    }
}
