using Fuse.Dotnet;
using Fuse.Telemetry;
using Fuse.Testing.Model;
using Fuse.Tests.Fixtures;

namespace Fuse.Tests.Engine;

/// <summary>
///     A dependent compiled against an intermediate default value is emitted again once its referenced project declares
///     the same as HEAD: comparing only with HEAD would keep the stale image.
/// </summary>
public class ShadowDefaultValueTests
{
    [Fact]
    public async Task Reverted_default_emits_the_dependent_again()
    {
        var repo = FixtureRepo.CreateStandard();
        repo.Replace("Lib/Calc.cs", "public int Mul(int a, int b) => a * b;", "public int Mul(int a, int b) => a * b;\n\n    public int Get(int v = 1) => v;");
        repo.Replace(
            "Lib.Tests/CalcTests.cs",
            "public void Multiplies() => Assert.Equal(6, NewCalc().Mul(2, 3));",
            "public void Multiplies() => Assert.Equal(6, NewCalc().Mul(2, 3));\n\n        [Fact]\n        public void GetsDefault() => Assert.Equal(1, NewCalc().Get());");
        repo.Commit("a method with a default and its test");
        await using var engine = await InProcessEngine.StartAsync(repo);
        engine.Repo.Replace("Lib/Calc.cs", "public int Get(int v = 1)", "public int Get(int v = 2)");
        await Task.Delay(400, TestContext.Current.CancellationToken);
        var first = await engine.Planner.PlanAsync(new TestScope.Affected(), PhaseTimes.None, TestContext.Current.CancellationToken);
        Assert.NotEmpty(first.Runs);

        engine.Repo.Replace("Lib/Calc.cs", "public int Get(int v = 2) => v;", "public int Get(int v = 1) => v + 0;");
        await Task.Delay(400, TestContext.Current.CancellationToken);
        var second = await engine.Planner.PlanAsync(new TestScope.Affected(), PhaseTimes.None, TestContext.Current.CancellationToken);

        // The test image inlines the default it was compiled against, so a kept image from the intermediate state fails.
        var run = Assert.Single(second.Runs);
        var outcome = await RunAsync(engine.Repo, Assert.IsType<RunMode.Shadow>(run.Mode), run.Filter);
        Assert.Equal(0, outcome.Failed);
        Assert.True(outcome.Passed > 0, "the shadow run ran no tests");
    }

    /// <summary>Runs a shadow run's test assembly with <c>dotnet test</c>, the way <c>fuse test</c> does, and reads its results.</summary>
    private static async Task<TrxResults> RunAsync(FixtureRepo repo, RunMode.Shadow shadow, string? filter)
    {
        var results = FixtureRepo.NewDirectory();
        try
        {
            string[] arguments = ["test", shadow.Assembly, .. filter is null ? Array.Empty<string>() : ["--filter", filter], "--logger", "trx", "--results-directory", results];
            var run = await ProcessRunner.RunAsync("dotnet", arguments, repo.Root.Path, TestContext.Current.CancellationToken);
            return TrxReader.ReadDirectory(results, repo.Root.Path) ?? throw new InvalidOperationException($"dotnet test wrote no results (exit code {run.ExitCode}):\n{run.Output}");
        }
        finally
        {
            Directory.Delete(results, recursive: true);
        }
    }
}
