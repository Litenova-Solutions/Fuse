using Fuse.Analysis.Git;

namespace Fuse.Analysis.Tests.Git;

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
            RunGit(repoDirectory, "init");
            RunGit(repoDirectory, "config user.email test@test.com");
            RunGit(repoDirectory, "config user.name Test");

            File.WriteAllText(Path.Combine(repoDirectory, "Hot.cs"), "public class Hot { }");
            RunGit(repoDirectory, "add .");
            RunGit(repoDirectory, "commit -m first");

            File.AppendAllText(Path.Combine(repoDirectory, "Hot.cs"), "\n// edit");
            RunGit(repoDirectory, "add .");
            RunGit(repoDirectory, "commit -m second");

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
        {
            throw new InvalidOperationException(
                $"git {args} failed: {process.StandardError.ReadToEnd()}");
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
