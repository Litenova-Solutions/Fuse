using Fuse.Fusion.Enrichment;
using Fuse.Emission.Models;

namespace Fuse.Fusion.Tests.Enrichment;

public sealed class GitStatsProviderTests
{
    [Fact]
    public async Task GetStatsAsync_OutsideGitRepo_ReturnsUnavailable()
    {
        var directory = Path.Combine(Path.GetTempPath(), "fuse-git-stats-none", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var provider = new GitStatsProvider();
            var result = await provider.GetStatsAsync(directory, ["Program.cs"]);

            Assert.False(result.IsAvailable);
            Assert.Empty(result.StatsByPath);
        }
        finally
        {
            TryDeleteDirectory(directory);
        }
    }

    [Fact]
    public async Task GetStatsAsync_InGitRepo_ReturnsPerFileStats()
    {
        var repoDirectory = Path.Combine(Path.GetTempPath(), "fuse-git-stats", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(repoDirectory);
        try
        {
            InitializeGitRepo(repoDirectory);

            File.WriteAllText(Path.Combine(repoDirectory, "Hot.cs"), "public class Hot { }");
            RunGit(repoDirectory, "add", ".");
            RunGit(repoDirectory, "commit", "-m", "first");

            File.AppendAllText(Path.Combine(repoDirectory, "Hot.cs"), "\n// edit");
            RunGit(repoDirectory, "add", ".");
            RunGit(repoDirectory, "commit", "-m", "second");

            var provider = new GitStatsProvider();
            var result = await provider.GetStatsAsync(repoDirectory, ["Hot.cs"]);

            Assert.True(result.IsAvailable);
            Assert.True(result.StatsByPath.ContainsKey("Hot.cs"));
            Assert.True(result.StatsByPath["Hot.cs"].CommitCount >= 2);
            Assert.NotNull(result.StatsByPath["Hot.cs"].LastModified);
        }
        finally
        {
            TryDeleteDirectory(repoDirectory);
        }
    }

    [Fact]
    public async Task GetStatsAsync_MultipleFiles_ReturnsDistinctPerFileStats()
    {
        var repoDirectory = Path.Combine(Path.GetTempPath(), "fuse-git-stats-batch", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(repoDirectory);
        try
        {
            InitializeGitRepo(repoDirectory);

            File.WriteAllText(Path.Combine(repoDirectory, "Stable.cs"), "public class Stable { }");
            File.WriteAllText(Path.Combine(repoDirectory, "Hot.cs"), "public class Hot { }");
            RunGit(repoDirectory, "add", ".");
            RunGit(repoDirectory, "commit", "-m", "seed");

            for (var i = 0; i < 2; i++)
            {
                File.AppendAllText(Path.Combine(repoDirectory, "Hot.cs"), $"\n// hot edit {i}");
                RunGit(repoDirectory, "add", "Hot.cs");
                RunGit(repoDirectory, "commit", "-m", $"hot {i}");
            }

            File.WriteAllText(Path.Combine(repoDirectory, "Warm.cs"), "public class Warm { }");
            RunGit(repoDirectory, "add", "Warm.cs");
            RunGit(repoDirectory, "commit", "-m", "warm");

            File.WriteAllText(Path.Combine(repoDirectory, "Untracked.cs"), "public class Untracked { }");

            var provider = new GitStatsProvider();
            var result = await provider.GetStatsAsync(
                repoDirectory,
                ["Hot.cs", "Warm.cs", "Stable.cs", "Untracked.cs"]);

            Assert.True(result.IsAvailable);
            Assert.Equal(4, result.StatsByPath.Count);

            Assert.True(result.StatsByPath["Hot.cs"].CommitCount >= 3);
            Assert.NotNull(result.StatsByPath["Hot.cs"].LastModified);

            Assert.Equal(1, result.StatsByPath["Warm.cs"].CommitCount);
            Assert.NotNull(result.StatsByPath["Warm.cs"].LastModified);

            Assert.Equal(1, result.StatsByPath["Stable.cs"].CommitCount);
            Assert.NotNull(result.StatsByPath["Stable.cs"].LastModified);

            Assert.Equal(0, result.StatsByPath["Untracked.cs"].CommitCount);
            Assert.Null(result.StatsByPath["Untracked.cs"].LastModified);

            Assert.True(result.StatsByPath["Hot.cs"].CommitCount > result.StatsByPath["Warm.cs"].CommitCount);
            Assert.True(result.StatsByPath["Warm.cs"].LastModified >= result.StatsByPath["Stable.cs"].LastModified);
        }
        finally
        {
            TryDeleteDirectory(repoDirectory);
        }
    }

    [Fact]
    public async Task GetStatsAsync_ManyTrackedPaths_ReturnsStatsForEach()
    {
        var repoDirectory = Path.Combine(Path.GetTempPath(), "fuse-git-stats-many", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(repoDirectory);
        try
        {
            InitializeGitRepo(repoDirectory);

            var paths = new List<string>();
            for (var i = 0; i < 30; i++)
            {
                var fileName = $"File{i:D3}.cs";
                paths.Add(fileName);
                File.WriteAllText(Path.Combine(repoDirectory, fileName), $"public class File{i} {{ }}");
            }

            RunGit(repoDirectory, "add", ".");
            RunGit(repoDirectory, "commit", "-m", "seed many files");

            var provider = new GitStatsProvider();
            var result = await provider.GetStatsAsync(repoDirectory, paths);

            Assert.True(result.IsAvailable);
            Assert.Equal(paths.Count, result.StatsByPath.Count);
            foreach (var path in paths)
            {
                Assert.True(result.StatsByPath.ContainsKey(path));
                Assert.True(result.StatsByPath[path].CommitCount >= 1);
                Assert.NotNull(result.StatsByPath[path].LastModified);
            }
        }
        finally
        {
            TryDeleteDirectory(repoDirectory);
        }
    }

    private static void InitializeGitRepo(string repoDirectory)
    {
        RunGit(repoDirectory, "init");
        RunGit(repoDirectory, "config", "user.email", "test@test.com");
        RunGit(repoDirectory, "config", "user.name", "Test");
    }

    private static void RunGit(string workingDirectory, params string[] args)
    {
        var psi = new System.Diagnostics.ProcessStartInfo("git")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in args)
        {
            psi.ArgumentList.Add(arg);
        }

        using var process = System.Diagnostics.Process.Start(psi)!;
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"git {string.Join(' ', args)} failed: {process.StandardError.ReadToEnd()}");
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch
        {
        }
    }
}
