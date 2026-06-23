using Fuse.Collection.FileSystem;

namespace Fuse.Reduction.Caching;

/// <summary>
///     Resolves on-disk paths for the persistent Fuse SQLite store.
/// </summary>
public static class FuseStorePaths
{
    /// <summary>
    ///     Environment variable overriding the machine-wide Fuse data directory (default
    ///     <c>~/.fuse</c>). Used for tests and custom install layouts.
    /// </summary>
    public const string UserDataEnvironmentVariable = "FUSE_USER_DATA";

    /// <summary>The database file name inside the Fuse data directory.</summary>
    public const string DatabaseFileName = "fuse.db";

    /// <summary>
    ///     The relative path from a git repository root to the store database
    ///     (<c>.fuse/fuse.db</c>).
    /// </summary>
    public const string RepositoryDatabaseRelativePath = ".fuse/fuse.db";

    /// <summary>
    ///     Returns the machine-wide Fuse data directory (<c>~/.fuse</c> by default).
    /// </summary>
    public static string GetUserDataDirectory()
    {
        var overridePath = Environment.GetEnvironmentVariable(UserDataEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(overridePath))
            return Path.GetFullPath(overridePath);

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".fuse");
    }

    /// <summary>
    ///     Resolves the absolute path to <c>fuse.db</c> for a fusion source directory.
    /// </summary>
    /// <param name="sourceDirectory">The fusion source directory.</param>
    /// <returns>
    ///     <c>{repoRoot}/.fuse/fuse.db</c> when inside a git repository; otherwise
    ///     <c>~/.fuse/fuse.db</c> (or <see cref="UserDataEnvironmentVariable" />).
    /// </returns>
    public static string ResolveDatabasePath(string sourceDirectory)
    {
        var repositoryRoot = RepositoryRootResolver.TryFindRepositoryRoot(sourceDirectory);
        if (repositoryRoot is not null)
        {
            return Path.Combine(
                repositoryRoot,
                RepositoryDatabaseRelativePath.Replace('/', Path.DirectorySeparatorChar));
        }

        return Path.Combine(GetUserDataDirectory(), DatabaseFileName);
    }
}
