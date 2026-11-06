using Fuse.Tests.Fixtures;

namespace Fuse.Tests.EndToEnd;

/// <summary>
///     A build or a test run whose tree, projects and arguments equal the last finished one's prints that result
///     instead of running again. These cases run the real executable, because the reuse crosses processes through the
///     state directory, which an in-process test could not show.
/// </summary>
public class BuildReuseTests
{
    [Fact]
    public async Task A_second_identical_build_prints_the_first_ones_result()
    {
        using var repo = FixtureRepo.CreateStandard();
        try
        {
            repo.Replace("Lib/Calc.cs", "public int Add(", "public int Plus(");
            var first = await FuseProcess.RunAsync(repo.Path, null, "build");
            Assert.Equal(1, first.ExitCode);
            Assert.DoesNotContain("reused from", first.Stdout, StringComparison.Ordinal);

            var second = await FuseProcess.RunAsync(repo.Path, null, "build");
            Assert.Equal(1, second.ExitCode);
            Assert.Contains("reused from", second.Stdout, StringComparison.Ordinal);
            Assert.Contains("tree ", second.Stdout, StringComparison.Ordinal);
            Assert.Contains("error CS", second.Stdout, StringComparison.Ordinal);

            repo.Replace("Lib/Calc.cs", "public int Plus(", "public int Add(");
            var third = await FuseProcess.RunAsync(repo.Path, null, "build");
            Assert.Equal(0, third.ExitCode);
            Assert.DoesNotContain("reused from", third.Stdout, StringComparison.Ordinal);
        }
        finally
        {
            await FuseProcess.StopEngineAsync(repo.Root);
        }
    }

    [Fact]
    public async Task A_second_identical_test_run_prints_the_first_ones_result()
    {
        using var repo = FixtureRepo.CreateStandard();
        try
        {
            repo.Replace("Lib/Calc.cs", "a * b", "a - b");
            var first = await FuseProcess.RunAsync(repo.Path, null, "test");
            Assert.Equal(1, first.ExitCode);
            Assert.DoesNotContain("reused from", first.Stdout, StringComparison.Ordinal);

            var second = await FuseProcess.RunAsync(repo.Path, null, "test");
            Assert.Equal(1, second.ExitCode);
            Assert.Contains("test run reused from", second.Stdout, StringComparison.Ordinal);
            Assert.Contains("tree ", second.Stdout, StringComparison.Ordinal);
            Assert.Contains("FAILED", second.Stdout, StringComparison.Ordinal);
        }
        finally
        {
            await FuseProcess.StopEngineAsync(repo.Root);
        }
    }
}
