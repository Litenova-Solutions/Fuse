using Fuse.Tests.Fixtures;

namespace Fuse.Tests.EndToEnd;

public class FailedRunTests
{
    [Fact]
    public async Task Failed_reruns_only_the_last_failures_until_none_are_left()
    {
        using var repo = FixtureRepo.CreateStandard();
        try
        {
            repo.Replace("Lib/Calc.cs", "a * b", "a - b");
            var test = await FuseProcess.RunAsync(repo.Path, null, "test");
            Assert.Equal(1, test.ExitCode);
            Assert.Contains("FAILED Lib.Tests.CalcTests.Multiplies", test.Stdout, StringComparison.Ordinal);

            var rerun = await FuseProcess.RunAsync(repo.Path, null, "test", "--failed");
            Assert.Equal(1, rerun.ExitCode);
            Assert.Contains("FAILED Lib.Tests.CalcTests.Multiplies", rerun.Stdout, StringComparison.Ordinal);
            Assert.Contains("1 failed, 0 passed", rerun.Stdout, StringComparison.Ordinal);
            Assert.Contains("reran 1 test(s) that failed last time in 1 test project(s)", rerun.Stdout, StringComparison.Ordinal);

            repo.Replace("Lib/Calc.cs", "a - b", "a * b");
            await FuseProcess.WaitForStatusAsync(repo.Path, stdout => stdout.Contains("0 changed file(s)", StringComparison.Ordinal), "fuse status never reported the revert");
            var fixedRerun = await FuseProcess.RunAsync(repo.Path, null, "test", "--failed");
            Assert.Equal(0, fixedRerun.ExitCode);
            Assert.Contains("0 failed, 1 passed", fixedRerun.Stdout, StringComparison.Ordinal);

            var clean = await FuseProcess.RunAsync(repo.Path, null, "test", "--failed");
            Assert.Equal(0, clean.ExitCode);
            Assert.Equal("fuse: the last test run had no failures", clean.Stdout);

            repo.Replace("Lib/Calc.cs", "a * b", "a - b");
            var arguments = await FuseProcess.RunAsync(repo.Path, null, "test", "Lib.Tests", "--filter", "FullyQualifiedName~Multiplies");
            Assert.Equal(1, arguments.ExitCode);
            Assert.Contains("FAILED Lib.Tests.CalcTests.Multiplies", arguments.Stdout, StringComparison.Ordinal);

            var afterArguments = await FuseProcess.RunAsync(repo.Path, null, "test", "--failed");
            Assert.Equal(2, afterArguments.ExitCode);
            Assert.Equal("fuse: the last test run was from dotnet test arguments, so its failures cannot be rerun; run fuse test", afterArguments.Stdout);
        }
        finally
        {
            await FuseProcess.StopEngineAsync(repo.Root);
        }
    }

    [Fact]
    public async Task Failed_without_a_recorded_run_runs_nothing()
    {
        using var repo = FixtureRepo.CreateEmpty(new Dictionary<string, string> { ["a.txt"] = "x" });
        try
        {
            var result = await FuseProcess.RunAsync(repo.Path, null, "test", "--failed");

            Assert.Equal(2, result.ExitCode);
            Assert.Equal("fuse: no test run is recorded yet; run fuse test first", result.Stdout);
        }
        finally
        {
            await FuseProcess.StopEngineAsync(repo.Root);
        }
    }

    [Theory]
    [InlineData("test", "--failed", "--all")]
    [InlineData("test", "--all", "--failed")]
    public async Task Failed_with_other_arguments_is_a_usage_failure(params string[] arguments)
    {
        using var repo = FixtureRepo.CreateEmpty(new Dictionary<string, string> { ["a.txt"] = "x" });
        var result = await FuseProcess.RunAsync(repo.Path, null, arguments);

        Assert.Equal(2, result.ExitCode);
        Assert.Contains("--failed", result.Stdout + result.Stderr, StringComparison.Ordinal);
    }
}
