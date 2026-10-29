using Fuse.Tests.Fixtures;

namespace Fuse.Tests.EndToEnd;

/// <summary>
///     Over the standard fixture through the real <c>fuse</c> process, a second <c>fuse test</c> answers the passing
///     class from the cache, and a class broken after it was cached reruns and fails.
/// </summary>
public class CachedTestRunTests
{
    [Fact]
    public async Task A_second_test_answers_the_passing_class_from_cache()
    {
        using var repo = FixtureRepo.CreateStandard();
        try
        {
            repo.Replace("Lib/Calc.cs", "a * b;", "b * a;");
            var first = await FuseProcess.RunAsync(repo.Path, null, "test");
            Assert.Equal(0, first.ExitCode);
            Assert.Contains("0 failed, 1 passed", first.Stdout, StringComparison.Ordinal);
            Assert.Contains("without MSBuild", first.Stdout, StringComparison.Ordinal);
            Assert.DoesNotContain("from cache", first.Stdout, StringComparison.Ordinal);

            // Strict is reached by no test, so the cached key still holds.
            repo.Replace("Strict/Thing.cs", "=> 1;", "=> 2;");
            var second = await FuseProcess.RunAsync(repo.Path, null, "test");
            Assert.Equal(0, second.ExitCode);
            Assert.Contains("0 failed, 1 passed", second.Stdout, StringComparison.Ordinal);
            Assert.Contains("1 result(s) from cache", second.Stdout, StringComparison.Ordinal);

            var all = await FuseProcess.RunAsync(repo.Path, null, "test", "--all");
            Assert.Equal(0, all.ExitCode);
            Assert.DoesNotContain("from cache", all.Stdout, StringComparison.Ordinal);
        }
        finally
        {
            await FuseProcess.StopEngineAsync(repo.Root);
        }
    }

    [Fact]
    public async Task A_class_broken_after_it_was_cached_reruns_and_fails()
    {
        using var repo = FixtureRepo.CreateStandard();
        try
        {
            repo.Replace("Lib/Calc.cs", "a * b;", "b * a;");
            var first = await FuseProcess.RunAsync(repo.Path, null, "test");
            Assert.Equal(0, first.ExitCode);
            Assert.Contains("without MSBuild", first.Stdout, StringComparison.Ordinal);

            repo.Replace("Lib/Calc.cs", "b * a;", "b * a + 1;");
            var second = await FuseProcess.RunAsync(repo.Path, null, "test");
            Assert.Equal(1, second.ExitCode);
            Assert.Contains("FAILED Lib.Tests.CalcTests.Multiplies", second.Stdout, StringComparison.Ordinal);
            Assert.DoesNotContain("from cache", second.Stdout, StringComparison.Ordinal);
        }
        finally
        {
            await FuseProcess.StopEngineAsync(repo.Root);
        }
    }
}
