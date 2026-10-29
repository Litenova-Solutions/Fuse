using System.Text;
using Fuse.Dotnet;
using Fuse.Telemetry;
using Fuse.Testing.Model;
using Fuse.Tests.Fixtures;

namespace Fuse.Tests.Engine;

/// <summary>
///     A dependent of a project that declares the same as HEAD is not emitted again: the shadow folder keeps the image
///     the last plan wrote. A dependent whose referenced declaration changed is still emitted again.
/// </summary>
public class UnchangedDependentEmitTests
{
    [Fact]
    public async Task Body_only_edit_between_two_plans_emits_only_the_stale_project()
    {
        await using var engine = await InProcessEngine.StartAsync();
        engine.Repo.Replace("Lib/Calc.cs", "a * b;", "a * b + \"first\".Length;");
        await Task.Delay(400, TestContext.Current.CancellationToken);
        var first = await engine.Planner.PlanAsync(new TestScope.Affected(), PhaseTimes.None, TestContext.Current.CancellationToken);
        var emits = engine.Planner.Emitter.Emits;
        var tested = ShadowFile(first, "Lib.Tests.dll");
        var written = File.GetLastWriteTimeUtc(tested);

        engine.Repo.Replace("Lib/Calc.cs", "\"first\"", "\"second\"");
        await Task.Delay(400, TestContext.Current.CancellationToken);
        var second = await engine.Planner.PlanAsync(new TestScope.Affected(), PhaseTimes.None, TestContext.Current.CancellationToken);

        Assert.Equal(2, emits);
        Assert.Equal(emits + 1, engine.Planner.Emitter.Emits);
        Assert.Equal(tested, ShadowFile(second, "Lib.Tests.dll"));
        Assert.Equal(written, File.GetLastWriteTimeUtc(tested));
        var lib = File.ReadAllBytes(ShadowFile(second, "Lib.dll"));
        Assert.True(HoldsString(lib, "second"), "the shadow Lib.dll does not hold the second edit");
        Assert.False(HoldsString(lib, "first"), "the shadow Lib.dll still holds the first edit");
        Assert.Contains(engine.Log, line => line.Contains("not emitting Lib.Tests again", StringComparison.Ordinal));

        // The kept test assembly binds the newly emitted library, so the selected test still fails as the edit requires.
        var run = Assert.Single(second.Runs);
        var outcome = await RunAsync(engine.Repo, Assert.IsType<RunMode.Shadow>(run.Mode), run.Filter);
        Assert.Equal((1, 0), (outcome.Failed, outcome.Passed));
    }

    [Fact]
    public async Task Renamed_member_emits_the_dependent_again()
    {
        var repo = FixtureRepo.CreateStandard();
        repo.Replace("Lib/Calc.cs", "public int Mul(int a, int b) => a * b;", "public int Mul(int a, int b) => a * b;\n\n    public int Unused(int a) => a;");
        repo.Commit("add an uncalled member to Lib");
        await using var engine = await InProcessEngine.StartAsync(repo);
        engine.Repo.Replace("Lib/Calc.cs", "a * b;", "a * b + 1;");
        await Task.Delay(400, TestContext.Current.CancellationToken);
        var first = await engine.Planner.PlanAsync(new TestScope.Affected(), PhaseTimes.None, TestContext.Current.CancellationToken);
        var emits = engine.Planner.Emitter.Emits;
        Assert.NotEmpty(first.Runs);

        engine.Repo.Replace("Lib/Calc.cs", "public int Unused(", "public int UnusedRenamed(");
        await Task.Delay(400, TestContext.Current.CancellationToken);
        var second = await engine.Planner.PlanAsync(new TestScope.Affected(), PhaseTimes.None, TestContext.Current.CancellationToken);

        Assert.Equal(2, emits);
        Assert.Equal(emits + 2, engine.Planner.Emitter.Emits);
        Assert.NotEmpty(second.Runs);
        Assert.DoesNotContain(engine.Log, line => line.Contains("not emitting", StringComparison.Ordinal));
    }

    /// <summary>The file named <paramref name="name"/> next to the single shadow run's test assembly.</summary>
    private static string ShadowFile(TestPlanResult plan, string name)
    {
        var shadow = Assert.IsType<RunMode.Shadow>(Assert.Single(plan.Runs).Mode);
        return Path.Combine(Path.GetDirectoryName(shadow.Assembly)!, name);
    }

    /// <summary>Whether an assembly holds <paramref name="text"/> as a string literal, which the metadata stores in UTF-16.</summary>
    private static bool HoldsString(byte[] assembly, string text) =>
        assembly.AsSpan().IndexOf(Encoding.Unicode.GetBytes(text)) >= 0;

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
