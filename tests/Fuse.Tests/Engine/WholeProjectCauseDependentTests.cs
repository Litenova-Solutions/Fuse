using Fuse.Tests.Fixtures;

namespace Fuse.Tests.Engine;

/// <summary>
///     A check that binds whole projects names every reached project, including a dependent that owns no candidate and
///     no target: it was still bound whole, and names the changed files that forced it.
/// </summary>
public class WholeProjectCauseDependentTests
{
    [Fact]
    public async Task A_dependent_that_owns_no_candidate_is_still_named_when_whole()
    {
        var repo = FixtureRepo.CreateStandard();
        for (var i = 0; i < 510; i++)
            repo.Write($"Lib/Generated/Part{i}.cs", $"namespace Lib;\n\npublic static class Part{i}\n{{\n    public static int Use() => new Calc().Add(1, 2);\n}}\n");
        repo.Commit("fixture files that call Add");
        await using var engine = await InProcessEngine.StartAsync(repo);
        engine.Repo.Replace("Lib/Calc.cs", "public int Add(", "public int Plus(");

        var report = await engine.CheckAsync("Lib/Calc.cs");

        Assert.True(report.CheckedWholeProjects);
        var appTests = Assert.Single(report.WholeProjectCauses, c => c.Project == "App.Tests");
        Assert.Contains("Lib/Calc.cs", appTests.ChangedFiles);
    }
}
