using Fuse.Fusion.Scoping;

namespace Fuse.Fusion.Tests.Scoping;

public class GitChangeDetectorTests
{
    [Fact]
    public async Task GetChangedRelativePaths_ReturnsNormalizedPaths()
    {
        var detector = new StubChangeDetector(["src\\Foo.cs", "Bar/Baz.cs"]);
        var paths = await detector.GetChangedRelativePathsAsync("/repo", "main");
        Assert.All(paths, p => Assert.DoesNotContain('\\', p));
        Assert.Contains("src/Foo.cs", paths);
    }

    [Fact]
    public async Task GetChangedRelativePaths_EmptyOutput_ReturnsEmptyList()
    {
        var detector = new StubChangeDetector([]);
        var paths = await detector.GetChangedRelativePathsAsync("/repo", "main");
        Assert.Empty(paths);
    }

    [Fact]
    public async Task GetChangedRelativePaths_EmptySinceRef_ThrowsOrReturnsEmpty()
    {
        var repoDirectory = Path.Combine(Path.GetTempPath(), "fuse-git-empty-ref", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(repoDirectory);
        try
        {
            RunGit(repoDirectory, "init");
            RunGit(repoDirectory, "config user.email test@test.com");
            RunGit(repoDirectory, "config user.name Test");
            File.WriteAllText(Path.Combine(repoDirectory, "File.cs"), "public class File { }");
            RunGit(repoDirectory, "add .");
            RunGit(repoDirectory, "commit -m baseline");

            var detector = new GitChangeDetector();

            await Assert.ThrowsAsync<ChangeDetectionException>(() =>
                detector.GetChangedRelativePathsAsync(repoDirectory, "not-a-valid-ref-xyz"));
        }
        finally
        {
            TryDeleteDirectory(repoDirectory);
        }
    }

    [Fact]
    public async Task GetChangedRelativePaths_NotGitRepo_ThrowsChangeDetectionException()
    {
        var directory = Path.Combine(Path.GetTempPath(), "fuse-not-git", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var detector = new GitChangeDetector();

            var exception = await Assert.ThrowsAsync<ChangeDetectionException>(() =>
                detector.GetChangedRelativePathsAsync(directory, "HEAD"));

            Assert.Contains("not a git repository", exception.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            TryDeleteDirectory(directory);
        }
    }

    private static void RunGit(string workingDirectory, string args)
    {
        var psi = new System.Diagnostics.ProcessStartInfo("git", args)
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var process = System.Diagnostics.Process.Start(psi)!;
        process.WaitForExit();
        if (process.ExitCode != 0)
            throw new InvalidOperationException(process.StandardError.ReadToEnd());
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private sealed class StubChangeDetector(IReadOnlyList<string> paths) : IChangeDetector
    {
        public Task<IReadOnlyList<string>> GetChangedRelativePathsAsync(
            string sourceDirectory,
            string since,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<string>>(paths
                .Select(p => p.Replace('\\', '/'))
                .ToArray());
        }
    }
}
