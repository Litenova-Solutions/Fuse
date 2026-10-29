using Fuse.Check.Model;
using Fuse.Tests.Fixtures;

namespace Fuse.Tests.Engine;

/// <summary>
///     A check that binds whole projects names the changed files that forced it, per project, with the declaration
///     changes that reached them. An incremental check names none.
/// </summary>
public class WholeProjectCauseTests
{
    [Fact]
    public async Task Past_five_hundred_candidates_each_whole_project_names_the_forcing_file()
    {
        await using var engine = await StartWithAsync(
            [.. Enumerable.Range(0, 510).Select(i => ($"Strict/Generated/Part{i}.cs", $"namespace Strict;\n\ninternal static class Part{i}\n{{\n}}\n"))]);
        engine.Repo.Replace("Strict/Thing.cs", "public static class Thing", "public static partial class Thing");
        engine.Repo.Replace("Strict/Thing.cs", "public static int Value() => 1;", "public static int Value() => new int[0].Length + 1;");

        var report = await engine.CheckAsync("Strict/Thing.cs");

        Assert.True(report.CheckedWholeProjects);
        Assert.NotEmpty(report.WholeProjectCauses);
        var strict = Assert.Single(report.WholeProjectCauses, c => c.Project == "Strict");
        Assert.Contains("Strict/Thing.cs", strict.ChangedFiles);
        Assert.NotEmpty(strict.Causes);
    }

    [Fact]
    public async Task A_broad_change_in_a_small_project_names_itself_when_whole()
    {
        await using var engine = await InProcessEngine.StartAsync();
        engine.Repo.Replace("Lib/Calc.cs", "public class Calc", "public sealed class Calc");
        await engine.SyncAsync("Lib/Calc.cs");

        var reach = Assert.IsType<Reach.Broad>(await engine.ReachAsync("Lib/Calc.cs"));

        Assert.NotNull(reach.BroadCauses);
        Assert.Contains(reach.BroadCauses, b => b.File == engine.Repo.PathOf("Lib/Calc.cs"));
    }

    [Fact]
    public async Task Two_broad_targets_both_appear()
    {
        await using var engine = await InProcessEngine.StartAsync();
        engine.Repo.Replace("Lib/Calc.cs", "public class Calc", "public sealed class Calc");
        engine.Repo.Replace("Lib/Greeting.cs", "public interface IGreeter", "public interface IGreeter : System.IDisposable");
        await engine.SyncAsync("Lib/Calc.cs", "Lib/Greeting.cs");

        var reach = Assert.IsType<Reach.Broad>(await engine.ReachAsync("Lib/Calc.cs", "Lib/Greeting.cs"));

        Assert.NotNull(reach.BroadCauses);
        Assert.Contains(reach.BroadCauses, b => b.File == engine.Repo.PathOf("Lib/Calc.cs"));
        Assert.Contains(reach.BroadCauses, b => b.File == engine.Repo.PathOf("Lib/Greeting.cs"));
    }

    [Fact]
    public async Task An_incremental_check_names_no_whole_project_cause()
    {
        await using var engine = await InProcessEngine.StartAsync();
        engine.Repo.Replace("Lib/Calc.cs", "public int Add(", "public int Plus(");

        var report = await engine.CheckAsync("Lib/Calc.cs");

        Assert.False(report.CheckedWholeProjects);
        Assert.Empty(report.WholeProjectCauses);
    }

    [Fact]
    public async Task A_precise_reach_records_which_target_declared_each_cause()
    {
        await using var engine = await InProcessEngine.StartAsync();
        engine.Repo.Replace("Lib/Calc.cs", "public int Add(", "public int Plus(");
        await engine.CheckAsync("Lib/Calc.cs");

        var precise = Assert.IsType<Reach.Precise>(await engine.ReachAsync("Lib/Calc.cs"));

        Assert.NotNull(precise.CauseFiles);
        Assert.Contains(precise.CauseFiles.Values, v => v == engine.Repo.PathOf("Lib/Calc.cs"));
    }

    private static async Task<InProcessEngine> StartWithAsync(params (string Path, string Content)[] files)
    {
        var repo = FixtureRepo.CreateStandard();
        foreach (var (path, content) in files)
            repo.Write(path, content);
        repo.Commit("fixture files");
        return await InProcessEngine.StartAsync(repo);
    }
}
