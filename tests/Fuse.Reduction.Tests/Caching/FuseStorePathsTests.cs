using Fuse.Reduction.Caching;

namespace Fuse.Reduction.Tests.Caching;

public sealed class FuseStorePathsTests
{
    [Fact]
    public void ResolveDatabasePath_InsideGitRepo_ReturnsRepositoryRelativePath()
    {
        var repo = CreateTempDirectory();

        try
        {
            Directory.CreateDirectory(Path.Combine(repo, ".git"));
            var nested = Path.Combine(repo, "src", "Api");
            Directory.CreateDirectory(nested);

            var expected = Path.Combine(repo, ".fuse", "fuse.db");
            Assert.Equal(Path.GetFullPath(expected), FuseStorePaths.ResolveDatabasePath(nested));
        }
        finally
        {
            Directory.Delete(repo, recursive: true);
        }
    }

    [Fact]
    public void ResolveDatabasePath_NoGitDirectory_ReturnsUserDataPath()
    {
        var root = CreateTempDirectory();
        var userData = Path.Combine(root, "user-data");
        Directory.CreateDirectory(userData);
        Environment.SetEnvironmentVariable(FuseStorePaths.UserDataEnvironmentVariable, userData);

        try
        {
            var nested = Path.Combine(root, "src", "Api");
            Directory.CreateDirectory(nested);

            var expected = Path.Combine(userData, FuseStorePaths.DatabaseFileName);
            Assert.Equal(Path.GetFullPath(expected), FuseStorePaths.ResolveDatabasePath(nested));
        }
        finally
        {
            Environment.SetEnvironmentVariable(FuseStorePaths.UserDataEnvironmentVariable, null);
            Directory.Delete(root, recursive: true);
        }
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "fuse-store-paths-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
