using Fuse.Tests.Fixtures;

namespace Fuse.Tests.EndToEnd;

public class ChangedBuildProcessTests
{
    [Fact]
    public async Task Changed_build_builds_the_leaves_nothing_when_clean_and_the_solution_when_project_files_change()
    {
        using var repo = FixtureRepo.CreateStandard();
        try
        {
            var clean = await FuseProcess.RunAsync(repo.Path, null, "build", "--changed");
            Assert.Equal(0, clean.ExitCode);
            Assert.Contains("fuse: no C# changes since HEAD; nothing to build (tree ", clean.Stdout, StringComparison.Ordinal);

            repo.Replace("Lib/Calc.cs", "public int Add(int a, int b) => a + b;", "public int Add(int a, int b) => a - b;");
            await FuseProcess.WaitForStatusAsync(repo.Path, stdout => stdout.Contains("1 changed file(s)", StringComparison.Ordinal), "fuse status never reported the edit");
            var changed = await FuseProcess.RunAsync(repo.Path, null, "build", "--changed");
            Assert.Equal(0, changed.ExitCode);
            var first = changed.Stdout.Split('\n')[0];
            Assert.StartsWith("fuse: building 2 project(s) for 1 changed file(s) (tree ", first, StringComparison.Ordinal);
            Assert.Contains(": App.Tests, Lib.Tests; changes in Lib", first, StringComparison.Ordinal);
            Assert.Contains("fuse: build (App.Tests) succeeded in ", changed.Stdout, StringComparison.Ordinal);
            Assert.Contains("fuse: build (Lib.Tests) succeeded in ", changed.Stdout, StringComparison.Ordinal);
            Assert.Equal("fuse: 0 of 2 project(s) failed", changed.Stdout.Split('\n')[^1]);
            Assert.DoesNotContain("build (Multi)", changed.Stdout, StringComparison.Ordinal);
            Assert.DoesNotContain("build (Strict)", changed.Stdout, StringComparison.Ordinal);
            Assert.Contains("; tree ", changed.Stdout, StringComparison.Ordinal);
            var header = File.ReadLines(Path.Combine(repo.Root.StateDirectory, "last-build-results.txt")).First();
            Assert.StartsWith("fuse build: 0 error(s); tree ", header, StringComparison.Ordinal);

            repo.Replace("Lib/Lib.csproj", "</Project>", "<!-- build -->\n</Project>");
            var fallback = await FuseProcess.RunAsync(repo.Path, null, "build", "--changed");
            Assert.Equal(0, fallback.ExitCode);
            Assert.StartsWith("fuse: project files changed (Lib/Lib.csproj); building the whole solution instead", fallback.Stdout.Split('\n')[0], StringComparison.Ordinal);
            Assert.Contains("fuse: build succeeded in ", fallback.Stdout, StringComparison.Ordinal);
        }
        finally
        {
            await FuseProcess.StopEngineAsync(repo.Root);
        }
    }

    [Fact]
    public async Task Changed_build_outside_a_repository_exits_2()
    {
        var result = await FuseProcess.RunAsync(Path.GetTempPath(), null, "build", "--changed");

        Assert.Equal(2, result.ExitCode);
        Assert.Contains("not inside a git repository", result.Stderr, StringComparison.Ordinal);
    }
}
