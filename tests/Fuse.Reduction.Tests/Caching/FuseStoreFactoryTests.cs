using Fuse.Collection.FileSystem;
using Fuse.Reduction.Caching;
using Microsoft.Data.Sqlite;

namespace Fuse.Reduction.Tests.Caching;

public sealed class FuseStoreFactoryTests
{
    [Fact]
    public async Task Open_SourceInsideGitRepo_PlacesDatabaseAtRepositoryRoot()
    {
        var repo = CreateTempDirectory();

        try
        {
            Directory.CreateDirectory(Path.Combine(repo, ".git"));
            var source = Path.Combine(repo, "src");
            Directory.CreateDirectory(source);

            await using (var store = new FuseStoreFactory().Open(source))
            {
                store.Set("test", "key", [1]);
                await store.FlushAsync();
            }

            var expectedPath = Path.Combine(repo, ".fuse", "fuse-cache.db");
            Assert.True(File.Exists(expectedPath));
            Assert.False(File.Exists(Path.Combine(repo, ".fuse", "fuse.db")));
            Assert.False(Directory.Exists(Path.Combine(source, ".fuse")));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(repo))
                Directory.Delete(repo, recursive: true);
        }
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "fuse-store-factory-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
