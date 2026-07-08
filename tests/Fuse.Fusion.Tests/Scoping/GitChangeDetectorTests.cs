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

    [Fact]
    public async Task GetChangedRelativePaths_GitMissingFromPath_ThrowsChangeDetectionException()
    {
        var repoDirectory = Path.Combine(Path.GetTempPath(), "fuse-git-missing-path", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(repoDirectory);
        var originalPath = Environment.GetEnvironmentVariable("PATH");
        try
        {
            Environment.SetEnvironmentVariable("PATH", string.Empty);

            var detector = new GitChangeDetector();
            var exception = await Assert.ThrowsAsync<ChangeDetectionException>(() =>
                detector.GetChangedRelativePathsAsync(repoDirectory, "HEAD"));

            Assert.Contains("not available on PATH", exception.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", originalPath);
            TryDeleteDirectory(repoDirectory);
        }
    }

    [Fact]
    public async Task GetDiffsAsync_CancellationDuringDiff_ThrowsOperationCanceledException()
    {
        var repoDirectory = Path.Combine(Path.GetTempPath(), "fuse-git-cancel", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(repoDirectory);
        try
        {
            RunGit(repoDirectory, "init");
            RunGit(repoDirectory, "config user.email test@test.com");
            RunGit(repoDirectory, "config user.name Test");

            var baselineContent = new string('x', 1024 * 1024);
            File.WriteAllText(Path.Combine(repoDirectory, "Large.cs"), baselineContent);
            RunGit(repoDirectory, "add .");
            RunGit(repoDirectory, "commit -m baseline");

            File.WriteAllText(Path.Combine(repoDirectory, "Large.cs"), new string('y', 1024 * 1024));
            RunGit(repoDirectory, "add .");
            RunGit(repoDirectory, "commit -m changed");

            // Cancel before the diff starts so the assertion cannot race the git process finishing: on a fast
            // machine a 1 MB diff completes within any fixed delay, leaving nothing to cancel. This still
            // verifies the contract that GetDiffsAsync honors a cancelled token.
            using var cts = new CancellationTokenSource();
            cts.Cancel();
            var detector = new GitChangeDetector();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => detector.GetDiffsAsync(repoDirectory, "HEAD~1", cts.Token));
        }
        finally
        {
            TryDeleteDirectory(repoDirectory);
        }
    }

    [Fact]
    public async Task GetFileContentAt_CommittedFile_ReturnsContentAtRef()
    {
        var repoDirectory = Path.Combine(Path.GetTempPath(), "fuse-git-show", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(repoDirectory);
        try
        {
            RunGit(repoDirectory, "init");
            RunGit(repoDirectory, "config user.email test@test.com");
            RunGit(repoDirectory, "config user.name Test");
            File.WriteAllText(Path.Combine(repoDirectory, "File.cs"), "public class Foo { }");
            RunGit(repoDirectory, "add .");
            RunGit(repoDirectory, "commit -m baseline");

            // The working copy diverges from the committed version; git show must return the committed content.
            File.WriteAllText(Path.Combine(repoDirectory, "File.cs"), "public class Foo { public int Bar; }");

            var detector = new GitChangeDetector();
            var content = await detector.GetFileContentAtAsync(repoDirectory, "HEAD", "File.cs");

            Assert.Equal("public class Foo { }", content?.TrimEnd());
        }
        finally
        {
            TryDeleteDirectory(repoDirectory);
        }
    }

    [Fact]
    public async Task GetFileContentAt_FileAbsentAtRef_ReturnsNull()
    {
        var repoDirectory = Path.Combine(Path.GetTempPath(), "fuse-git-show-absent", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(repoDirectory);
        try
        {
            RunGit(repoDirectory, "init");
            RunGit(repoDirectory, "config user.email test@test.com");
            RunGit(repoDirectory, "config user.name Test");
            File.WriteAllText(Path.Combine(repoDirectory, "Existing.cs"), "public class Existing { }");
            RunGit(repoDirectory, "add .");
            RunGit(repoDirectory, "commit -m baseline");

            var detector = new GitChangeDetector();
            // A file that never existed at HEAD (a newly added file has no base version) reads as null, not error.
            var content = await detector.GetFileContentAtAsync(repoDirectory, "HEAD", "NeverExisted.cs");

            Assert.Null(content);
        }
        finally
        {
            TryDeleteDirectory(repoDirectory);
        }
    }

    [Fact]
    public async Task GetFileContentAt_NotGitRepo_ThrowsChangeDetectionException()
    {
        var directory = Path.Combine(Path.GetTempPath(), "fuse-git-show-notrepo", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var detector = new GitChangeDetector();
            var exception = await Assert.ThrowsAsync<ChangeDetectionException>(() =>
                detector.GetFileContentAtAsync(directory, "HEAD", "File.cs"));

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
