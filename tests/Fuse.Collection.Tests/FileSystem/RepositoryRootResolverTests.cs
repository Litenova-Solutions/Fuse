using Fuse.Collection.FileSystem;

namespace Fuse.Collection.Tests.FileSystem;

public sealed class RepositoryRootResolverTests
{
    [Fact]
    public void TryFindRepositoryRoot_StopsAtFirstGitDirectory()
    {
        var outer = CreateTempDirectory();

        try
        {
            Directory.CreateDirectory(Path.Combine(outer, ".git"));
            var inner = Path.Combine(outer, "nested", "inner");
            Directory.CreateDirectory(inner);

            Assert.Equal(Path.GetFullPath(outer), RepositoryRootResolver.TryFindRepositoryRoot(inner));
        }
        finally
        {
            Directory.Delete(outer, recursive: true);
        }
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "fuse-repo-root-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
