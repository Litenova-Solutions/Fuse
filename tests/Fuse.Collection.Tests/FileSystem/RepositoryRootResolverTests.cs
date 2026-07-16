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

    [Fact]
    public void TryFindRepositoryRoot_AcceptsGitFileUsedByWorktrees()
    {
        var root = CreateTempDirectory();

        try
        {
            File.WriteAllText(Path.Combine(root, ".git"), "gitdir: C:/tmp/example-worktree");
            var nested = Path.Combine(root, "src", "Feature");
            Directory.CreateDirectory(nested);

            Assert.Equal(Path.GetFullPath(root), RepositoryRootResolver.TryFindRepositoryRoot(nested));
            Assert.True(WorkspaceIdentityResolver.TryResolveRepositoryRoot(nested, out var identity));
            Assert.Equal(Path.GetFullPath(root), identity);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void WorkspaceIdentityResolver_RefusesDirectoryOutsideGit()
    {
        var root = CreateTempDirectory();

        try
        {
            Assert.False(WorkspaceIdentityResolver.TryResolveRepositoryRoot(root, out var identity));
            Assert.Equal(string.Empty, identity);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void WorkspaceIdentityResolver_RefusesMissingPathInsideGit()
    {
        var root = CreateTempDirectory();

        try
        {
            Directory.CreateDirectory(Path.Combine(root, ".git"));
            var missing = Path.Combine(root, "missing", "workspace");

            Assert.False(WorkspaceIdentityResolver.TryResolveRepositoryRoot(missing, out var identity));
            Assert.Equal(string.Empty, identity);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "fuse-repo-root-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
