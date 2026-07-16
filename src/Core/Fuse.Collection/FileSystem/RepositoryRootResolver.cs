namespace Fuse.Collection.FileSystem;

/// <summary>
///     Locates the git repository root for a path on disk.
/// </summary>
public static class RepositoryRootResolver
{
    /// <summary>
    ///     Walks upward from <paramref name="startDirectory" /> until a directory containing a <c>.git</c> marker is found.
    /// </summary>
    /// <param name="startDirectory">The directory to start from (typically the fusion source directory).</param>
    /// <returns>The repository root, or <see langword="null" /> when no <c>.git</c> marker is found.</returns>
    public static string? TryFindRepositoryRoot(string startDirectory)
    {
        var current = Path.GetFullPath(startDirectory);
        if (!Directory.Exists(current))
        {
            var parent = Path.GetDirectoryName(current);
            if (string.IsNullOrEmpty(parent))
                return null;
            current = parent;
        }

        while (!string.IsNullOrEmpty(current))
        {
            var marker = Path.Combine(current, ".git");
            if (Directory.Exists(marker) || File.Exists(marker))
                return current;

            var parent = Directory.GetParent(current);
            if (parent is null)
                return null;

            current = parent.FullName;
        }

        return null;
    }
}
